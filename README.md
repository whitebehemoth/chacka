# Chacka — Live Meeting Translator (EN/RU/FR) 


> Real-time meeting helper for Windows: capture system audio, transcribe with Whisper, and translate on the fly.

---

## [Protable ZIP win/x64](https://github.com/whitebehemoth/chacka/releases/download/v1.0.0/chacka-win-x64-portable.zip)

## English

### What is this
`Chacka` is a WPF desktop app (`.NET 10`) for online meetings:
- captures **system output audio** (loopback)
- splits audio into smart chunks (pause-based)
- recognizes speech with **Whisper**
- translates recognized text to target language
- optionally records full session into one MP3 file

### Main features
- Audio device selection (render devices)
- Pause-aware chunking controls:
  - `PauseDurationSeconds`
  - `MinChunkDurationBeforePauseFlushSeconds`
  - `SpeechStartThreshold`
  - `SpeechEndThreshold`
- UI language switch: **EN/RU**
- Ctrl + mouse wheel text zoom (shared for transcript + translation)
- Optional full-session MP3 recording between Start/Stop
- Open recordings folder button

### Requirements
- Windows 10/11
- .NET 10 SDK (for local build/run)
- Whisper model download access (on first run)
- Translation API endpoint/key (if translation is enabled)

### Quick start
```bash
# from repository root
cd chacka
dotnet restore
dotnet run
```

### Configuration
Default config: `chacka/appsettings.json`

Important fields:
- `ChunkDurationSeconds`
- `PauseDurationSeconds`
- `MinChunkDurationBeforePauseFlushSeconds`
- `SpeechStartThreshold`
- `SpeechEndThreshold`
- `RecordAudioEnabled`
- `UiLanguage`
- `WhisperModelType`
- `Translation.ApiUrl`, `Translation.ModelName`, `Translation.ApiKey`

Runtime UI changes are persisted into local `appsettings.json` in the portable folder (next to the executable).

### Where files are saved
- User settings: `./appsettings.json` (portable app folder)
- Recordings: `~/Documents/Chacka meeting recordings`

### Security note
Do not commit real API keys into git. Use:
- `UserSecrets` for development
- environment variables / secret store for production

### Build release binary (portable)
```bash
cd chacka
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```
Output is in `bin/Release/net10.0-windows/win-x64/publish`.

---

## Русский

### Что это
`Chacka` — WPF-приложение (`.NET 10`) для онлайн-митингов:
- захватывает **системный звук** (loopback)
- делит аудио на умные чанки (по паузам)
- распознаёт речь через **Whisper**
- переводит текст в целевой язык
- опционально сохраняет всю сессию в один MP3

### Основные возможности
- Выбор аудиоустройства (render devices)
- Настройки разбивки по паузам:
  - `PauseDurationSeconds`
  - `MinChunkDurationBeforePauseFlushSeconds`
  - `SpeechStartThreshold`
  - `SpeechEndThreshold`
- Переключение языка интерфейса: **EN/RU**
- Изменение размера текста: Ctrl + колесо (для оригинала и перевода)
- Запись всей сессии в MP3 между Start/Stop
- Кнопка открытия папки записей

### Требования
- Windows 10/11
- .NET 10 SDK (для локальной сборки/запуска)
- Доступ для загрузки модели Whisper (при первом старте)
- API endpoint/key для перевода (если включён)

### Быстрый старт
```bash
# из корня репозитория
cd chacka
dotnet restore
dotnet run
```

### Конфигурация
Базовые настройки: `chacka/appsettings.json`

Ключевые поля:
- `ChunkDurationSeconds`
- `PauseDurationSeconds`
- `MinChunkDurationBeforePauseFlushSeconds`
- `SpeechStartThreshold`
- `SpeechEndThreshold`
- `RecordAudioEnabled`
- `UiLanguage`
- `WhisperModelType`
- `Translation.ApiUrl`, `Translation.ModelName`, `Translation.ApiKey`

Изменения из UI сохраняются в локальный `appsettings.json` рядом с `.exe` (portable-папка).

### Куда сохраняются файлы
- Пользовательские настройки: `./appsettings.json` (в portable-папке)
- Записи: `~/Documents/Chacka meeting recordings`

### Важно по безопасности
Не коммитьте реальные API-ключи в репозиторий. Используйте:
- `UserSecrets` для разработки
- переменные окружения / секрет-хранилище для продакшена

