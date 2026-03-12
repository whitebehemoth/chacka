using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace chacka.Services;

public class AudioDeviceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public override string ToString() => Name;
}

public class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device;
    private readonly object _lock = new();
    private MemoryStream _buffer = new();
    private WaveFormat? _captureFormat;
    private float _lastPeak;
    private float _lastRms;
    private bool _isCapturing;
    private DateTime _lastSoundTime = DateTime.UtcNow;
    private bool _silenceFlushPending;
    private bool _inSpeech;
    private WaveFileWriter? _sessionWriter;
    private string? _sessionTempWavPath;
    private string? _lastRecordingPath;
    private readonly object _cleanupLock = new();
    private readonly HashSet<string> _pendingTempCleanup = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _cleanupTimer;

    /// <summary>Fired on a background thread with 16 kHz mono float32 samples.</summary>
    public event Action<float[]>? AudioChunkReady;
    public event Action<string>? StatusChanged;

    /// <summary>How many seconds of audio to buffer before flushing to STT.</summary>
    public double MaxChunkDurationSeconds { get; set; } = 10.0;

    /// <summary>How many seconds of silence must pass before flushing mid-chunk.</summary>
    public double PauseDurationSeconds { get; set; } = 0.8;

    /// <summary>Minimum chunk duration before pause-based flush is allowed.</summary>
    public double MinChunkDurationBeforePauseFlushSeconds { get; set; } = 1.2;

    /// <summary>Amplitude below which audio is considered silence.</summary>
    public float SilenceThreshold { get; set; } = 0.002f;

    /// <summary>RMS threshold for entering speech state.</summary>
    public float SpeechStartThreshold { get; set; } = 0.0030f;

    /// <summary>RMS threshold for leaving speech state (hysteresis).</summary>
    public float SpeechEndThreshold { get; set; } = 0.0015f;

    /// <summary>Record full captured session into a single MP3 file between Start/Stop.</summary>
    public bool SessionRecordingEnabled { get; set; }
    public static string RecordingsDirectory { get; set; } = "";
    public string? LastRecordingPath => _lastRecordingPath;
    public bool IsCapturing => _isCapturing;

    public AudioCaptureService()
    {
        _device = GetDefaultDevice();
    }

    public static string GetRecordsDirectory()
    {
        return !string.IsNullOrWhiteSpace(RecordingsDirectory)
            ? RecordingsDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Chacka meeting recordings");
    }

    public static void CleanupStaleTempFiles()
    {
        string dir = GetRecordsDirectory();
        if (!Directory.Exists(dir)) return;

        foreach (string file in Directory.EnumerateFiles(dir, "tmp-*.wav"))
        {
            try { File.Delete(file); }
            catch { }
        }
    }

    public static List<AudioDeviceInfo> ListRenderDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName })
            .ToList();
    }

    public void SwitchDevice(string deviceId)
    {
        bool wasCapturing = _isCapturing;
        if (wasCapturing) Stop();

        var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(deviceId);
        StatusChanged?.Invoke($"Switched to: {_device.FriendlyName}");

        if (wasCapturing) Start();
    }

    public void Start()
    {
        if (_isCapturing || _device == null) return;

        _capture = new WasapiLoopbackCapture(_device);
        _captureFormat = _capture.WaveFormat;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        StartSessionRecording();

        lock (_lock) { _buffer = new MemoryStream(); }

        _lastSoundTime = DateTime.UtcNow;

        _inSpeech = false;

        _capture.StartRecording();
        _isCapturing = true;
        StatusChanged?.Invoke($"Capturing: {_device.FriendlyName}");
    }

    public void Stop()
    {
        if (_capture != null)
        {
            _capture.StopRecording();
            _capture.Dispose();
            _capture = null;
        }

        FlushBuffer();
        StopSessionRecording();
        _silenceFlushPending = false;
        _isCapturing = false;
        StatusChanged?.Invoke("Stopped");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        double bufferedSeconds;
        lock (_lock)
        {
            _buffer.Write(e.Buffer, 0, e.BytesRecorded);
            _sessionWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            bufferedSeconds = GetBufferedSecondsNoLock();
        }

        // Высчитываем пики и RMS текущего фрейма
        float peak = 0f;
        for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
        {
            float abs = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
            if (abs > peak) peak = abs;
        }
        _lastPeak = peak;
        _lastRms = ComputeRms(e.Buffer, e.BytesRecorded);

        // Логика VAD (определение пауз) и Максимальной длины
        var now = DateTime.UtcNow;
        bool speechDetected = _inSpeech ? _lastRms >= SpeechEndThreshold : _lastRms >= SpeechStartThreshold;

        // Считаем ChunkDurationSeconds не жестким таймером, а "максимальной длиной до принудительного сброса", 
        // чтобы текст спикера, который говорит без пауз 15-20 секунд, все равно выводился на экран.
        bool forceMaxDurationFlush = bufferedSeconds >= MaxChunkDurationSeconds;

        if (speechDetected)
        {
            _inSpeech = true;
            _lastSoundTime = now;
        }
        else
        {
            // Проверяем, достаточно ли длилась пауза
            bool pauseExceeded = (now - _lastSoundTime).TotalSeconds >= PauseDurationSeconds;

            if (_inSpeech && pauseExceeded && bufferedSeconds >= MinChunkDurationBeforePauseFlushSeconds)
            {
                // Сработал сброс по натуральной паузе
                _inSpeech = false;
                FlushBuffer();
                return; // Выходим, буфер сброшен
            }
        }

        // Если человек говорит без остановок дольше максимума, режем принудительно, 
        // или если копилась тишина дольше ChunkDurationSeconds времени
        if (forceMaxDurationFlush)
        {
            _inSpeech = false;
            _lastSoundTime = now;
            FlushBuffer();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            StatusChanged?.Invoke($"Capture error: {e.Exception.Message}");
    }

    private void FlushBuffer()
    {
        byte[] data;
        lock (_lock)
        {
            data = _buffer.ToArray();
            _buffer = new MemoryStream();
        }

        if (data.Length == 0 || _captureFormat == null) return;

        try
        {
            float[] samples = ResampleTo16kMono(data, _captureFormat);

            // Skip silence
            float maxAbs = 0f;
            foreach (float s in samples)
            {
                float a = Math.Abs(s);
                if (a > maxAbs) maxAbs = a;
            }
            if (maxAbs < SilenceThreshold) return;

            AudioChunkReady?.Invoke(samples);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Resample error: {ex.Message}");
        }
    }

    private static float[] ResampleTo16kMono(byte[] rawData, WaveFormat sourceFormat)
    {
        using var memStream = new MemoryStream(rawData);
        using var inputStream = new RawSourceWaveStream(memStream, sourceFormat);
        var sampleProvider = inputStream.ToSampleProvider();

        ISampleProvider mono = sourceFormat.Channels > 1
            ? new StereoToMonoSampleProvider(sampleProvider)
            : sampleProvider;

        var resampled = new WdlResamplingSampleProvider(mono, 16000);

        // Предполагаемый размер 16кгц буфера (с небольшим запасом)
        // Формула: (длина_байт / байт_в_секунду) * 16000
        int estimatedSamples = (int)((rawData.Length / (double)sourceFormat.AverageBytesPerSecond) * 16000) + 1000;
        
        var result = new List<float>(estimatedSamples); // Избавляемся от тысяч аллокаций памяти (memory leaks)
        var readBuffer = new float[16000];
        int read;
        
        while ((read = resampled.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            // Специальный оптимизированный метод с .NET 8 / C# 12+ (если используете)
            // Иначе можно использовать AddRange или просто оставить как есть, List capacity не даст утечек
            result.AddRange(new ReadOnlySpan<float>(readBuffer, 0, read));
        }

        return result.ToArray();
    }

    private static float ComputeRms(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded <= 0) return 0f;

        double sumSquares = 0;
        int samples = 0;
        for (int i = 0; i + 4 <= bytesRecorded; i += 4)
        {
            float value = BitConverter.ToSingle(buffer, i);
            sumSquares += value * value;
            samples++;
        }

        return samples == 0 ? 0f : (float)Math.Sqrt(sumSquares / samples);
    }

    private double GetBufferedSecondsNoLock()
    {
        return _captureFormat == null
            ? 0
            : (double)_buffer.Length / _captureFormat.AverageBytesPerSecond;
    }

    private void StartSessionRecording()
    {
        _lastRecordingPath = null;
        _sessionWriter?.Dispose();
        _sessionWriter = null;
        _sessionTempWavPath = null;

        if (!SessionRecordingEnabled || _captureFormat == null)
            return;

        try
        {
            string recordsDir = GetRecordsDirectory();
            Directory.CreateDirectory(recordsDir);

            _sessionTempWavPath = Path.Combine(recordsDir, $"tmp-{Guid.NewGuid():N}.wav");
            _sessionWriter = new WaveFileWriter(_sessionTempWavPath, _captureFormat);
        }
        catch (Exception ex)
        {
            _sessionWriter = null;
            _sessionTempWavPath = null;
            StatusChanged?.Invoke($"Record init error: {ex.Message}");
        }
    }

    private void StopSessionRecording()
    {
        string? tempWavPath;
        lock (_lock)
        {
            _sessionWriter?.Dispose();
            _sessionWriter = null;
            tempWavPath = _sessionTempWavPath;
            _sessionTempWavPath = null;
        }

        if (string.IsNullOrWhiteSpace(tempWavPath) || !File.Exists(tempWavPath))
            return;

        try
        {
            string recordsDir = Path.GetDirectoryName(tempWavPath)!;
            string mp3Path = Path.Combine(recordsDir, $"meeting-{DateTime.Now:yyyyMMdd-HHmmss}.mp3");

            using var reader = new WaveFileReader(tempWavPath);
            MediaFoundationEncoder.EncodeToMp3(reader, mp3Path);

            _lastRecordingPath = mp3Path;

            TryDeleteFileWithRetry(tempWavPath);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Record save error: {ex.Message}");
        }
    }

    private void TryDeleteFileWithRetry(string filePath)
    {
        for (int i = 0; i < 8; i++)
        {
            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(80);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(80);
            }
        }

        ScheduleTempCleanup(filePath);
        StatusChanged?.Invoke($"Temp file is locked. Scheduled cleanup: {filePath}");
    }

    private void ScheduleTempCleanup(string filePath)
    {
        lock (_cleanupLock)
        {
            _pendingTempCleanup.Add(filePath);
            _cleanupTimer ??= new System.Threading.Timer(OnCleanupTimer, null,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    private void OnCleanupTimer(object? state)
    {
        List<string> files;
        lock (_cleanupLock)
        {
            files = _pendingTempCleanup.ToList();
        }

        foreach (string file in files)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);

                lock (_cleanupLock)
                {
                    _pendingTempCleanup.Remove(file);
                }
            }
            catch
            {
            }
        }

        lock (_cleanupLock)
        {
            if (_pendingTempCleanup.Count == 0)
            {
                _cleanupTimer?.Dispose();
                _cleanupTimer = null;
            }
        }
    }

    private void FlushPendingCleanup()
    {
        OnCleanupTimer(null);
        CleanupStaleTempFiles();

        lock (_cleanupLock)
        {
            _cleanupTimer?.Dispose();
            _cleanupTimer = null;
            _pendingTempCleanup.Clear();
        }
    }

    public string GetStatus()
    {
        string device = _device?.FriendlyName ?? "None";
        return _isCapturing
            ? $"Capturing: {device} | Peak: {_lastPeak:F4} | Rms: {_lastRms:F4}"
            : $"Stopped | Device: {device}";
    }

    private static MMDevice? GetDefaultDevice()
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (devices.Count == 0) return null;

        return devices.FirstOrDefault(d =>
            d.FriendlyName.Contains("Speakers", StringComparison.OrdinalIgnoreCase)) ?? devices[0];
    }

    public void Dispose()
    {
        Stop();
        FlushPendingCleanup();
        GC.SuppressFinalize(this);
    }
}
