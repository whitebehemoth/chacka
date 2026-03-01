using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using chacka.Options;
using chacka.Services;
using Microsoft.Extensions.Configuration;
using Whisper.net.Ggml;

namespace chacka;

public record LanguageInfo(string Code, string Name);

public partial class MainWindow : Window
{
    private sealed record UiLangOption(string Code, string Name);

    private readonly AppSettings _settings;
    private readonly AudioCaptureService _capture = new();
    private readonly WhisperRecognizer _recognizer = new();
    private readonly TranslationService _translator;
    private readonly LanguageInfo[] _languages;
    private readonly UiLangOption[] _uiLanguages =
    [
        new("en", "EN"),
        new("ru", "RU")
    ];
    private readonly ConcurrentQueue<float[]> _pendingChunks = new();
    private int _processingQueue;
    private bool _suppressUiEvents = true;
    private const double MinUiFontSize = 8;
    private const double MaxUiFontSize = 24;

    public MainWindow()
    {
        InitializeComponent();

        AudioCaptureService.CleanupStaleTempFiles();

        _settings = App.Configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();
        _capture.ChunkDurationSeconds = _settings.ChunkDurationSeconds;
        _capture.PauseDurationSeconds = _settings.PauseDurationSeconds;
        _capture.MinChunkDurationBeforePauseFlushSeconds = _settings.MinChunkDurationBeforePauseFlushSeconds;
        _capture.SpeechStartThreshold = _settings.SpeechStartThreshold;
        _capture.SpeechEndThreshold = _settings.SpeechEndThreshold;
        _capture.SessionRecordingEnabled = _settings.RecordAudioEnabled;
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
        InitializeSettingsUi();
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
        _capture.AudioChunkReady += OnAudioChunkReady;
        _capture.RecordingSaved += path =>
            Dispatcher.Invoke(() => UpdateRecordingPath(path));
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
                MessageBox.Show(this, ex.Message, "Model error", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private void InitializeSettingsUi()
    {
        _suppressUiEvents = true;

        UiLanguageCombo.ItemsSource = _uiLanguages;
        UiLanguageCombo.DisplayMemberPath = nameof(UiLangOption.Name);
        UiLanguageCombo.SelectedValuePath = nameof(UiLangOption.Code);
        UiLanguageCombo.SelectedValue = _settings.UiLanguage is "ru" ? "ru" : "en";

        RecordAudioCheckBox.IsChecked = _settings.RecordAudioEnabled;
        RecordAudioCheckBox.IsEnabled = !_capture.IsCapturing;
        UpdateRecordIndicator();

        PauseDurationSlider.Value = _settings.PauseDurationSeconds;
        MinChunkBeforePauseSlider.Value = _settings.MinChunkDurationBeforePauseFlushSeconds;
        SpeechStartThresholdSlider.Value = _settings.SpeechStartThreshold;
        SpeechEndThresholdSlider.Value = _settings.SpeechEndThreshold;
        if (_settings.OutputFontSize is < MinUiFontSize or > MaxUiFontSize)
            _settings.OutputFontSize = 13;

        ApplyOutputFontSize(_settings.OutputFontSize);
        ApplyUiLanguage(_settings.UiLanguage);
        UpdateRecordingPath(_capture.LastRecordingPath);
        UpdateSettingsLabels();
        _suppressUiEvents = false;
    }

    private void SettingsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeechStartThresholdSlider is null || SpeechEndThresholdSlider is null ||
            PauseDurationSlider is null || MinChunkBeforePauseSlider is null)
            return;

        if (_suppressUiEvents)
            return;

        _suppressUiEvents = true;
        if (SpeechEndThresholdSlider.Value >= SpeechStartThresholdSlider.Value)
        {
            if (ReferenceEquals(sender, SpeechEndThresholdSlider))
                SpeechEndThresholdSlider.Value = Math.Max(SpeechEndThresholdSlider.Minimum, SpeechStartThresholdSlider.Value - 0.0005);
            else
                SpeechStartThresholdSlider.Value = Math.Min(SpeechStartThresholdSlider.Maximum, SpeechEndThresholdSlider.Value + 0.0005);
        }

        _settings.PauseDurationSeconds = PauseDurationSlider.Value;
        _settings.MinChunkDurationBeforePauseFlushSeconds = MinChunkBeforePauseSlider.Value;
        _settings.SpeechStartThreshold = (float)SpeechStartThresholdSlider.Value;
        _settings.SpeechEndThreshold = (float)SpeechEndThresholdSlider.Value;

        _capture.PauseDurationSeconds = _settings.PauseDurationSeconds;
        _capture.MinChunkDurationBeforePauseFlushSeconds = _settings.MinChunkDurationBeforePauseFlushSeconds;
        _capture.SpeechStartThreshold = _settings.SpeechStartThreshold;
        _capture.SpeechEndThreshold = _settings.SpeechEndThreshold;

        UpdateSettingsLabels();
        SaveUserSettings();
        _suppressUiEvents = false;
    }

    private void OutputText_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        e.Handled = true;
        double next = _settings.OutputFontSize + (e.Delta > 0 ? 1 : -1);
        next = Math.Clamp(next, MinUiFontSize, MaxUiFontSize);
        if (Math.Abs(next - _settings.OutputFontSize) < 0.0001)
            return;

        _settings.OutputFontSize = next;
        ApplyOutputFontSize(next);
        SaveUserSettings();
    }

    private void ApplyOutputFontSize(double size)
    {
        TranscriptBox.FontSize = size;
        TranslationBox.FontSize = size;
        FontSizeValueText.Text = size.ToString("F0");
    }

    private void UpdateSettingsLabels()
    {
        PauseDurationValueText.Text = PauseDurationSlider.Value.ToString("F2");
        MinChunkBeforePauseValueText.Text = MinChunkBeforePauseSlider.Value.ToString("F2");
        SpeechStartValueText.Text = SpeechStartThresholdSlider.Value.ToString("F4");
        SpeechEndValueText.Text = SpeechEndThresholdSlider.Value.ToString("F4");
    }

    private void UiLanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        string lang = (UiLanguageCombo.SelectedValue as string) is "ru" ? "ru" : "en";
        _settings.UiLanguage = lang;
        ApplyUiLanguage(lang);
        SaveUserSettings();
    }

    private void RecordAudioCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        _settings.RecordAudioEnabled = RecordAudioCheckBox.IsChecked == true;
        _capture.SessionRecordingEnabled = _settings.RecordAudioEnabled;
        UpdateRecordIndicator();
        SaveUserSettings();
    }

    private void UpdateRecordIndicator()
    {
        bool enabled = RecordAudioCheckBox.IsChecked == true;
        RecordIndicator.Foreground = enabled ? Brushes.Red : Brushes.Gray;
        RecordIndicator.ToolTip = _settings.UiLanguage == "ru"
            ? (enabled ? "Запись включена" : "Запись выключена")
            : (enabled ? "Recording enabled" : "Recording disabled");
    }

    private void UpdateRecordingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            RecordPathText.Text = "-";
            RecordPathText.ToolTip = _settings.UiLanguage == "ru"
                ? "Запись ещё не сохранена"
                : "No recording saved yet";
            return;
        }

        RecordPathText.Text = Path.GetFileName(path);
        RecordPathText.ToolTip = path;
    }

    private void ApplyUiLanguage(string lang)
    {
        bool ru = lang == "ru";
        string recordsDir = AudioCaptureService.GetRecordsDirectory();

        Title = ru ? "Переводчик для митингов" : "Meeting Live Translator";

        DeviceLabel.Text = ru ? "Устройство:" : "Device:";
        RefreshDevicesBtn.ToolTip = ru ? "Обновить список устройств" : "Refresh audio devices";
        DevicesCombo.ToolTip = ru ? "Источник системного звука для распознавания" : "System audio source for recognition";

        StartBtn.Content = ru ? "▶ Старт" : "▶ Start";
        StartBtn.ToolTip = ru ? "Начать захват и распознавание" : "Start capture and recognition";
        StopBtn.Content = ru ? "⏹ Стоп" : "⏹ Stop";
        StopBtn.ToolTip = ru ? "Остановить захват" : "Stop capture";

        RecordAudioCheckBox.Content = ru ? "Запись" : "Record";
        RecordAudioCheckBox.ToolTip = ru
            ? $"Сохранять весь аудиопоток между Старт/Стоп в один MP3. Папка: {recordsDir}"
            : $"Save all captured audio between Start/Stop into one MP3. Folder: {recordsDir}";
        OpenRecordFolderBtn.ToolTip = ru
            ? $"Открыть папку записей: {recordsDir}"
            : $"Open recordings folder: {recordsDir}";
        UpdateRecordIndicator();

        FromLabel.Text = ru ? "Из:" : "From:";
        ToLabel.Text = ru ? "В:" : "To:";
        UiLangLabel.Text = ru ? "Язык UI:" : "UI:";
        SourceLangCombo.ToolTip = ru ? "Язык оригинальной речи" : "Source speech language";
        TargetLangCombo.ToolTip = ru ? "Язык перевода" : "Translation target language";
        UiLanguageCombo.ToolTip = ru ? "Язык интерфейса" : "Interface language";

        PauseDurationLabel.Text = ru ? "Пауза(с):" : "Pause(s):";
        PauseDurationSlider.ToolTip = ru
            ? "Pause duration - Длина тишины перед завершением фразы. Увеличьте, если фразы режутся рано."
            : "Pause duration - Silence required to close phrase. Increase if phrases are cut too early.";

        MinChunkLabel.Text = ru ? "Мин. чанк(с):" : "Min chunk(s):";
        MinChunkBeforePauseSlider.ToolTip = ru
            ? "Minimum chunk duration - Минимальная длина чанка до срабатывания паузы."
            : "Minimum chunk duration - Minimal chunk length before pause flush is allowed.";

        SpeechStartLabel.Text = ru ? "Порог старта:" : "Start thr:";
        SpeechStartThresholdSlider.ToolTip = ru
            ? "Speech start threshold - Порог начала речи. Повысьте, если шум запускает ложные фразы."
            : "Speech start threshold - Speech start sensitivity. Raise if noise starts false phrases.";

        SpeechEndLabel.Text = ru ? "Порог конца:" : "End thr:";
        SpeechEndThresholdSlider.ToolTip = ru
            ? "Speech end threshold - Порог конца речи. Понизьте, если в шуме плохо определяется конец фразы."
            : "Speech end threshold - End-of-speech sensitivity. Lower when too noisy to capture end of phrase.";

        FontHintLabel.Text = ru ? "Шрифт Ctrl+Wheel:" : "Font Ctrl+Wheel:";
        TranscriptHeader.Text = ru ? "Оригинал" : "Transcript (source)";
        TranslationHeader.Text = ru ? "Перевод" : "Translation";

        TranscriptBox.ToolTip = ru
            ? "Ctrl + колесо: изменить размер шрифта"
            : "Ctrl + mouse wheel: change font size";
        TranslationBox.ToolTip = TranscriptBox.ToolTip;

        RecordPathLabel.Text = ru ? "| Файл:" : "| Save:";
        RecordPathLabel.ToolTip = ru ? $"Папка записей: {recordsDir}" : $"Recordings folder: {recordsDir}";

        UpdateRecordingPath(_capture.LastRecordingPath);
    }

    private void OpenRecordFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        string dir = AudioCaptureService.GetRecordsDirectory();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    private void SaveUserSettings()
    {
        var root = new
        {
            AppSettings = new
            {
                _settings.ChunkDurationSeconds,
                _settings.PauseDurationSeconds,
                _settings.MinChunkDurationBeforePauseFlushSeconds,
                _settings.SpeechStartThreshold,
                _settings.SpeechEndThreshold,
                _settings.DefaultSourceLanguage,
                _settings.DefaultTargetLanguage,
                _settings.WhisperModelType,
                _settings.OutputFontSize,
                _settings.RecordAudioEnabled,
                _settings.UiLanguage
            }
        };

        string dir = Path.GetDirectoryName(App.UserSettingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(App.UserSettingsPath, JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
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
                        string translated = await _translator.TranslateAsync(
                            text, sourceFullName, targetLang.Name);

                        Dispatcher.Invoke(() =>
                        {
                            TranslationBox.AppendText(translated + Environment.NewLine);
                            TranslationBox.ScrollToEnd();
                        });
                    }
                }

            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
                MessageBox.Show(this, ex.Message, "Processing error", MessageBoxButton.OK, MessageBoxImage.Error));
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

        _capture.SessionRecordingEnabled = RecordAudioCheckBox.IsChecked == true;

        _capture.Start();
        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        RecordAudioCheckBox.IsEnabled = false;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _capture.Stop();
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        RecordAudioCheckBox.IsEnabled = true;
        UpdateRecordingPath(_capture.LastRecordingPath);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveUserSettings();
        _capture.Dispose();
        AudioCaptureService.CleanupStaleTempFiles();
        _recognizer.Dispose();
    }
}