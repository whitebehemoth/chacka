# Chacka - Meeting Assistant

Real-time meeting transcription & translation desktop app.
Captures **any system audio** (Zoom, Teams, browser, microphone playback - anything that goes through your speakers),
transcribes it with **Whisper**, and translates on the fly.

---

## [Portable ZIP win/x64](https://github.com/whitebehemoth/chacka/releases/download/v1.0.0/chacka-win-x64-portable.zip)

---

## English

### What is this?

**Chacka** is a WPF desktop application (`.NET 10`) designed to help you understand
meetings conducted in a foreign language - in real time.

It captures the system audio output via WASAPI loopback, so it picks up **every sound
your computer plays**: remote speakers in a video call, a presentation, or even your
own microphone if it routes through the system mixer. No browser extension or virtual
audio driver required.

### How it works

```
System audio -> WASAPI loopback capture
  -> smart chunking (pause-based VAD)
  -> Whisper STT (local, on-device)
  -> translation (Azure Translator / OpenAI LLM)
  -> transcript + translation displayed side-by-side
```

### Key features

- **Audio capture** - any render device; picks up all system audio regardless of the source app
- **Speech-to-text** - Whisper (tiny / base / small / medium / large); runs locally
- **Translation** - Azure Cognitive Translator or any OpenAI-compatible endpoint
- **Session recording** - optional full-session MP3 recording between Start / Stop
- **Speech tuning** - adjustable pause duration, min chunk length, speech start/end thresholds, silence threshold
- **UI languages** - English / Russian
- **Text zoom** - Ctrl + mouse wheel in transcript / translation panels
- **Save text** - right-click context menu to save transcript or translation to file

### Requirements

- Windows 10 / 11
- .NET 10 SDK (for building from source)
- Internet access for Whisper model download (first run) and translation API calls
- An API key for your chosen translation provider

### Quick start

```bash
cd chacka
dotnet restore
dotnet run
```

### Configuration

Settings file: `appsettings.json` (next to the executable).

Key settings:

- `MaxChunkDurationSeconds` - force-flush audio after this many seconds even without a pause
- `PauseDurationSeconds` - silence duration that triggers end-of-phrase
- `MinChunkDurationBeforePauseFlushSeconds` - minimum speech before pause-flush is allowed
- `SpeechStartThreshold` / `SpeechEndThreshold` - RMS thresholds for VAD hysteresis
- `SilenceThreshold` - amplitude below which audio is pure silence
- `WhisperModelType` - `tiny`, `base`, `small`, `medium`, or `large`
- `Translation` - dictionary of named LLM / API profiles

All slider-adjustable settings are persisted automatically.

### Build portable release

```bash
cd chacka
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Output: `bin/Release/net10.0-windows/win-x64/publish`

---

## Русский

### Что это?

**Chacka** - настольное WPF-приложение (`.NET 10`), которое помогает понимать
совещания на иностранном языке - в реальном времени.

Приложение захватывает **любой звук, воспроизводимый компьютером** через WASAPI loopback:
голоса собеседников в Zoom/Teams, звук в браузере и даже ваш микрофон,
если он проходит через системный микшер. Не требует расширений для браузера
или виртуальных аудиодрайверов.

### Как это работает

```
Системный звук -> захват через WASAPI loopback
  -> умная разбивка на фразы (VAD по паузам)
  -> распознавание речи Whisper (локально, на устройстве)
  -> перевод (Azure Translator / OpenAI LLM)
  -> оригинал + перевод отображаются рядом
```

### Основные возможности

- **Захват аудио** - любое устройство воспроизведения; захватывает весь системный звук вне зависимости от источника
- **Распознавание речи** - Whisper (tiny / base / small / medium / large); работает локально
- **Перевод** - Azure Cognitive Translator или любой OpenAI-совместимый endpoint
- **Запись сессии** - опциональная запись всей сессии в MP3 между Старт / Стоп
- **Настройка речи** - регулировка паузы, минимальной длины фразы, порогов начала/конца речи, порога тишины
- **Языки интерфейса** - Английский / Русский
- **Масштаб текста** - Ctrl + колесо мыши в панелях оригинала / перевода
- **Сохранение текста** - правый клик для сохранения оригинала или перевода в файл

### Требования

- Windows 10 / 11
- .NET 10 SDK (для сборки из исходников)
- Интернет для загрузки модели Whisper (при первом запуске) и вызовов API перевода
- API-ключ выбранного сервиса перевода

### Быстрый старт

```bash
cd chacka
dotnet restore
dotnet run
```

### Конфигурация

Файл настроек: `appsettings.json` (рядом с исполняемым файлом).

Ключевые параметры:

- `MaxChunkDurationSeconds` - принудительный сброс аудио после стольки секунд, даже без паузы
- `PauseDurationSeconds` - длительность тишины для определения конца фразы
- `MinChunkDurationBeforePauseFlushSeconds` - минимальная длина речи до разрешения сброса по паузе
- `SpeechStartThreshold` / `SpeechEndThreshold` - RMS-пороги для гистерезиса VAD
- `SilenceThreshold` - амплитуда, ниже которой звук считается тишиной
- `WhisperModelType` - `tiny`, `base`, `small`, `medium` или `large`
- `Translation` - словарь именованных профилей LLM / API

Все настройки, изменённые через ползунки, сохраняются автоматически.

### Сборка портативной версии

```bash
cd chacka
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Результат: `bin/Release/net10.0-windows/win-x64/publish`
