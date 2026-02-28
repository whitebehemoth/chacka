using System;
using System.IO;
using System.Threading.Tasks;
using chacka.Options;
using chacka.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Whisper.net.Ggml;

namespace chacka.Tests;

[TestClass]
public class FunctionalTests
{
    private static readonly FunctionalTestSettings Settings = LoadSettings();

    [TestMethod]
    public async Task Translate_SmallText()
    {
        var translator = new TranslationService(Settings.Translation);

        var result = await translator.TranslateAsync(Settings.TextToTranslate,
            Settings.SourceLanguageName,
            Settings.TargetLanguageName);

        Assert.IsFalse(string.IsNullOrWhiteSpace(result), "Translation result should not be empty.");
    }

    [TestMethod]
    public async Task Recognize_ShortAudio()
    {
        if (string.IsNullOrWhiteSpace(Settings.AudioFilePath))
        {
            Assert.Inconclusive("Set AudioFilePath in appsettings.functional.json to run recognition tests.");
        }

        if (!File.Exists(Settings.AudioFilePath))
        {
            Assert.Inconclusive("Audio file path configured in appsettings.functional.json does not exist.");
        }

        var recognizer = new WhisperRecognizer();
        var modelDir = string.IsNullOrWhiteSpace(Settings.ModelsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "chacka", "models")
            : Settings.ModelsDirectory;
        var modelType = Enum.TryParse<GgmlType>(Settings.WhisperModelType, true, out var parsed)
            ? parsed
            : GgmlType.Base;

        await recognizer.InitializeAsync(modelDir, modelType);

        var transcript = await recognizer.RecognizeFromWaveFileAsync(Settings.AudioFilePath,
            Settings.RecognitionLanguageCode);

        Assert.IsFalse(string.IsNullOrWhiteSpace(transcript), "Transcript should contain recognized text.");
    }

    private static FunctionalTestSettings LoadSettings()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.functional.json", optional: true)
            .Build();

        return config.Get<FunctionalTestSettings>() ?? new FunctionalTestSettings();
    }
}
