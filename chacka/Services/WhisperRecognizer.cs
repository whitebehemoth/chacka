using System.IO;
using System.Text;
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

    public async Task<string> RecognizeAsync(float[] samples, string language = "en")
    {
        if (_factory == null) return string.Empty;

        await _semaphore.WaitAsync();
        try
        {
            // Настройки под чистый звук с митингов
            await using var processor = _factory.CreateBuilder()
                .WithLanguage(language)
                .WithEntropyThreshold(2.4f) // Помогает бороться с зацикливанием пауз
                .WithTemperature(0.1f)      // Детерминированный результат (ускоряет работу на чистом аудио)
                .WithThreads(Environment.ProcessorCount / 2) // Оставляем ресурсы приложению
                .Build();

            var sb = new StringBuilder();
            
            // Whisper.net умеет работать напрямую с float[], избегая создания WAV!
            await foreach (var segment in processor.ProcessAsync(samples))
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

    public async Task<string> RecognizeFromWaveFileAsync(string path, string language = "en")
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path must be provided", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found", path);

        if (_factory == null) return string.Empty;

        await _semaphore.WaitAsync();
        try
        {
            await using var processor = _factory.CreateBuilder()
                .WithLanguage(language)
                .WithEntropyThreshold(2.4f)
                .WithTemperature(0.0f)
                .Build();

            await using var stream = File.OpenRead(path);
            
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(stream))
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

    public void Dispose()
    {
        _factory?.Dispose();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
