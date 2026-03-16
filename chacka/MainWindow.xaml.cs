using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private sealed record TranslationRequest(string Text, string SourceLanguageName, string TargetLanguageName);

    private readonly AppSettings _settings;
    private readonly AudioCaptureService _capture = new();
    private readonly WhisperRecognizer _recognizer = new();
    private ITranslationService _translator = new AzureTranslationService();
    private readonly LanguageInfo[] _languages;
    private readonly UiLangOption[] _uiLanguages =
    [
        new("en", "EN"),
        new("ru", "RU")
    ];
    private readonly ConcurrentQueue<float[]> _pendingChunks = new();
    private readonly ConcurrentQueue<TranslationRequest> _pendingTranslations = new();
    private int _processingQueue;
    private int _processingTranslations;
    private bool _suppressUiEvents = true;
    private const double MinUiFontSize = 8;
    private const double MaxUiFontSize = 24;
    private System.Windows.Threading.DispatcherTimer _sessionTimer = new();
    private DateTime _sessionStartTime;

    public MainWindow()
    {
        InitializeComponent();

        AudioCaptureService.CleanupStaleTempFiles();

        _settings = App.Configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();
        _capture.MaxChunkDurationSeconds = _settings.MaxChunkDurationSeconds;
        _capture.PauseDurationSeconds = _settings.PauseDurationSeconds;
        _capture.MinChunkDurationBeforePauseFlushSeconds = _settings.MinChunkDurationBeforePauseFlushSeconds;
        _capture.SpeechStartThreshold = _settings.SpeechStartThreshold;
        _capture.SpeechEndThreshold = _settings.SpeechEndThreshold;
        _capture.SilenceThreshold = _settings.SilenceThreshold;
        _capture.SessionRecordingEnabled = _settings.RecordAudioEnabled;
        _capture.VoiceCaptured += capture_StatusChanged;
        _capture.StatusChanged += app_StatusChanged;
        _recognizer.StatusChanged += app_StatusChanged;
        _translator.StatusChanged += app_StatusChanged;
        AudioCaptureService.RecordingsDirectory = _settings.RecordingsDirectory;

        _languages = new[]
        {
            new LanguageInfo("en", "English"),
            new LanguageInfo("ru", "Russian"),
            new LanguageInfo("fr", "French"),
        };

        InitializeLanguages();
        InitializeLlmCombo();
        InitializeWhisperCombo();
        LoadDevices();
        WireEvents();
        InitializeSettingsUi();

        _sessionTimer.Interval = TimeSpan.FromSeconds(1);
        _sessionTimer.Tick += (s, e) => 
        {
            SessionTimerText.Text = (DateTime.Now - _sessionStartTime).ToString(@"h\:mm\:ss");
        };

        _ = InitializeWhisperAsync();
    }

    private void app_StatusChanged(string obj)
    {
        Dispatcher.Invoke(() => 
            StatusLabel.Text = obj);
    }
    private void capture_StatusChanged(string obj)
    {
        Dispatcher.Invoke(() =>
            CapturedStatusLabel.Text = obj);
    }

    private void InitializeLanguages()
    {
        var languages = _settings.SupportedLanguages.Select(kvp => new LanguageInfo(kvp.Key, kvp.Value)).ToArray();
        SourceLangCombo.ItemsSource = languages;
        TargetLangCombo.ItemsSource = languages;

        SourceLangCombo.SelectedItem = languages.FirstOrDefault(l => l.Code == _settings.DefaultSourceLanguage)
            ?? languages.First();
        TargetLangCombo.SelectedItem = languages.FirstOrDefault(l => l.Code == _settings.DefaultTargetLanguage)
            ?? languages.Skip(1).FirstOrDefault() ?? languages.First();

    }

    private void InitializeLlmCombo()
    {
        LlmCombo.ItemsSource = _settings.Translation.Keys;
        var llm = _settings.Translation.Keys.FirstOrDefault(k => k == _settings.DefaultTranslationLlm)
            ?? _settings.Translation.Keys.FirstOrDefault();
        if (llm is null)
        {
            MessageBox.Show($"Translation LLM was not found, check the configuration or repair the app", "Corrypted config", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
        else
        {
            LlmCombo.SelectedItem = llm;
            UpdateTranslator(_settings.Translation[llm]);
        }
    }

    private void UpdateTranslator(TranslationOptions options)
    {
        if (_translator != null)
            _translator.StatusChanged -= app_StatusChanged;

        if (string.Equals(options.Provider, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            _translator = new AzureTranslationService();
        }
        else
        {
            _translator = new TranslationService(); // Default to OpenAI
        }

        _translator.StatusChanged += app_StatusChanged;
        _translator.UpdateOptions(options);
    }

    private void InitializeWhisperCombo()
    {
        foreach (System.Windows.Controls.ComboBoxItem item in WhisperModelCombo.Items)
        {
            if (item.Content.ToString() == _settings.WhisperModelType)
            {
                WhisperModelCombo.SelectedItem = item;
                break;
            }
        }
        if (WhisperModelCombo.SelectedItem == null && WhisperModelCombo.Items.Count > 0)
        {
            WhisperModelCombo.SelectedIndex = 1; // base
        }
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
        // _capture.RecordingSaved += path => { /* UI already shows save path but we might not need to update it as part of request, skipping original logic here */ };
        SourceLangCombo.SelectionChanged += LangCombo_SelectionChanged;
        TargetLangCombo.SelectionChanged += LangCombo_SelectionChanged;
    }

    private void LangCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateWhisperModeIndicator();
    }

    private void UpdateWhisperModeIndicator()
    {
        var sourceLang = (SourceLangCombo.SelectedItem as LanguageInfo)?.Code;
        var targetLang = (TargetLangCombo.SelectedItem as LanguageInfo)?.Code;

        if (sourceLang == targetLang && sourceLang != null)
        {
            WhisperModeText.Visibility = Visibility.Visible;
        }
        else
        {
            WhisperModeText.Visibility = Visibility.Collapsed;
        }
    }

    private async Task InitializeWhisperAsync()
    {
        try
        {
            var modelDir = Path.Combine("models");
            var modelTypeString = _settings.WhisperModelType ?? "base";
            var modelType = Enum.TryParse<GgmlType>(modelTypeString, true, out var parsed)
                ? parsed
                : GgmlType.Medium;

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
        UpdateActiveRecordIndicator();

        PauseDurationSlider.Value = _settings.PauseDurationSeconds;
        MinChunkBeforePauseSlider.Value = _settings.MinChunkDurationBeforePauseFlushSeconds;
        SpeechStartThresholdSlider.Value = _settings.SpeechStartThreshold;
        SpeechEndThresholdSlider.Value = _settings.SpeechEndThreshold;
        SilenceThresholdSlider.Value = _settings.SilenceThreshold;

        WhisperTempSlider.Value = _settings.WhisperTemperature;
        
        if (LlmCombo.SelectedItem != null && _settings.Translation.TryGetValue(LlmCombo.SelectedItem.ToString()!, out var llmOptions))
        {
            LlmTempSlider.Value = llmOptions.Temperature;
        }

        if (_settings.OutputFontSize is < MinUiFontSize or > MaxUiFontSize)
            _settings.OutputFontSize = 13;

        MenuMainToolbar.IsChecked = _settings.MainToolbarVisible;
        MenuAudioToolbar.IsChecked = _settings.AudioToolbarVisible;
        MenuLlmsToolbar.IsChecked = _settings.LlmToolbarVisible;

        ApplyToolbarVisibility();

        ApplyOutputFontSize(_settings.OutputFontSize);
        ApplyUiLanguage(_settings.UiLanguage);
        //UpdateRecordingPath(_capture.LastRecordingPath);
        UpdateSettingsLabels();
        UpdateWhisperModeIndicator();
        _suppressUiEvents = false;
    }

    private void SettingsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents)
            return;

        _suppressUiEvents = true;
        //if (SpeechEndThresholdSlider.Value >= SpeechStartThresholdSlider.Value)
        //{
        //    if (ReferenceEquals(sender, SpeechEndThresholdSlider))
        //        SpeechEndThresholdSlider.Value = Math.Max(SpeechEndThresholdSlider.Minimum, SpeechStartThresholdSlider.Value - 0.0005);
        //    else
        //        SpeechStartThresholdSlider.Value = Math.Min(SpeechStartThresholdSlider.Maximum, SpeechEndThresholdSlider.Value + 0.0005);
        //}

        _settings.PauseDurationSeconds = PauseDurationSlider.Value;
        _settings.MinChunkDurationBeforePauseFlushSeconds = MinChunkBeforePauseSlider.Value;
        _settings.SpeechStartThreshold = (float)SpeechStartThresholdSlider.Value;
        _settings.SpeechEndThreshold = (float)SpeechEndThresholdSlider.Value;
        _settings.SilenceThreshold = (float)SilenceThresholdSlider.Value;

        _capture.PauseDurationSeconds = _settings.PauseDurationSeconds;
        _capture.MinChunkDurationBeforePauseFlushSeconds = _settings.MinChunkDurationBeforePauseFlushSeconds;
        _capture.SpeechStartThreshold = _settings.SpeechStartThreshold;
        _capture.SpeechEndThreshold = _settings.SpeechEndThreshold;
        _capture.SilenceThreshold = _settings.SilenceThreshold;

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
    }

    private void UpdateSettingsLabels()
    {
        PauseDurationValueText.Text = PauseDurationSlider.Value.ToString("F2");
        MinChunkBeforePauseValueText.Text = MinChunkBeforePauseSlider.Value.ToString("F2");
        SpeechStartValueText.Text = SpeechStartThresholdSlider.Value.ToString("F4");
        SpeechEndValueText.Text = SpeechEndThresholdSlider.Value.ToString("F4");
        SilenceThresholdText.Text = SilenceThresholdSlider.Value.ToString("F4");
        LlmTempValueText.Text = LlmTempSlider.Value.ToString("F1");
        WhisperTempValueText.Text = WhisperTempSlider.Value.ToString("F1");
    }

    private void LlmTempSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents) return;
        if (LlmCombo.SelectedItem != null && _settings.Translation.TryGetValue(LlmCombo.SelectedItem.ToString()!, out var llmOptions))
        {
            llmOptions.Temperature = e.NewValue;
            UpdateSettingsLabels();
            SaveUserSettings();
        }
    }

    private void WhisperTempSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents) return;
        _settings.WhisperTemperature = (float)e.NewValue;
        UpdateSettingsLabels();
        SaveUserSettings();
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

    private void UpdateActiveRecordIndicator()
    {
        bool isCapturing = (_capture?.IsCapturing ?? false) && (RecordAudioCheckBox.IsChecked == true);
        ActiveRecordIndicator.Foreground = isCapturing ? Brushes.Red : Brushes.Black;
        ActiveRecordIndicator.ToolTip = _settings.UiLanguage == "ru"
            ? (isCapturing ? "Запись идёт" : "Запись остановлена")
            : (isCapturing ? "Recording in progress" : "Recording stopped");

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
        UpdateActiveRecordIndicator();

        LlmLabel.Text = ru ? "Модель ИИ:" : "LLM:";
        LlmCombo.ToolTip = ru ? "Профиль LLM для перевода" : "LLM profile for translation";
        LlmTempLabel.Text = ru ? " Темп:" : " Temp:";
        LlmTempSlider.ToolTip = ru ? "Температура генерации LLM. Выше - больше креативности." : "LLM generation temperature. Higher = more creative.";
        
        WhisperLabel.Text = ru ? "Модель Whisper:" : "Whisper:";
        WhisperModelCombo.ToolTip = ru ? "Размер модели распознавания речи" : "Whisper model size for speech recognition";
        ApplyWhisperModelBtn.ToolTip = ru ? "Применить модель Whisper" : "Apply Whisper Model";
        WhisperTempLabel.Text = ru ? " Темп:" : " Temp:";
        WhisperTempSlider.ToolTip = ru ? "Температура Whisper. 0 для детерминированного результата." : "Whisper temperature. 0 for deterministic output.";

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

        SilenceThresholdLabel.Text = ru ? "Порог тишины:" : "Silence thr:";
        SilenceThresholdSlider.ToolTip = ru
           ? "Silence threshold - Амплитуда, ниже которой, считаем, что звука нет."
           : "Silence threshold - Amplitude below which audio is considered silence.";

        TranscriptHeader.Text = ru ? "Оригинал" : "Transcript (source)";
        TranslationHeader.Text = ru ? "Перевод" : "Translation";

        TranscriptBox.ToolTip = ru
            ? "Ctrl + колесо: изменить размер шрифта"
            : "Ctrl + mouse wheel: change font size";
        TranslationBox.ToolTip = TranscriptBox.ToolTip;
        
        MenuMainToolbar.Header = ru ? "Основная панель" : "Main Toolbar";
        MenuAudioToolbar.Header = ru ? "Панель аудио-настроек" : "Audio Settings";
        MenuLlmsToolbar.Header = ru ? "Панель нейросетей" : "LLM Toolbar";

        MenuTranscriptCopy.Header = ru ? "Копировать" : "Copy";
        MenuTranscriptSave.Header = ru ? "Сохранить в файл..." : "Save to File...";
        MenuTranscriptClear.Header = ru ? "Очистить текст" : "Clear text";

        MenuTranslationCopy.Header = ru ? "Копировать" : "Copy";
        MenuTranslationSave.Header = ru ? "Сохранить в файл..." : "Save to File...";
        MenuTranslationClear.Header = ru ? "Очистить текст" : "Clear text";

        //UpdateRecordingPath(_capture.LastRecordingPath);
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
        string path = App.AppSettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        JsonObject root = new();
        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                root = new JsonObject();
            }
        }

        JsonObject appSettings = root["AppSettings"] as JsonObject ?? new JsonObject();

        appSettings["ChunkDurationSeconds"] = _settings.MaxChunkDurationSeconds;
        appSettings["PauseDurationSeconds"] = _settings.PauseDurationSeconds;
        appSettings["MinChunkDurationBeforePauseFlushSeconds"] = _settings.MinChunkDurationBeforePauseFlushSeconds;
        appSettings["SpeechStartThreshold"] = _settings.SpeechStartThreshold;
        appSettings["SpeechEndThreshold"] = _settings.SpeechEndThreshold;
        appSettings["DefaultSourceLanguage"] = _settings.DefaultSourceLanguage;
        appSettings["DefaultTargetLanguage"] = _settings.DefaultTargetLanguage;
        appSettings["WhisperModelType"] = _settings.WhisperModelType;
        appSettings["OutputFontSize"] = _settings.OutputFontSize;
        appSettings["RecordAudioEnabled"] = _settings.RecordAudioEnabled;
        appSettings["UiLanguage"] = _settings.UiLanguage;
        appSettings["DefaultTranslationLlm"] = _settings.DefaultTranslationLlm;
        appSettings["WhisperTemperature"] = _settings.WhisperTemperature;
        appSettings["MainToolbarVisible"] = _settings.MainToolbarVisible;
        appSettings["AudioToolbarVisible"] = _settings.AudioToolbarVisible;
        appSettings["LlmToolbarVisible"] = _settings.LlmToolbarVisible;
        appSettings["SilenceThreshold"] = _settings.SilenceThreshold;

        // Optionally, one could update the Translation block here, 
        // but for now, we just ensure we don't accidentally overwrite the complex structure.

        root["AppSettings"] = appSettings;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LlmCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || LlmCombo.SelectedItem == null)
            return;

        string selectedLlm = LlmCombo.SelectedItem.ToString()!;
        if (_settings.Translation.TryGetValue(selectedLlm, out var options))
        {
            UpdateTranslator(options);
        }
    }

    private void ApplyWhisperModelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (WhisperModelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)
        {
            _settings.WhisperModelType = item.Content.ToString()!;
            SaveUserSettings();
            _ = InitializeWhisperAsync();
        }
    }

    private void ToggleToolbar_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        
        _settings.MainToolbarVisible = MenuMainToolbar.IsChecked;
        _settings.AudioToolbarVisible = MenuAudioToolbar.IsChecked;
        _settings.LlmToolbarVisible = MenuLlmsToolbar.IsChecked;

        ApplyToolbarVisibility();
        SaveUserSettings();
    }

    private void ApplyToolbarVisibility()
    {
        if (MainToolbar != null) MainToolbar.Visibility = _settings.MainToolbarVisible ? Visibility.Visible : Visibility.Collapsed;
        if (AudioToolbar != null) AudioToolbar.Visibility = _settings.AudioToolbarVisible ? Visibility.Visible : Visibility.Collapsed;
        if (LLMToolbar != null) LLMToolbar.Visibility = _settings.LlmToolbarVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAudioChunkReady(float[] samples)
    {
        _pendingChunks.Enqueue(samples);
        _ = ProcessChunkQueueAsync();
    }

    private void EnqueueTranslation(string text, string sourceFullName, string targetFullName)
    {
        _pendingTranslations.Enqueue(new TranslationRequest(text, sourceFullName, targetFullName));
        _ = ProcessTranslationQueueAsync();
    }

    private async Task ProcessTranslationQueueAsync()
    {
        if (Interlocked.Exchange(ref _processingTranslations, 1) == 1)
            return;

        try
        {
            string text = "", sourceLang = "", targetLang = "";
            while (_pendingTranslations.TryDequeue(out var request))
            {
                text += request.Text + " ";
                sourceLang = request.SourceLanguageName;
                targetLang = request.TargetLanguageName;
            }
            if (!string.IsNullOrWhiteSpace(text))
            {

                string translated = await _translator.TranslateAsync(
                    text,
                    sourceLang,
                    targetLang);

                Dispatcher.Invoke(() =>
                {
                    TranslationBox.AppendText(translated + Environment.NewLine);
                    TranslationBox.ScrollToEnd();
                });
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
                MessageBox.Show(this, ex.Message, "Translation error", MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            Interlocked.Exchange(ref _processingTranslations, 0);

            if (!_pendingTranslations.IsEmpty)
                _ = ProcessTranslationQueueAsync();
        }
    }

    private async Task ProcessChunkQueueAsync()
    {
        if (Interlocked.Exchange(ref _processingQueue, 1) == 1)
            return;

        try
        {
            while (_pendingChunks.TryDequeue(out var samples))
            {
                app_StatusChanged($"Chunks in queue({_pendingChunks.Count}), processing current of {samples.Length} B");
                string sourceLang = Dispatcher.Invoke(() =>
                    (SourceLangCombo.SelectedItem as LanguageInfo)?.Code ?? "en");

                LanguageInfo? targetLang = Dispatcher.Invoke(() =>
                    TargetLangCombo.SelectedItem as LanguageInfo);

                string sourceFullName = Dispatcher.Invoke(() =>
                    (SourceLangCombo.SelectedItem as LanguageInfo)?.Name ?? "English");

                string text = await _recognizer.RecognizeAsync(samples, sourceLang, _settings.WhisperTemperature);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    Dispatcher.Invoke(() =>
                    {
                        TranscriptBox.AppendText(text + Environment.NewLine);
                        TranscriptBox.ScrollToEnd();
                    });

                    if (targetLang != null && targetLang.Code != sourceLang)
                    {
                        EnqueueTranslation(text, sourceLang, targetLang.Code);
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
        var sourceLang = (SourceLangCombo.SelectedItem as LanguageInfo)?.Code;
        var targetLang = (TargetLangCombo.SelectedItem as LanguageInfo)?.Code;

        if (sourceLang != targetLang)
        {
            if (LlmCombo.SelectedItem != null && _settings.Translation.TryGetValue(LlmCombo.SelectedItem.ToString()!, out var options))
            {
                if (string.IsNullOrWhiteSpace(options.ApiUrl) || options.ApiUrl.Contains("[Full OpenAi URL]"))
                {
                    string msg = _settings.UiLanguage == "ru"
                        ? $"Выбранный профиль LLM ({LlmCombo.SelectedItem}) не настроен.\nПожалуйста, заполните конфигурацию в файile:\n{App.AppSettingsPath}\n\nИли выберите одинаковые языки (Source -> Target) для работы в режиме [Whisper only]."
                        : $"Selected LLM profile ({LlmCombo.SelectedItem}) is not configured.\nPlease fill out the configuration in file:\n{App.AppSettingsPath}\n\nOr select identical languages (Source -> Target) to work in [Whisper only] mode.";

                    MessageBox.Show(this, msg, "Configuration Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }

        if (DevicesCombo.SelectedItem is AudioDeviceInfo device)
            _capture.SwitchDevice(device.Id);

        _capture.SessionRecordingEnabled = RecordAudioCheckBox.IsChecked == true;

        try
        {
            _capture.Start();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, $"{ex.Message}\n\nSelected device is not available. Please select an active device.", 
                "Device Error", MessageBoxButton.OK, MessageBoxImage.Error);
            LoadDevices(); // Offer device list refresh implicitly
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to start capturing: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        RecordAudioCheckBox.IsEnabled = false;
        
        _sessionStartTime = DateTime.Now;
        SessionTimerText.Text = "0:00:00";
        _sessionTimer.Start();

        UpdateActiveRecordIndicator();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _capture.Stop();
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        RecordAudioCheckBox.IsEnabled = true;
        //UpdateRecordingPath(_capture.LastRecordingPath);
        
        _sessionTimer.Stop();
        UpdateActiveRecordIndicator();
    }
    private void MenuTranscriptClear_Click(object sender, RoutedEventArgs e)
    {
        TranscriptBox.Clear();
    }
    private void MenuTranslationClear_Click(object sender, RoutedEventArgs e)
    {
        TranslationBox.Clear();
    }
    private void MenuTranscriptSave_Click(object sender, RoutedEventArgs e)
    {
        SaveTextToFile(TranscriptBox.Text, (SourceLangCombo.SelectedItem as LanguageInfo)?.Code ?? "src");
    }

    private void MenuTranslationSave_Click(object sender, RoutedEventArgs e)
    {
        SaveTextToFile(TranslationBox.Text, (TargetLangCombo.SelectedItem as LanguageInfo)?.Code ?? "target");
    }

    private void SaveTextToFile(string text, string langSuffix)
    {
        string dir = AudioCaptureService.GetRecordsDirectory();
        Directory.CreateDirectory(dir);
        
        string baseName = "meeting";
        if (!string.IsNullOrEmpty(_capture.LastRecordingPath))
        {
            baseName = Path.GetFileNameWithoutExtension(_capture.LastRecordingPath);
        }
        else
        {
            baseName += $"-{DateTime.Now:yyyyMMdd-HHmmss}";
        }

        string defaultFileName = $"{baseName}_{langSuffix}.txt";

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = dir,
            FileName = defaultFileName,
            DefaultExt = ".txt",
            Filter = "Text documents (.txt)|*.txt"
        };

        if (saveDialog.ShowDialog() == true)
        {
            File.WriteAllText(saveDialog.FileName, text, System.Text.Encoding.Unicode);
            MessageBox.Show($"File saved: {saveDialog.FileName}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveUserSettings();
        _capture.Dispose();
        AudioCaptureService.CleanupStaleTempFiles();
        _recognizer.Dispose();
    }
}