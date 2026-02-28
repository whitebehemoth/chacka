namespace chacka.Options;

public class AppSettings
{
    public double ChunkDurationSeconds { get; set; } = 5.0;
    public string DefaultSourceLanguage { get; set; } = "en";
    public string DefaultTargetLanguage { get; set; } = "ru";
    public string WhisperModelType { get; set; } = "base";
    public TranslationOptions Translation { get; set; } = new();
}
