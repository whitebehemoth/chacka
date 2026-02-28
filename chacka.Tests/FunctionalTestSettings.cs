#nullable enable

using System;
using System.IO;
using chacka.Options;

namespace chacka.Tests;

public class FunctionalTestSettings
{
    public string TextToTranslate { get; set; } = "The quick brown fox jumps over the lazy dog.";
    public string SourceLanguageName { get; set; } = "English";
    public string TargetLanguageName { get; set; } = "Russian";
    public string AudioFilePath { get; set; } = string.Empty;
    public string ModelsDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "chacka", "models");
    public string WhisperModelType { get; set; } = "base";
    public string RecognitionLanguageCode { get; set; } = "en";
    public TranslationOptions Translation { get; set; } = new();
}
