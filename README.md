# Alice Voice — Native Windows App

Native WinUI 3 desktop application for Alice Voice, replacing the browser-based
web app with a proper Windows experience: push-to-talk speech-to-text on the
voice GPU, streamed replies, native audio and structured logging.

## Architecture

```
AliceVoice.sln
├── src/
│   ├── AliceVoice.App/        WinUI 3 desktop shell (UI, DI, startup, crash handlers)
│   ├── AliceVoice.Core/       Config, logging, GPU selection, faster-whisper STT client,
│   │                          voice backends (Hermes / Alice LOCAL), SOUL store, self-update
│   ├── AliceVoice.Audio/      NAudio mic capture + TTS playback, SAPI / Piper / AllTalk TTS
│   └── AliceVoice.Inference/  EXPERIMENTAL ONNX Runtime + DirectML Whisper — not registered
│                              by the app (DirectML adapter ordinals are not NVIDIA indices,
│                              so it could land on the render GPU)
├── scripts/
│   ├── faster_whisper_stt.py  STT worker; the app runs it once in --server mode and keeps the model warm
│   └── alice_local_tools.py
└── tests/
    └── AliceVoice.App.Tests/  xUnit tests (Core)
```

Audio classes are named `Wasapi*` for history, but use NAudio `WaveIn` /
`WaveOut` on the Windows default devices (mic captured at 16 kHz mono).

## Speech-to-text (push-to-talk)

Hold the talk button: audio is recorded while held and transcribed once on
release by a single warm `faster_whisper_stt.py --server` process (default
model `large-v3`, `int8_float32`). The Python used is, in order:
`ALICEVOICE_PYTHON` / `PythonPath`, the Hermes venv
(`%HERMES_HOME%\hermes-agent\venv\Scripts\python.exe`), then `python.exe`
on PATH — the Microsoft Store `WindowsApps` stub is always skipped.
The script is looked up in `ALICEVOICE_SCRIPTS` / `ScriptsDir`, then in a
`scripts\` folder next to the exe (copied at build) or in a parent folder (repo).

## GPU strategy

Two GTX 1080 Ti. GPU 0 is the farm render GPU and is **never** used for voice.
`NvidiaGpuSelector` queries `nvidia-smi` and picks the GPU with the most free
VRAM whose index is not 0 (typically GPU 1); the STT process is started with
`CUDA_VISIBLE_DEVICES` set to that index. `ALICEVOICE_STT_GPU` can force an
index, but 0 is refused. With no eligible GPU, STT falls back to CPU (`base` model).

## Backends and ports

| Service | Default | Override |
|---|---|---|
| Hermes api_server (fleet chat) | `http://127.0.0.1:8643` | `ALICEVOICE_HERMES_URL` |
| Ollama (Alice LOCAL) | `http://localhost:11434` | `ALICEVOICE_OLLAMA_URL` |
| AllTalk TTS | `http://127.0.0.1:7851` | `ALICEVOICE_ALLTALK_URL` |

The Hermes API key is read from the Hermes `.env` (`API_SERVER_KEY`); key
values are never logged.

## SOUL editor

The editor only ever writes the **Alice LOCAL** SOUL, by default
`%LOCALAPPDATA%\AliceVoice\alice_local\SOUL.md` (`ALICEVOICE_SOUL_LOCAL`).
The Hermes `profiles\alice_voice\SOUL.md` is a read-only fallback used until
the local copy exists; it is never overwritten. Before each save the previous
file is copied to `alice_local\backups\SOUL.yyyyMMdd-HHmmss-fff.md`, the new
text is written atomically, and Alice LOCAL reloads it.

## Configuration

Settings come from `%LOCALAPPDATA%\AliceVoice\appsettings.json` (optional,
property names as in `AliceVoiceOptions`), overridden by environment variables:

`ALICEVOICE_PYTHON`, `ALICEVOICE_SCRIPTS`, `ALICEVOICE_SOUL_LOCAL`,
`ALICEVOICE_SOUL_FALLBACK`, `ALICEVOICE_BACKGROUNDS`, `ALICEVOICE_PIPER`,
`ALICEVOICE_PIPER_VOICES`, `ALICEVOICE_ALLTALK_URL`, `ALICEVOICE_OLLAMA_URL`,
`ALICEVOICE_HERMES_URL`, `ALICEVOICE_STT_MODEL`, `ALICEVOICE_STT_COMPUTE`,
`ALICEVOICE_STT_CPU_MODEL`, `ALICEVOICE_STT_GPU`, `ALICEVOICE_UPDATE_URL`.
`HERMES_HOME` (default `%LOCALAPPDATA%\hermes`) locates the Hermes install.

## Logging

Serilog: rolling daily file in `%LOCALAPPDATA%\AliceVoice\logs`, console, and
the Windows Event Log (warnings+) when the source is available — if it is not
(e.g. non-admin first run) the app continues with file logging. Unhandled
exceptions are written to `%LOCALAPPDATA%\AliceVoice\crash`.

## Self-update

On startup the app reads `version.json` (this repo, raw on GitHub). A release
is only offered when it has a higher version, an `https` `downloadUrl` and a
64-hex `sha256`. Downloads are hash-verified (deleted on mismatch) and
re-verified immediately before launch; the app currently only shows
"Update available" and never runs a download automatically. Authenticode
signature checking is a planned follow-up. The app version comes from
`Directory.Build.props`.

## Build

```powershell
dotnet build AliceVoice.sln -c Debug -p:Platform=x64
dotnet test  AliceVoice.sln -c Debug -p:Platform=x64
```

Requires: .NET SDK 10, Visual Studio 18 (WinUI workload), Windows App SDK 2.5.1;
for STT, a Python with `faster-whisper` and an NVIDIA driver (`nvidia-smi`).
The Ollama integration test skips itself when Ollama is not running.
