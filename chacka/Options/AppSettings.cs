namespace chacka.Options;

public class AppSettings
{
    public double ChunkDurationSeconds { get; set; } = 5.0;
    public double PauseDurationSeconds { get; set; } = 0.8;
    public double MinChunkDurationBeforePauseFlushSeconds { get; set; } = 1.2;
    public float SpeechStartThreshold { get; set; } = 0.0030f;
    public float SpeechEndThreshold { get; set; } = 0.0015f;
    public double OutputFontSize { get; set; } = 13;
    public bool RecordAudioEnabled { get; set; } = false;
    public string UiLanguage { get; set; } = "en";
    public string DefaultSourceLanguage { get; set; } = "en";
    public string DefaultTargetLanguage { get; set; } = "ru";
    public string WhisperModelType { get; set; } = "base";
    public TranslationOptions Translation { get; set; } = new();
}
