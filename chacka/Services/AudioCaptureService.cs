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
    private System.Threading.Timer? _chunkTimer;
    private float _lastPeak;
    private bool _isCapturing;

    /// <summary>Fired on a background thread with 16 kHz mono float32 samples.</summary>
    public event Action<float[]>? AudioChunkReady;
    public event Action<string>? StatusChanged;

    /// <summary>How many seconds of audio to buffer before flushing to STT.</summary>
    public double ChunkDurationSeconds { get; set; } = 5.0;

    public AudioCaptureService()
    {
        _device = GetDefaultDevice();
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

        lock (_lock) { _buffer = new MemoryStream(); }

        _chunkTimer = new System.Threading.Timer(
            OnChunkTimer, null,
            TimeSpan.FromSeconds(ChunkDurationSeconds),
            TimeSpan.FromSeconds(ChunkDurationSeconds));

        _capture.StartRecording();
        _isCapturing = true;
        StatusChanged?.Invoke($"Capturing: {_device.FriendlyName}");
    }

    public void Stop()
    {
        _chunkTimer?.Dispose();
        _chunkTimer = null;

        if (_capture != null)
        {
            _capture.StopRecording();
            _capture.Dispose();
            _capture = null;
        }

        FlushBuffer();
        _isCapturing = false;
        StatusChanged?.Invoke("Stopped");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        lock (_lock) { _buffer.Write(e.Buffer, 0, e.BytesRecorded); }

        // Update peak
        float peak = 0f;
        for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
        {
            float abs = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
            if (abs > peak) peak = abs;
        }
        _lastPeak = peak;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            StatusChanged?.Invoke($"Capture error: {e.Exception.Message}");
    }

    private void OnChunkTimer(object? state) => FlushBuffer();

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
            if (maxAbs < 0.002f) return;

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

        var result = new List<float>();
        var readBuffer = new float[16000];
        int read;
        while ((read = resampled.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                result.Add(readBuffer[i]);
        }

        return result.ToArray();
    }

    public string GetStatus()
    {
        string device = _device?.FriendlyName ?? "None";
        return _isCapturing
            ? $"Capturing: {device} | Peak: {_lastPeak:F4}"
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
        GC.SuppressFinalize(this);
    }
}
