<div align="center">
  <img src="docs/logo.png" width="96" alt="SkyNetwork">
  <h1>Skynetwork-voice</h1>
  <p>Голосовой сервер и клиентская библиотека для авиационной радиосвязи SkyNetwork.</p>

  [![CI](https://github.com/chatgpt82628163-pixel/Skynetwork-voice/actions/workflows/ci.yml/badge.svg)](https://github.com/chatgpt82628163-pixel/Skynetwork-voice/actions/workflows/ci.yml)
  [![Release](https://img.shields.io/github/v/release/chatgpt82628163-pixel/Skynetwork-voice)](https://github.com/chatgpt82628163-pixel/Skynetwork-voice/releases)

  [Сайт](https://sky.network.npzy2.us) · [Поддержка](https://sky.network.npzy2.us/support)
</div>

---

## Что это

**Skynetwork-voice** — UDP-сервер на C++, который ретранслирует голосовые пакеты между участниками сети SkyNetwork. Сервер не декодирует аудио: он получает кадры Opus, рассчитывает дальность связи по радиогоризонту VHF, и пересылает каждый кадр только тем, кто настроен на ту же частоту и находится в зоне слышимости.

Рядом находится клиентская библиотека **SkyNetwork.Voice** на C# (.NET 8). Её используют SkyPilot и Network-ATC: она берёт на себя протокол, кодек Opus, аудиоустройства Windows, радиоэффекты и управление тангентой.

## Возможности

### Сервер (C++)

- **UDP-ретрансляция без декодирования** — минимальная задержка; сервер работает как маршрутизатор пакетов.
- **Расчёт радиогоризонта** — пакет доходит только до тех клиентов, у которых есть прямая видимость с передатчиком (высота и координаты антенны).
- **Расширенное покрытие для диспетчеров** — контроллер может задать дальность покрытия сектора (до 10 000 nm), чтобы самолёты слышали его и за горизонтом.
- **Сила сигнала** — каждый пакет сопровождается коэффициентом 0–1, который клиент использует для наложения шума и эффектов.
- **Аккаунты в SQLite** — пароли хранятся в виде PBKDF2-SHA256; сервер периодически проверяет статус подключённых участников и отключает заблокированных.
- **Рейтинги** — OBS, S1–S3, C1–C3, I1–I3, SUP, ADM.
- **Утилита `skynet-admin`** — управление аккаунтами из командной строки (`adduser`, `rating`, `staff`, `passwd`, `suspend`/`unsuspend`).

### Клиентская библиотека (C#)

- **`VoiceClient`** — единая точка входа: подключение, радиостанции, позиция антенны, мик и динамики.
- **Кодек Opus (Concentus)** — чистая C#-реализация, не требует нативных DLL.
- **Радиоэффект** — полоса 300–3000 Гц, мягкое ограничение, шум зависит от силы сигнала; хвост шума после конца передачи (squelch tail). Эффект можно отключить.
- **Радиомикшер** — несколько станций на нескольких частотах одновременно; независимая регулировка громкости каждой радиостанции.
- **Управление тангентой** — любая клавиша клавиатуры (Windows VK) или кнопка джойстика; конфигурация хранится строкой `"key:162"` / `"joy:0:4"`.
- **Трансиверы** — автоматически рассчитываются из набора радиостанций и позиций антенн; обновляются при изменении частоты или значительном перемещении.
- **Аудиоустройства Windows** — выбор микрофона и динамиков по индексу, регулировка усиления.
- **События** — `StateChanged`, `ReceiveActivity` (начало/конец приёма по частоте), `TransmitChanged`.

## Сборка и запуск

### Сервер

Требования: компилятор C++17, CMake 3.16+, OpenSSL, SQLite3.

```bash
cmake -B build
cmake --build build
```

Запуск:

```bash
./build/skynet-voice [--db skynetwork.db] [--host 0.0.0.0] [--port 3782] [--account-check 10]
```

| Опция | По умолчанию | Описание |
|---|---|---|
| `--db` | `skynetwork.db` | Путь к базе данных SQLite |
| `--host` | `0.0.0.0` | Адрес для привязки |
| `--port` | `3782` | UDP-порт |
| `--account-check` | `10` | Интервал проверки аккаунтов (секунды) |

Управление аккаунтами:

```bash
./build/skynet-admin [--db skynetwork.db] adduser CID NAME PASSWORD [RATING]
./build/skynet-admin [--db skynetwork.db] rating CID RATING
./build/skynet-admin [--db skynetwork.db] suspend CID
./build/skynet-admin [--db skynetwork.db] unsuspend CID
```

Рейтинги: `OBS S1 S2 S3 C1 C2 C3 I1 I2 I3 SUP ADM`.

### Клиентская библиотека

Требования: .NET 8 SDK.

```bash
dotnet build client/SkyNetwork.Voice.sln
```

### Тесты

Интеграционные тесты на Python (запускают реальный бинарь):

```bash
cmake -B build && cmake --build build
python3 -m unittest discover -s tests
```

Юнит-тесты клиентской библиотеки:

```bash
dotnet test client/SkyNetwork.Voice.sln
```

## Устройство

```
Skynetwork-voice/
├── src/
│   ├── main.cpp            — точка входа skynet-voice
│   ├── admin.cpp           — точка входа skynet-admin
│   ├── voice_server.cpp/h  — UDP-сервер, ретрансляция, расчёт горизонта
│   ├── accounts.cpp/h      — SQLite, PBKDF2-SHA256, рейтинги
│   ├── net.cpp/h           — сетевые утилиты
│   └── geo.h               — расчёт радиогоризонта
├── client/
│   └── SkyNetwork.Voice/   — C#-библиотека (копируется в SkyPilot и Network-ATC)
│       ├── VoiceClient.cs  — публичный API
│       ├── VoiceConnection.cs — протокол, UDP
│       ├── Transmitter.cs  — запись и отправка Opus
│       ├── RadioMixer.cs   — приём и микширование
│       ├── RadioEffect.cs  — полоса, шум, squelch tail
│       ├── PushToTalk.cs   — тангента (клавиша / джойстик)
│       ├── Opus.cs         — обёртка Concentus
│       └── Protocol.cs     — константы протокола
│   └── SkyNetwork.Voice.Tests/
├── tests/
│   └── test_voice.py       — Python-интеграционные тесты
└── CMakeLists.txt
```

## Часть SkyNetwork

Skynetwork-voice — один из компонентов сети SkyNetwork:

| Репозиторий | Описание |
|---|---|
| [SkyNetwork-site](https://github.com/chatgpt82628163-pixel/SkyNetwork-site) | Основной сайт сети |
| [SkyNetwork-FSD](https://github.com/chatgpt82628163-pixel/SkyNetwork-FSD) | FSD-сервер |
| [Network-ATC](https://github.com/chatgpt82628163-pixel/Network-ATC) | Программа диспетчера |
| [SkyPilot](https://github.com/chatgpt82628163-pixel/SkyPilot) | Программа пилота |
| [Skynetwork-voice](https://github.com/chatgpt82628163-pixel/Skynetwork-voice) | Голосовой сервер и библиотека (этот репозиторий) |
| [SkyRUS-site](https://github.com/chatgpt82628163-pixel/SkyRUS-site) | Сайт SkyRUS |
| [Skynetwork-bot](https://github.com/chatgpt82628163-pixel/Skynetwork-bot) | Бот |

---

## English

**Skynetwork-voice** is a UDP voice relay server (C++) and a C# client library for aviation radio communication on the SkyNetwork virtual ATC network.

**Server** receives Opus frames from clients, calculates VHF line-of-sight range from antenna coordinates, and forwards each frame only to clients tuned to the same frequency within range. It never decodes audio. Signal strength (0–1) is included with every forwarded packet so the receiver can apply realistic radio noise. Controllers can declare coverage beyond the radio horizon (up to 10 000 nm). Accounts are stored in SQLite with PBKDF2-SHA256 passwords; suspended members are kicked within the configured check interval.

**Client library** (`SkyNetwork.Voice`, .NET 8) handles the protocol, pure-C# Opus encoding via Concentus, Windows audio devices (NAudio), a 300–3000 Hz radio band-pass effect with signal-dependent noise and squelch tail, radio mixing for multiple simultaneous frequencies, and push-to-talk via any keyboard key or joystick button.

**Build:**
```bash
# Server
cmake -B build && cmake --build build

# Client library
dotnet build client/SkyNetwork.Voice.sln

# Tests
python3 -m unittest discover -s tests   # integration (needs built server)
dotnet test client/SkyNetwork.Voice.sln # unit tests
```

[Website](https://sky.network.npzy2.us) · [Support](https://sky.network.npzy2.us/support)
