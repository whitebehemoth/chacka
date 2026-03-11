namespace chacka.Options;

public class AppSettings
{
    public double MaxChunkDurationSeconds { get; set; } = 20.0;
    public double PauseDurationSeconds { get; set; } = 0.8;
    public double MinChunkDurationBeforePauseFlushSeconds { get; set; } = 1.2;
    public float SpeechStartThreshold { get; set; } = 0.0030f;
    public float SpeechEndThreshold { get; set; } = 0.0015f;
    public double OutputFontSize { get; set; } = 13;
    public bool RecordAudioEnabled { get; set; } = false;
    public string UiLanguage { get; set; } = "en";
    public string DefaultSourceLanguage { get; set; } = "fr";
    public string DefaultTargetLanguage { get; set; } = "ru";
    public string DefaultTranslationLlm { get; set; } = "";
    public string WhisperModelType { get; set; } = "small";
    public float WhisperTemperature { get; set; } = 0.0f;
    public Dictionary<string, string> SupportedLanguages { get; set; } = new()
    {
        { "ru", "Russian" },
        { "en", "English" },
        { "fr", "French" }
    };
    public Dictionary<string, TranslationOptions> Translation { get; set; } = new();
}
