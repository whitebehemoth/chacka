using System.IO;
using Whisper.net;
using Whisper.net.Ggml;

namespace chacka.Services;

public class WhisperRecognizer : IDisposable
{
    private WhisperFactory? _factory;
    private string? _modelPath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public event Action<string>? StatusChanged;

    public async Task InitializeAsync(string modelDir, GgmlType modelType = GgmlType.Base)
    {
        Directory.CreateDirectory(modelDir);
        _modelPath = Path.Combine(modelDir, $"ggml-{modelType.ToString().ToLowerInvariant()}.bin");

        if (!File.Exists(_modelPath))
        {
            StatusChanged?.Invoke($"Downloading Whisper {modelType} model…");
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(modelType);
            using var fileStream = File.Create(_modelPath);
            await modelStream.CopyToAsync(fileStream);
        }

        _factory = WhisperFactory.FromPath(_modelPath);
        StatusChanged?.Invoke($"Model loaded: {modelType}");
    }

    public bool IsLoaded => _factory != null;

    public async Task<string> RecognizeAsync(float[] samples, string language = "en")
    {
        return await RecognizeViaStreamAsync(() => CreateWavStream(samples, 16000), language);
    }

    public async Task<string> RecognizeFromWaveFileAsync(string path, string language = "en")
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path must be provided", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found", path);

        return await RecognizeViaStreamAsync(() => File.OpenRead(path), language);
    }

    private async Task<string> RecognizeViaStreamAsync(Func<Stream> streamFactory, string language)
    {
        if (_factory == null) return string.Empty;

        await _semaphore.WaitAsync();
        try
        {
            await using var processor = _factory.CreateBuilder()
                .WithLanguage(language)
                .Build();

            using var stream = streamFactory();

            var segments = new List<string>();
            await foreach (var segment in processor.ProcessAsync(stream))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                    segments.Add(segment.Text.Trim());
            }

            return string.Join(" ", segments);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Creates a 16-bit PCM WAV stream from 16 kHz mono float samples.
    /// </summary>
    private static MemoryStream CreateWavStream(float[] samples, int sampleRate)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

        int dataSize = samples.Length * sizeof(short);

        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);          // chunk size
        bw.Write((short)1);    // PCM format
        bw.Write((short)1);    // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * sizeof(short)); // byte rate
        bw.Write((short)sizeof(short));       // block align
        bw.Write((short)16);                  // bits per sample

        // data chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);

        foreach (float sample in samples)
        {
            float clamped = Math.Clamp(sample, -1f, 1f);
            bw.Write((short)(clamped * short.MaxValue));
        }

        bw.Flush();
        ms.Position = 0;
        return ms;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
