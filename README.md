# Alice Voice — Native Windows App

Native WinUI 3 desktop application for Alice Voice, replacing the browser-based
web app with a proper Windows experience. GPU-accelerated UI, GPU inference,
low-latency native audio, and best-practice structured logging.

## Architecture

```
AliceVoice.sln
├── src/
│   ├── AliceVoice.App/        WinUI 3 desktop shell (UI, DI, startup)
│   ├── AliceVoice.Core/       Logging, GPU selection, self-update
│   ├── AliceVoice.Inference/  ONNX Runtime + DirectML Whisper (GPU)
│   └── AliceVoice.Audio/      WASAPI microphone capture
└── tests/
    └── AliceVoice.App.Tests/  xUnit tests
```

## GPU strategy

Two GTX 1080 Ti. GPU 0 is the farm render GPU (kept for rendering); voice
inference is pinned to the GPU with the most free VRAM that is NOT index 0
(typically GPU 1). Selection is deliberate via `IGpuSelector`, not left to
chance.

## Logging

Serilog: rolling daily file + Windows Event Log (warnings+) + console.
Structured, best-practice — replaces the ad-hoc logging of the legacy web app.

## Self-update

The app checks `version.json` (in this repo, served from GitHub) on startup and
downloads the latest release. Source and binaries are retained in git.

## Build

```bash
dotnet build AliceVoice.sln -c Debug
dotnet test AliceVoice.sln -c Debug
```

Requires: .NET SDK 10, Visual Studio 18 (WinUI workload), Windows App SDK 2.5.1.
