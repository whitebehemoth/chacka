using System.Windows.Media;
using chacka.Services;

namespace chacka;

/// <summary>
/// Partial class containing UI localization logic (EN/RU).
/// All translatable text assignments are centralized here.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Applies all UI labels, tooltips and titles for the selected language.
    /// </summary>
    private void ApplyUiLanguage(string lang)
    {
        bool ru = lang == "ru";
        string recordsDir = AudioCaptureService.GetRecordsDirectory();

        // Window title
        Title = ru ? "Секретарь Чакка" : "Meeting assistant Chacka";

        // Device toolbar
        DeviceLabel.Text = ru ? "Устройство:" : "Device:";
        RefreshDevicesBtn.ToolTip = ru ? "Обновить список устройств" : "Refresh audio devices";
        DevicesCombo.ToolTip = ru ? "Источник системного звука для распознавания" : "System audio source for recognition";

        // Start / Stop
        StartBtn.Content = ru ? "▶ Старт" : "▶ Start";
        StartBtn.ToolTip = ru ? "Начать захват и распознавание" : "Start capture and recognition";
        StopBtn.Content = ru ? "⏹ Стоп" : "⏹ Stop";
        StopBtn.ToolTip = ru ? "Остановить захват" : "Stop capture";

        // Recording controls
        RecordAudioCheckBox.Content = ru ? "Запись" : "Record";
        RecordAudioCheckBox.ToolTip = ru
            ? $"Сохранять весь аудиопоток между Старт/Стоп в один MP3. Папка: {recordsDir}"
            : $"Save all captured audio between Start/Stop into one MP3. Folder: {recordsDir}";
        OpenRecordFolderBtn.ToolTip = ru
            ? $"Открыть папку записей: {recordsDir}"
            : $"Open recordings folder: {recordsDir}";
        UpdateRecordIndicator();
        UpdateActiveRecordIndicator();

        // Translation LLM toolbar
        LlmLabel.Text = ru ? "Перевод через:" : "Translator:";
        LlmCombo.ToolTip = ru ? "Профиль LLM для перевода" : "LLM profile for translation";
        LlmTempLabel.Text = ru ? "Креативность:" : " Temp:";
        LlmTempValueText.ToolTip = ru
            ? "Температура генерации LLM. [0..1] Выше - больше креативности."
            : "LLM generation temperature. Higher = more creative.";

        // Whisper toolbar
        WhisperLabel.Text = ru ? "Распознать через Whisper:" : "Whisper:";
        WhisperModelCombo.ToolTip = ru ? "Размер модели распознавания речи" : "Whisper model size for speech recognition";
        ApplyWhisperModelBtn.ToolTip = ru ? "Применить модель Whisper" : "Apply Whisper Model";
        WhisperTempLabel.Text = ru ? " Креативность:" : " Temp:";
        WhisperTempValueText.ToolTip = ru
            ? "Температура креативности Whisper. [0..1], 0 для детерминированного результата."
            : "Whisper temperature. 0 for deterministic output.";

        // Language selection
        FromLabel.Text = ru ? "Из:" : "From:";
        ToLabel.Text = ru ? "В:" : "To:";
        UiLangLabel.Text = ru ? "Язык:" : "UI:";
        SourceLangCombo.ToolTip = ru ? "Язык оригинальной речи" : "Source speech language";
        TargetLangCombo.ToolTip = ru ? "Язык перевода" : "Translation target language";
        UiLanguageCombo.ToolTip = ru ? "Язык интерфейса" : "Interface language";

        // Audio settings toolbar
        PauseDurationLabel.Text = ru ? "Пауза между фразами:" : "Pause(s):";
        PauseDurationValueText.ToolTip = ru
            ? "Pause duration - Длина тишины в секундах перед завершением фразы. Увеличьте, если фразы режутся рано."
            : "Pause duration - Silence required to close phrase. Increase if phrases are cut too early.";

        MinChunkLabel.Text = ru ? "Мин. длина фразы:" : "Min chunk(s):";
        MinChunkBeforePauseValueText.ToolTip = ru
            ? "Minimum chunk duration - Минимальная длина чанка в секундах для распознования речи до срабатывания паузы."
            : "Minimum chunk duration - Minimal chunk length before pause flush is allowed.";

        SpeechStartLabel.Text = ru ? "Амплитуда начала:" : "Start thr:";
        SpeechStartValueText.ToolTip = ru
            ? "Speech start threshold - Порог начала речи. Повысьте, если шум запускает ложные фразы."
            : "Speech start threshold - Speech start sensitivity. Raise if noise starts false phrases.";

        SpeechEndLabel.Text = ru ? "Амплитуда конца:" : "End thr:";
        SpeechEndValueText.ToolTip = ru
            ? "Speech end threshold - Порог конца речи. Понизьте, если в шуме плохо определяется конец фразы."
            : "Speech end threshold - End-of-speech sensitivity. Lower when too noisy to capture end of phrase.";

        SilenceThresholdLabel.Text = ru ? "Амплитуда \"тишины\":" : "Silence thr:";
        SilenceThresholdText.ToolTip = ru
           ? "Silence threshold - Амплитуда, ниже которой, считаем, что звука нет."
           : "Silence threshold - Amplitude below which audio is considered silence.";

        // Output panels
        TranscriptHeader.Text = ru ? "Оригинал" : "Transcript (source)";
        TranslationHeader.Text = ru ? "Перевод" : "Translation";

        TranscriptBox.ToolTip = ru
            ? "Ctrl + колесо: изменить размер шрифта"
            : "Ctrl + mouse wheel: change font size";
        TranslationBox.ToolTip = TranscriptBox.ToolTip;

        // Toolbar visibility menu
        MenuMainToolbar.Header = ru ? "Основная панель" : "Main Toolbar";
        MenuAudioToolbar.Header = ru ? "Панель аудио-настроек" : "Audio Settings";
        MenuLlmsToolbar.Header = ru ? "Панель нейросетей" : "LLM Toolbar";

        // Transcript context menu
        MenuTranscriptCopy.Header = ru ? "Копировать" : "Copy";
        MenuTranscriptSave.Header = ru ? "Сохранить в файл..." : "Save to File...";
        MenuTranscriptClear.Header = ru ? "Очистить текст" : "Clear text";

        // Translation context menu
        MenuTranslationCopy.Header = ru ? "Копировать" : "Copy";
        MenuTranslationSave.Header = ru ? "Сохранить в файл..." : "Save to File...";
        MenuTranslationClear.Header = ru ? "Очистить текст" : "Clear text";
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
        bool isCapturing = _capture.IsCapturing && (RecordAudioCheckBox.IsChecked == true);
        ActiveRecordIndicator.Foreground = isCapturing ? Brushes.Red : Brushes.Black;
        ActiveRecordIndicator.ToolTip = _settings.UiLanguage == "ru"
            ? (isCapturing ? "Запись идёт" : "Запись остановлена")
            : (isCapturing ? "Recording in progress" : "Recording stopped");
    }
}
