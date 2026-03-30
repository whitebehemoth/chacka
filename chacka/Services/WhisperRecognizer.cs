using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace chacka.Services;

public record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

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
            var tempModelPath = _modelPath + ".tmp"; // Загружаем во временный файл
            
            using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(modelType))
            using (var fileStream = File.Create(tempModelPath))
            {
                await modelStream.CopyToAsync(fileStream);
            }
            
            // Запись прошла успешно — атомарно переименовываем
            File.Move(tempModelPath, _modelPath, overwrite: true);
        }

        // Включаем оптимизацию для многопоточности (если нужно)
        _factory = WhisperFactory.FromPath(_modelPath);
        StatusChanged?.Invoke($"Model loaded: {modelType}");
    }

    public bool IsLoaded => _factory != null;

    public async Task<string> RecognizeAsync(float[] samples, string language = "en", float temperature = 0.0f, CancellationToken cancellationToken = default)
    {
        if (_factory == null) return string.Empty;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            // Настройки под чистый звук с митингов
            await using var processor = _factory.CreateBuilder()
                .WithLanguage(language)
                .WithEntropyThreshold(2.4f) // Помогает бороться с зацикливанием пауз
                .WithTemperature(temperature)      // Детерминированный результат (ускоряет работу на чистом аудио)
                .WithThreads(Environment.ProcessorCount / 2) // Оставляем ресурсы приложению
                .Build();

            var sb = new StringBuilder();
            
            // Whisper.net умеет работать напрямую с float[], избегая создания WAV!
            await foreach (var segment in processor.ProcessAsync(samples).WithCancellation(cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                    sb.Append(segment.Text); 
                    // Не тримим текст, Whisper.net сам расставит нужные пробелы перед словами
            }

            return sb.ToString().Trim();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<string> RecognizeFromWaveFileAsync(string path, string language = "en", float temperature = 0.0f, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path must be provided", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found", path);

        if (_factory == null) return string.Empty;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var processor = _factory.CreateBuilder()
                .WithLanguage(language)
                .WithEntropyThreshold(2.4f)
                .WithTemperature(temperature)
                .Build();

            await using var stream = File.OpenRead(path);
            
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(stream).WithCancellation(cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                    sb.Append(segment.Text);
            }

            return sb.ToString().Trim();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async IAsyncEnumerable<TranscriptSegment> ProcessFileStreamingAsync(
        float[] samples,
        string language = "en",
        float temperature = 0.0f,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_factory == null) yield break;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            // Beam Search + оптимизации для длинных записей
            await using var processor = _factory.CreateBuilder()
                .WithLanguage(language)
                .WithBeamSearchSamplingStrategy()
                .WithEntropyThreshold(2.4f)
                .WithTemperature(temperature)
                .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
                .Build();

            await foreach (var segment in processor.ProcessAsync(samples).WithCancellation(cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                    yield return new TranscriptSegment(segment.Start, segment.End, segment.Text.Trim());
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
