using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls.Primitives;
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
    private sealed record TranslationRequest(string Text, string SourceLangCode, string TargetLangCode);

    private readonly AppSettings _settings;
    private readonly AudioCaptureService _capture = new();
    private readonly WhisperRecognizer _recognizer = new();
    private ITranslationService _translator = new AzureTranslationService();
    private readonly UiLangOption[] _uiLanguages =
    [
        new("en", "EN"),
        new("ru", "RU")
    ];
    private readonly ConcurrentQueue<float[]> _pendingChunks = new();
    private readonly ConcurrentQueue<TranslationRequest> _pendingTranslations = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _processingQueue;          // 1 = chunk queue is being processed
    private int _processingTranslations;   // 1 = translation queue is being processed
    private int _isShuttingDown;           // 1 = app is shutting down
    private int _forceCloseStarted;
    private volatile string _cachedSourceLangCode = "en";
    private volatile LanguageInfo? _cachedTargetLang;
    private bool _suppressUiEvents = true; // guards against recursive UI events during initialization
    private CancellationTokenSource? _fileProcessingCts;
    private bool _isProcessingFile;
    private const double MinUiFontSize = 8;
    private const double MaxUiFontSize = 24;
    private readonly System.Windows.Threading.DispatcherTimer _sessionTimer = new();
    private DateTime _sessionStartTime;

    public MainWindow()
    {
        InitializeComponent();

        AudioCaptureService.CleanupStaleTempFiles();

        // Load persisted settings (falls back to defaults if missing)
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

    /// <summary>Updates main status bar text from any thread (recognizer, translator, capture).</summary>
    private void app_StatusChanged(string obj) =>
        PostToUiIfAlive(() => StatusLabel.Text = obj);

    /// <summary>Updates voice-level indicator from capture thread.</summary>
    private void capture_StatusChanged(string obj) =>
        PostToUiIfAlive(() => CapturedStatusLabel.Text = obj);

    /// <summary>Safely posts an action to the UI thread, skipping if shutdown is in progress.</summary>
    private void PostToUiIfAlive(Action action)
    {
        if (Volatile.Read(ref _isShuttingDown) == 1)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (Volatile.Read(ref _isShuttingDown) == 1)
                return;

            action();
        });
    }

    /// <summary>Populates source/target language combos from config.</summary>
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
            MessageBox.Show($"Translation LLM was not found, check the configuration or repair the app", "Corrupted config", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }
        else
        {
            LlmCombo.SelectedItem = llm;
            UpdateTranslator(_settings.Translation[llm]);
        }
    }

    /// <summary>Swaps the active translation service based on provider type.</summary>
    private void UpdateTranslator(TranslationOptions options)
    {
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
        SourceLangCombo.SelectionChanged += LangCombo_SelectionChanged;
        TargetLangCombo.SelectionChanged += LangCombo_SelectionChanged;
    }

    private void LangCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateCachedLanguages();
        UpdateWhisperModeIndicator();
    }

    private void UpdateCachedLanguages()
    {
        var src = SourceLangCombo.SelectedItem as LanguageInfo;
        _cachedSourceLangCode = src?.Code ?? "en";
        _cachedTargetLang = TargetLangCombo.SelectedItem as LanguageInfo;
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
        UpdateSettingsLabels();
        UpdateCachedLanguages();
        UpdateWhisperModeIndicator();
        _suppressUiEvents = false;
    }

    private void SettingsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents)
            return;

        _suppressUiEvents = true;

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

    private void NumericValueTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ApplyNumericValueFromText(sender as System.Windows.Controls.TextBox);
    }

    private void NumericValueTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        ApplyNumericValueFromText(sender as System.Windows.Controls.TextBox);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ApplyNumericValueFromText(System.Windows.Controls.TextBox? textBox)
    {
        if (textBox is null)
            return;

        RangeBase? numeric = textBox.Name switch
        {
            nameof(PauseDurationValueText) => GetNumericRangeByName(nameof(PauseDurationSlider)),
            nameof(MinChunkBeforePauseValueText) => GetNumericRangeByName(nameof(MinChunkBeforePauseSlider)),
            nameof(SpeechStartValueText) => GetNumericRangeByName(nameof(SpeechStartThresholdSlider)),
            nameof(SpeechEndValueText) => GetNumericRangeByName(nameof(SpeechEndThresholdSlider)),
            nameof(SilenceThresholdText) => GetNumericRangeByName(nameof(SilenceThresholdSlider)),
            nameof(LlmTempValueText) => GetNumericRangeByName(nameof(LlmTempSlider)),
            nameof(WhisperTempValueText) => GetNumericRangeByName(nameof(WhisperTempSlider)),
            _ => null
        };

        if (numeric is null)
            return;

        if (!double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            UpdateSettingsLabels();
            return;
        }

        value = Math.Clamp(value, numeric.Minimum, numeric.Maximum);

        if (Math.Abs(numeric.Value - value) > 0.0000001)
            numeric.Value = value;
        else
            UpdateSettingsLabels();
    }

    private void NumericSpinButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RepeatButton { Tag: string tag })
            return;

        var split = tag.Split(':', 2);
        if (split.Length != 2)
            return;

        RangeBase? numeric = GetNumericRangeByName(split[0]);
        if (numeric is null)
            return;

        int direction = split[1] == "+" ? 1 : -1;
        double step = numeric is ScrollBar scrollBar ? scrollBar.SmallChange : 1;
        double next = Math.Clamp(numeric.Value + (direction * step), numeric.Minimum, numeric.Maximum);

        if (Math.Abs(numeric.Value - next) > 0.0000001)
            numeric.Value = next;
        else
            UpdateSettingsLabels();
    }

    private RangeBase? GetNumericRangeByName(string name)
    {
        return name switch
        {
            nameof(PauseDurationSlider) => PauseDurationSlider,
            nameof(MinChunkBeforePauseSlider) => MinChunkBeforePauseSlider,
            nameof(SpeechStartThresholdSlider) => SpeechStartThresholdSlider,
            nameof(SpeechEndThresholdSlider) => SpeechEndThresholdSlider,
            nameof(SilenceThresholdSlider) => SilenceThresholdSlider,
            nameof(LlmTempSlider) => LlmTempSlider,
            nameof(WhisperTempSlider) => WhisperTempSlider,
            _ => null
        };
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

    /// <summary>Callback from AudioCaptureService when a speech chunk is ready for STT.</summary>
    private void OnAudioChunkReady(float[] samples)
    {
        if (Volatile.Read(ref _isShuttingDown) == 1)
            return;

        _pendingChunks.Enqueue(samples);
        _ = ProcessChunkQueueAsync();
    }

    /// <summary>Enqueues recognized text for translation and kicks the translation pipeline.</summary>
    private void EnqueueTranslation(string text, string sourceLangCode, string targetLangCode)
    {
        if (Volatile.Read(ref _isShuttingDown) == 1)
            return;

        _pendingTranslations.Enqueue(new TranslationRequest(text, sourceLangCode, targetLangCode));
        _ = ProcessTranslationQueueAsync();
    }

    /// <summary>
    /// Single-threaded translation pipeline. Drains queued requests, merges consecutive
    /// texts into one batch, sends to the active translator, and appends result to UI.
    /// </summary>
    private async Task ProcessTranslationQueueAsync()
    {
        if (Volatile.Read(ref _isShuttingDown) == 1)
            return;

        if (Interlocked.Exchange(ref _processingTranslations, 1) == 1)
            return;

        try
        {
            var textBatch = new StringBuilder();
            string sourceLang = "", targetLang = "";
            while (_pendingTranslations.TryDequeue(out var request))
            {
                if (Volatile.Read(ref _isShuttingDown) == 1)
                    return;

                textBatch.Append(request.Text).Append(' ');
                sourceLang = request.SourceLangCode;
                targetLang = request.TargetLangCode;
            }

            string text = textBatch.ToString().TrimEnd();
            if (Volatile.Read(ref _isShuttingDown) == 0 && !string.IsNullOrWhiteSpace(text))
            {
                string translated = await _translator.TranslateAsync(
                    text,
                    sourceLang,
                    targetLang,
                    _shutdownCts.Token);

                if (Volatile.Read(ref _isShuttingDown) == 1)
                    return;

                Dispatcher.BeginInvoke(() =>
                {
                    if (Volatile.Read(ref _isShuttingDown) == 1)
                        return;

                    TranslationBox.AppendText(translated + Environment.NewLine);
                    TranslationBox.ScrollToEnd();
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _isShuttingDown) == 1)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _isShuttingDown) == 1)
                    return;

                MessageBox.Show(this, ex.Message, "Translation error", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
        finally
        {
            Interlocked.Exchange(ref _processingTranslations, 0);

            if (Volatile.Read(ref _isShuttingDown) == 0 && !_pendingTranslations.IsEmpty)
                _ = ProcessTranslationQueueAsync();
        }
    }

    /// <summary>
    /// Single-threaded STT pipeline. Drains audio chunks, recognizes via Whisper,
    /// appends transcript to UI, and enqueues translation if source != target language.
    /// </summary>
    private async Task ProcessChunkQueueAsync()
    {
        if (Volatile.Read(ref _isShuttingDown) == 1)
            return;

        if (Interlocked.Exchange(ref _processingQueue, 1) == 1)
            return;

        try
        {
            while (_pendingChunks.TryDequeue(out var samples))
            {
                if (Volatile.Read(ref _isShuttingDown) == 1)
                    return;

                app_StatusChanged($"Chunks in queue({_pendingChunks.Count}), processing current of {samples.Length} samples");
                string sourceLang = _cachedSourceLangCode;
                LanguageInfo? targetLang = _cachedTargetLang;

                string text = await _recognizer.RecognizeAsync(samples, sourceLang, _settings.WhisperTemperature, _shutdownCts.Token);

                if (Volatile.Read(ref _isShuttingDown) == 0 && !string.IsNullOrWhiteSpace(text))
                {
                    await Dispatcher.BeginInvoke(() =>
                    {
                        if (Volatile.Read(ref _isShuttingDown) == 1)
                            return;

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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _isShuttingDown) == 1)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _isShuttingDown) == 1)
                    return;

                MessageBox.Show(this, ex.Message, "Processing error", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
        finally
        {
            Interlocked.Exchange(ref _processingQueue, 0);

            if (Volatile.Read(ref _isShuttingDown) == 0 && !_pendingChunks.IsEmpty)
                _ = ProcessChunkQueueAsync();
        }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_recognizer.IsLoaded)
        {
            MessageBox.Show(this,
                _settings.UiLanguage == "ru"
                    ? "Модель Whisper ещё не загружена. Подождите."
                    : "Whisper model is not loaded yet. Please wait.",
                "Whisper", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = _settings.UiLanguage == "ru" ? "Открыть аудиозапись" : "Open audio recording",
            Filter = "Audio files (*.mp3;*.wav)|*.mp3;*.wav|All files (*.*)|*.*",
            InitialDirectory = AudioCaptureService.GetRecordsDirectory()
        };

        if (dlg.ShowDialog(this) != true)
            return;

        // Очищаем окна перед обработкой нового файла
        TranscriptBox.Clear();
        TranslationBox.Clear();

        await ProcessRecordingFileAsync(dlg.FileName);
    }

    private async Task ProcessRecordingFileAsync(string filePath)
    {
        _isProcessingFile = true;
        _fileProcessingCts = new CancellationTokenSource();
        var ct = _fileProcessingCts.Token;

        SetFileProcessingUiState(active: true);

        string fileName = System.IO.Path.GetFileName(filePath);
        UpdateFileProgress(_settings.UiLanguage == "ru"
            ? $"Загрузка: {fileName}…"
            : $"Loading: {fileName}…");

        try
        {
            // Load and resample audio on a background thread
            float[] samples = await Task.Run(() => AudioCaptureService.LoadAudioFileAs16kMono(filePath), ct);

            double totalSeconds = samples.Length / 16000.0;
            string totalDuration = TimeSpan.FromSeconds(totalSeconds).ToString(@"h\:mm\:ss");

            UpdateFileProgress(_settings.UiLanguage == "ru"
                ? $"Обработка: {fileName} ({totalDuration})…"
                : $"Processing: {fileName} ({totalDuration})…");

            UpdateCachedLanguages();
            string sourceLang = _cachedSourceLangCode;
            LanguageInfo? targetLang = _cachedTargetLang;
            int segmentCount = 0;
            int lastTimestampMinute = -1;
            string? prevText = null;

            await foreach (var segment in _recognizer.ProcessFileStreamingAsync(
                samples, sourceLang, _settings.WhisperTemperature, ct))
            {
                // Дедупликация: пропускаем повторяющиеся сегменты (галлюцинации на тишине)
                if (prevText != null && string.Equals(segment.Text, prevText, StringComparison.Ordinal))
                    continue;
                prevText = segment.Text;

                segmentCount++;

                // Таймстамп раз в минуту (ближайшая целая минута)
                int currentMinute = (int)Math.Round(segment.Start.TotalMinutes);
                string timestampLine = "";
                if (currentMinute > lastTimestampMinute)
                {
                    lastTimestampMinute = currentMinute;
                    var roundedTime = TimeSpan.FromMinutes(currentMinute);
                    timestampLine = $"[{roundedTime:hh\\:mm\\:ss}]{Environment.NewLine}";
                }

                string line = timestampLine + segment.Text;

                await Dispatcher.InvokeAsync(() =>
                {
                    TranscriptBox.AppendText(line + Environment.NewLine);
                    TranscriptBox.ScrollToEnd();
                });

                UpdateFileProgress(_settings.UiLanguage == "ru"
                    ? $"Обработка: {fileName} — сегмент {segmentCount}, {segment.End:hh\\:mm\\:ss} / {totalDuration}"
                    : $"Processing: {fileName} — segment {segmentCount}, {segment.End:hh\\:mm\\:ss} / {totalDuration}");

                if (targetLang != null && targetLang.Code != sourceLang)
                {
                    EnqueueTranslation(segment.Text, sourceLang, targetLang.Code);
                }
            }

            UpdateFileProgress(_settings.UiLanguage == "ru"
                ? $"✔ Готово: {fileName} — {segmentCount} сегментов"
                : $"✔ Done: {fileName} — {segmentCount} segments");
        }
        catch (OperationCanceledException)
        {
            UpdateFileProgress(_settings.UiLanguage == "ru" ? "Обработка отменена" : "Processing cancelled");
        }
        catch (Exception ex)
        {
            // Если это отмена из whisper.cpp — не показываем ошибку
            if (ct.IsCancellationRequested)
            {
                UpdateFileProgress(_settings.UiLanguage == "ru" ? "Обработка отменена" : "Processing cancelled");
            }
            else
            {
                UpdateFileProgress("");
                MessageBox.Show(this, ex.Message,
                    _settings.UiLanguage == "ru" ? "Ошибка обработки файла" : "File processing error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _isProcessingFile = false;
            _fileProcessingCts?.Dispose();
            _fileProcessingCts = null;
            SetFileProcessingUiState(active: false);
        }
    }

    private void SetFileProcessingUiState(bool active)
    {
        StartBtn.IsEnabled = !active;
        OpenFileBtn.IsEnabled = !active;
        StopBtn.IsEnabled = active || _capture.IsCapturing;
        DevicesCombo.IsEnabled = !active;

        if (!active)
        {
            // Скрыть прогресс через 10 секунд после окончания
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (!_isProcessingFile)
                    FileProgressText.Visibility = Visibility.Collapsed;
            };
            timer.Start();
        }
    }

    private void UpdateFileProgress(string text)
    {
        PostToUiIfAlive(() =>
        {
            FileProgressText.Text = text;
            FileProgressText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        });
    }

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
        OpenFileBtn.IsEnabled = false;
        
        _sessionStartTime = DateTime.Now;
        SessionTimerText.Text = "0:00:00";
        _sessionTimer.Start();

        UpdateActiveRecordIndicator();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessingFile)
        {
            _fileProcessingCts?.Cancel();
            return;
        }

        _capture.Stop();
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        RecordAudioCheckBox.IsEnabled = true;
        OpenFileBtn.IsEnabled = true;

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
        if (Interlocked.Exchange(ref _forceCloseStarted, 1) == 1)
            return;

        e.Cancel = true;
        _ = ForceCloseAsync();
    }

    /// <summary>
    /// Graceful shutdown: signal all background work to stop, drain queues,
    /// persist settings, dispose resources, then force-exit the process.
    /// </summary>
    private async Task ForceCloseAsync()
    {
        Interlocked.Exchange(ref _isShuttingDown, 1);
        _shutdownCts.Cancel();
        _fileProcessingCts?.Cancel();
        _sessionTimer.Stop();

        _capture.AudioChunkReady -= OnAudioChunkReady;
        _capture.VoiceCaptured -= capture_StatusChanged;
        _capture.StatusChanged -= app_StatusChanged;
        _recognizer.StatusChanged -= app_StatusChanged;
        _translator.StatusChanged -= app_StatusChanged;

        while (_pendingChunks.TryDequeue(out _)) { }
        while (_pendingTranslations.TryDequeue(out _)) { }

        try
        {
            SaveUserSettings();
            _capture.Abort();
            _capture.Dispose();
            AudioCaptureService.CleanupStaleTempFiles();
            _shutdownCts.Dispose();
        }
        catch
        {
        }

        await Task.Delay(120);
        Environment.Exit(0);
    }
}