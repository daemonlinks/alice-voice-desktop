# Alice Voice App U

.NET MAUI head for Alice Voice. Windows and Android. The WinUI app in the
repo root is unchanged and is still the one `AliceVoice.sln` builds.

App U calls the same `AliceVoice.Core` services on Windows (GPU speech-to-text,
SOUL file, Honcho tools, fleet chat, signed update check, openWakeWord). On
Android those PC-only pieces are reached through a bridge on the Beast. See
[PARITY.md](PARITY.md) for every feature and the Android route.

## Layout

```
universal/
├── AliceVoice.Universal.sln
├── PARITY.md
├── src/AliceVoice.Universal.Core/   session, bridge, paths (net10.0)
├── src/AliceVoice.Universal.App/    MAUI: net10.0-android, and net10.0-windows on Windows
└── tests/AliceVoice.Universal.Core.Tests/
```

`AliceVoice.Core` stays where it is. The universal solution references it.
Nothing in `src/AliceVoice.App` or the original tests was edited.

## Tests

From the repo root, with the .NET 10 SDK:

```powershell
dotnet test tests/AliceVoice.App.Tests/AliceVoice.App.Tests.csproj -c Debug
dotnet test universal/tests/AliceVoice.Universal.Core.Tests/AliceVoice.Universal.Core.Tests.csproj -c Debug
```

The original suite does not need the MAUI workload. The universal suite does
not need the Android SDK.

## Windows head

On Windows, with the .NET 10 SDK, the MAUI workload, and the same WinUI
dependencies the original app already needs (so Python, NVIDIA, and the local
services are unchanged):

```powershell
dotnet workload install maui
dotnet build universal/AliceVoice.Universal.sln -c Debug -f net10.0-windows10.0.19041.0
dotnet run --project universal/src/AliceVoice.Universal.App -c Debug -f net10.0-windows10.0.19041.0
```

The Windows package type is unpackaged, same as the WinUI app, so it can start
`faster_whisper_stt.py` and write `%ProgramData%\AliceVoice\alice_local\SOUL.md`.

To let the phone use this PC, set a Tailscale (or LAN) bind and start the app.
There is no auth on the bridge. Do not bind it to a public address.

```powershell
$env:ALICEVOICE_BRIDGE_BIND = "100.64.1.2:8765"
```

If Windows refuses the prefix, once, as the user who runs the app:

```powershell
netsh http add urlacl url=http://100.64.1.2:8765/ user=$env:USERNAME
```

`127.0.0.1:8765` does not need that reservation. The bridge stays off until
`ALICEVOICE_BRIDGE_BIND` or `BridgeBind` in appsettings is set.

## Android head

```powershell
dotnet workload install maui-android
dotnet build universal/src/AliceVoice.Universal.App/AliceVoice.Universal.App.csproj -c Debug -f net10.0-android -p:AndroidSdkDirectory="$env:ANDROID_HOME"
```

A Debug APK is written under
`universal/src/AliceVoice.Universal.App/bin/Debug/net10.0-android/`.
Install it with `adb install` (the `-Signed.apk` is the one the SDK signs
with the debug key).

On the phone, point App U at the Beast. Same keys as above, in the environment
or in `appsettings.json` inside the app's private files (see
`appsettings.example.json`). `ALICEVOICE_*` environment variables win.

```text
ALICEVOICE_BEAST_HOST=100.64.1.2
ALICEVOICE_BRIDGE_PORT=8765
```

Android 9+ blocks plain HTTP unless the app allows it. This head sets
`usesCleartextTraffic` because Ollama, Hermes, AllTalk, and the bridge are
HTTP, matching the desktop. Tailscale encrypts the tunnel. Do not put the
bridge on the open internet.

The phone asks for the microphone and for notifications. Wake mode starts a
microphone foreground service so the process is allowed to keep listening.
