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

## Crash diagnostics

WinUI turns a managed exception on the UI thread into a native stowed
exception (`0xc000027b` from `Microsoft.UI.Xaml.dll`). That path does not
raise `AppDomain.UnhandledException` and does not write a .NET Runtime 1026
event, so the daily log can stay at startup lines through the crash. App U
records the exception before the process is allowed to exit.

Hooks, registered before `InitializeComponent` on Windows and before
`CreateMauiApp` work on both heads:

| Hook | What it means |
|---|---|
| `Microsoft.UI.Xaml.Application.UnhandledException` | UI-thread exception. `Handled` is left **false**, so the process still terminates. |
| `AppDomain.UnhandledException` | Fatal process exception. Logged and flushed. The process still terminates. |
| `TaskScheduler.UnobservedTaskException` | Background task that nobody observed. Written as a non-fatal failure and marked observed, which is the previous behavior. It does not tear the process down. |
| `AndroidEnvironment.UnhandledExceptionRaiser` | Android fatal path. `Handled` is left **false**. |

`MauiProgram.CreateMauiApp` reports an exception and then rethrows it, so a
startup failure is on disk and the app still fails to launch. A bridge that
refuses to bind, a session that fails to initialize, and a shutdown failure
are caught as they were before (the window stays up, or shutdown continues)
and are also written to a failure file.

Each report is written with `FileOptions.WriteThrough` and `Flush(true)`
**before** Serilog is used. Serilog then gets the same redacted text (`FTL`
for a fatal report, `ERR` for a caught failure) and a fatal report calls
`Log.CloseAndFlush`. If the crash directory cannot be created, or Serilog is
already shut down, the file still goes to the fallback directory. A report
that re-enters the handler writes one short `recursive-*.txt` marker and does
not loop. The same exception object, or the same type/message/stack within
two seconds, is written once. Fatal UI exceptions are not marked handled.

The report contains the timestamp, app name, assembly version, process name
and id, process path, managed thread id and name, source (`ui`, `domain`,
`task`, `startup`, `shutdown`, `android`, `background`), context, framework,
OS, and for the exception: type, message, HResult (decimal and hex),
`Source`, stack, `ToString()`, and inner exceptions (`AggregateException`
includes every inner). It also copies the in-memory operation trail
(startup, bridge, turn, `tool.start` / `tool.end` / `tool.failed` with the
tool name and result length only).

Not written by this path: prompts, tool arguments, tool results, SOUL text,
tokens, API keys, or the process environment. Values that look like
`api_key=…`, `Bearer …`, JWTs, `sk-…` keys, JSON fields such as `args`,
`content`, `query`, `prompt`, or `[TOOL name result: …]` are replaced.
Review a file before sharing it anyway. The bridge bind rule is unchanged:
Tailscale only, unless `ALICEVOICE_BRIDGE_ALLOW_UNSAFE_BIND=1`.

### Where the files are

Windows (same folders the WinUI app uses):

| What | Path |
|---|---|
| Daily log | `%LOCALAPPDATA%\AliceVoice\logs\alice-voice-YYYYMMDD.log` (14 kept, flushed every event) |
| Fatal report | `%LOCALAPPDATA%\AliceVoice\crash\crash-YYYYMMDD-HHMMSSfff-<source>-p<pid>-n<seq>.txt` |
| Caught startup / shutdown / task failure | `%LOCALAPPDATA%\AliceVoice\crash\failure-YYYYMMDD-HHMMSSfff-<source>-p<pid>-n<seq>.txt` |
| Handler re-entry marker | `%LOCALAPPDATA%\AliceVoice\crash\recursive-YYYYMMDD-HHMMSSfff.txt` |
| Fallback when `crash` cannot be written | `%TEMP%\AliceVoice\crash\` (same file names) |

32 diagnostic files are kept in each directory; older `crash-`, `failure-`,
and `recursive-` files are deleted. Android uses the same names under the
app private directory: `<files>/logs` and `<files>/crash`, with the same
temp fallback.

### Checking a crash on the Beast

This repo does not install onto the Beast. After you copy a Windows build
there and the process dies:

1. Open the newest file in `%LOCALAPPDATA%\AliceVoice\crash\`. It should name
   the exception type, message, HResult, stack, and inners, plus breadcrumbs
   such as `tool.start honcho_search` and, if the tool returned,
   `tool.end honcho_search chars=<n>`.
2. The daily log should now contain a `[FTL]` line beginning
   `Diagnostic ui` (or `domain` / `startup`) and the same report. A crash
   that previously left only startup lines is the case this is meant to catch.
3. Windows can still write Application Error 1000 (`Microsoft.UI.Xaml.dll`,
   `0xc000027b`). That event is expected. The managed exception is in the
   crash file, not in a .NET Runtime 1026 event.
4. Confirm the file does not contain the prompt, the tool arguments, or the
   tool result body.

Build the Windows head on Windows (this Linux environment has no Windows
SDK, so it cannot produce that binary):

```powershell
dotnet workload install maui
dotnet test universal/tests/AliceVoice.Universal.Core.Tests/AliceVoice.Universal.Core.Tests.csproj -c Debug
dotnet build universal/src/AliceVoice.Universal.App/AliceVoice.Universal.App.csproj -c Release -f net10.0-windows10.0.19041.0
dotnet publish universal/src/AliceVoice.Universal.App/AliceVoice.Universal.App.csproj -c Release -f net10.0-windows10.0.19041.0 -p:RuntimeIdentifier=win-x64
```

The publish output is under
`universal/src/AliceVoice.Universal.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`.
Copy that folder onto the Beast yourself (the running copy has been
`D:\Apps\AliceVoice.AppU`). Do not point the bridge at a public address.

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
