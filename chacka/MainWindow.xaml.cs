using System.IO;
using System.Collections.Concurrent;
using System.Windows;
using chacka.Options;
using chacka.Services;
using Microsoft.Extensions.Configuration;
using Whisper.net.Ggml;

namespace chacka;

public record LanguageInfo(string Code, string Name);

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AudioCaptureService _capture = new();
    private readonly WhisperRecognizer _recognizer = new();
    private readonly TranslationService _translator;
    private readonly LanguageInfo[] _languages;
    private readonly ConcurrentQueue<float[]> _pendingChunks = new();
    private int _processingQueue;

    public MainWindow()
    {
        InitializeComponent();

        _settings = App.Configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();
        _capture.ChunkDurationSeconds = _settings.ChunkDurationSeconds;
        _capture.PauseDurationSeconds = _settings.PauseDurationSeconds;
        _capture.MinChunkDurationBeforePauseFlushSeconds = _settings.MinChunkDurationBeforePauseFlushSeconds;
        _capture.SpeechStartThreshold = _settings.SpeechStartThreshold;
        _capture.SpeechEndThreshold = _settings.SpeechEndThreshold;
        _translator = new TranslationService(_settings.Translation);

        _languages = new[]
        {
            new LanguageInfo("en", "English"),
            new LanguageInfo("ru", "Russian"),
            new LanguageInfo("fr", "French"),
        };

        InitializeLanguages();
        LoadDevices();
        WireEvents();
        _ = InitializeWhisperAsync();
    }

    private void InitializeLanguages()
    {
        SourceLangCombo.ItemsSource = _languages;
        TargetLangCombo.ItemsSource = _languages;

        SourceLangCombo.SelectedItem = _languages.FirstOrDefault(l => l.Code == _settings.DefaultSourceLanguage)
            ?? _languages.First();
        TargetLangCombo.SelectedItem = _languages.FirstOrDefault(l => l.Code == _settings.DefaultTargetLanguage)
            ?? _languages.Skip(1).FirstOrDefault() ?? _languages.First();
    }

    private void LoadDevices()
    {
        var devices = AudioCaptureService.ListRenderDevices();
        DevicesCombo.ItemsSource = devices;
        if (devices.Count > 0)
            DevicesCombo.SelectedIndex = 0;
    }

    private void WireEvents()
    {
        _capture.StatusChanged += msg =>
            Dispatcher.InvokeAsync(() => StatusText.Text = msg);

        _capture.AudioChunkReady += OnAudioChunkReady;

        _recognizer.StatusChanged += msg =>
            Dispatcher.InvokeAsync(() => ModelStatusText.Text = $"Model: {msg}");

        _translator.StatusChanged += msg =>
            Dispatcher.InvokeAsync(() => StatusText.Text = msg);
    }

    private async Task InitializeWhisperAsync()
    {
        try
        {
            var modelDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "chacka", "models");
            var modelTypeString = _settings.WhisperModelType ?? "base";
            var modelType = Enum.TryParse<GgmlType>(modelTypeString, true, out var parsed)
                ? parsed
                : GgmlType.Base;

            await _recognizer.InitializeAsync(modelDir, modelType);
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
                ModelStatusText.Text = $"Model error: {ex.Message}");
        }
    }

    private void OnAudioChunkReady(float[] samples)
    {
        _pendingChunks.Enqueue(samples);
        _ = ProcessChunkQueueAsync();
    }

    private async Task ProcessChunkQueueAsync()
    {
        if (Interlocked.Exchange(ref _processingQueue, 1) == 1)
            return;

        try
        {
            while (_pendingChunks.TryDequeue(out var samples))
            {
                string sourceLang = Dispatcher.Invoke(() =>
                    (SourceLangCombo.SelectedItem as LanguageInfo)?.Code ?? "en");

                LanguageInfo? targetLang = Dispatcher.Invoke(() =>
                    TargetLangCombo.SelectedItem as LanguageInfo);

                string sourceFullName = Dispatcher.Invoke(() =>
                    (SourceLangCombo.SelectedItem as LanguageInfo)?.Name ?? "English");

                Dispatcher.Invoke(() => StatusText.Text = "Recognizing…");

                string text = await _recognizer.RecognizeAsync(samples, sourceLang);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    Dispatcher.Invoke(() =>
                    {
                        TranscriptBox.AppendText(text + Environment.NewLine);
                        TranscriptBox.ScrollToEnd();
                    });

                    if (targetLang != null && targetLang.Code != sourceLang)
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "Translating…");

                        string translated = await _translator.TranslateAsync(
                            text, sourceFullName, targetLang.Name);

                        Dispatcher.Invoke(() =>
                        {
                            TranslationBox.AppendText(translated + Environment.NewLine);
                            TranslationBox.ScrollToEnd();
                        });
                    }
                }

                Dispatcher.Invoke(() => StatusText.Text = _capture.GetStatus());
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => StatusText.Text = $"Error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _processingQueue, 0);

            if (!_pendingChunks.IsEmpty)
                _ = ProcessChunkQueueAsync();
        }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesCombo.SelectedItem is AudioDeviceInfo device)
            _capture.SwitchDevice(device.Id);

        _capture.Start();
        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _capture.Stop();
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _capture.Dispose();
        _recognizer.Dispose();
    }
}