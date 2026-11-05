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

The window and the phone share one Alice LOCAL conversation. The bridge process
holds that history. A turn on the phone shows up in the window, and the other
way around. Asks are serialized, so two devices cannot interleave one reply.

### Bridge bind (Tailscale only)

The bridge stays off until `ALICEVOICE_BRIDGE_BIND` (or `BridgeBind` in
appsettings) is set. The value is `host:port`. By default the host must be a
Tailscale address:

- an IPv4 address in `100.64.0.0/10` (what `tailscale ip -4` prints on the Beast)
- a Tailscale IPv6 address in `fd7a:115c:a1e0::/48`
- the machine's MagicDNS name (`ahc-the-beast`) or a `*.ts.net` name

`0.0.0.0`, a public address, and `192.168.x` (and other non-Tailscale
addresses, including `127.0.0.1`) are refused. The app logs the refusal and
does not listen. To override that on purpose:

```powershell
$env:ALICEVOICE_BRIDGE_ALLOW_UNSAFE_BIND = "1"
```

There is no authentication on the bridge. Do not point the override at the
public internet.

```powershell
tailscale ip -4
$env:ALICEVOICE_BRIDGE_BIND = "100.x.y.z:8765"
```

HttpListener on a non-loopback address needs a URL reservation, once, as the
user who runs the app (this is the usual Windows step, not a Tailscale step):

```powershell
netsh http add urlacl url=http://100.x.y.z:8765/ user=$env:USERNAME
```

The phone's default `ALICEVOICE_BEAST_HOST` is the Tailscale MagicDNS name
`ahc-the-beast`. Override it with the Beast's `100.x` address if MagicDNS is
off. The phone does not copy the `Mirrored` scene folder or the PC's
voice-assignment file. AllTalk and Piper still run on the Beast. Android's own
speech engine is a separate provider (`AndroidSpeech` = 3 in
`voice-assignments.json`) and can be assigned on the phone. Windows ignores
that value and keeps AllTalk = 0, SAPI = 1, Piper = 2.

## Android head

```powershell
dotnet workload install maui-android
dotnet build universal/src/AliceVoice.Universal.App/AliceVoice.Universal.App.csproj -c Debug -f net10.0-android -p:AndroidSdkDirectory="$env:ANDROID_HOME"
```

A Debug APK is written under
`universal/src/AliceVoice.Universal.App/bin/Debug/net10.0-android/`.
Install it with `adb install` (the `-Signed.apk` is the one the SDK signs
with the debug key).

On the phone, `ALICEVOICE_BEAST_HOST` defaults to `ahc-the-beast`. Set it to
the Beast's Tailscale IPv4 (`100.x.y.z`) when MagicDNS is not available.
`ALICEVOICE_BRIDGE_PORT` defaults to 8765. `ALICEVOICE_*` environment variables
win over `appsettings.json` (see `appsettings.example.json`).

Android 9+ blocks plain HTTP unless the app allows it. This head sets
`usesCleartextTraffic` because Ollama, Hermes, AllTalk, and the bridge are
HTTP, matching the desktop. Tailscale encrypts the tunnel.

The phone asks for the microphone and for notifications. Wake mode starts a
microphone foreground service so the process is allowed to keep listening.

### Android updates

`version.json` may grow an `android` object. The Windows fields stay, and the
existing signature still covers the whole file. This repo does not commit a
new `version.json.p7s`. Signing happens on the Beast, where the CN=AHC
code-signing certificate lives (thumbprint
`9F5116C32A280552E38F7FA87967EDED6C9C34C2`, valid until 2028-10-29):

```powershell
powershell -File universal/scripts/Publish-AndroidRelease.ps1 `
  -Version 0.2.0 `
  -ApkUrl https://github.com/daemonlinks/alice-voice-desktop/releases/download/v0.2.0/AliceVoice.AppU.apk `
  -Sha256 ((Get-FileHash .\AliceVoice.AppU.apk).Hash)
```

The script keeps the MSIX fields, writes the APK url and hash, and runs
`tools/AliceVoice.ReleaseSigner`. Commit `version.json` and `version.json.p7s`
together afterwards. Until that signed file is what GitHub serves, the phone
keeps reporting that the manifest only has a Windows package. After it is
served, the phone checks the same signature and offers the APK. It does not
download or install the APK by itself.
