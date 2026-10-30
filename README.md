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

`AliceVoice.Inference` is also where the wake-word ONNX runner lives; that part
IS used by the app and runs on the CPU only (ONNX Runtime CPU provider), so it
never touches a GPU. The wake-word models are in `models/wakeword/`, the
training scripts in `tools/wakeword-training/`.

## Speech-to-text (push-to-talk)

Hold the talk button: audio is recorded while held and transcribed once on
release by a single warm `faster_whisper_stt.py --server` process (default
model `large-v3`, `int8_float32`). The Python used is, in order:
`ALICEVOICE_PYTHON` / `PythonPath`, the Hermes venv
(`%HERMES_HOME%\hermes-agent\venv\Scripts\python.exe`), then `python.exe`
on PATH — the Microsoft Store `WindowsApps` stub is always skipped.
The script is looked up in `ALICEVOICE_SCRIPTS` / `ScriptsDir`, then in a
`scripts\` folder next to the exe (copied at build) or in a parent folder (repo).

## Wake word (hands-free)

Switch the voice panel to **Wake** mode and say **"hey alice"**, then your
request: the app records until you stop talking (energy VAD, ~0.9 s of
silence), then runs exactly the same transcribe -> reply -> speak turn as
push-to-talk. Detection is paused while a turn is recording, thinking or
speaking (so Alice does not wake herself), and resumes afterwards.
Push-to-talk keeps working; holding it while in Wake mode simply
starts a normal PTT turn. The "Wake word:" label shows which wake word is
active (an interim model is marked "(interim test model)"). The
**Sensitivity** slider is `1 - threshold` (higher triggers more easily).
Wake listening is off by default and the Wake button is disabled when no model
is installed.

The engine is a .NET re-implementation of the openWakeWord 0.6.0 streaming
pipeline (1280-sample / 80 ms chunks -> `melspectrogram.onnx` ->
`embedding_model.onnx` -> 16 x 96 features -> classifier), on ONNX Runtime
CPU. `tests/AliceVoice.App.Tests/WakeWordParityTests.cs` compares it frame by
frame with the Python package on SAPI recordings
(`tests/fixtures/wakeword`, reference scores from
`tools/wakeword-parity/oww_reference.py`): max difference < 0.001.

Models: `models/wakeword/wakeword-models.json` lists each file with its
SHA-256; every file is hashed before loading and a mismatch is refused.
The folder is searched in this order: `WakeWordDir` /
`ALICEVOICE_WAKEWORD_DIR`, `%ProgramData%\AliceVoice\wakeword`,
`models\wakeword` next to the exe (copied at build), then `models\wakeword` in
a parent folder (repo checkout). The default model is `hey_alice`
(`hey_alice_v0.1.onnx`, threshold 0.5, patience 3 frames), trained from Piper
synthetic speech with `tools/wakeword-training` (see its README and
`hey_alice_v0.1.report.json`). openWakeWord's `hey_jarvis` is still shipped as
the interim/test model (`ALICEVOICE_WAKEWORD_MODEL=hey_jarvis`).

Licences: openWakeWord's code is Apache-2.0 (re-implemented here, not
bundled). Its pre-trained models (`melspectrogram`, `embedding_model`,
`hey_jarvis`) and our `hey_alice` (a derivative of them and of the
CC BY-NC-SA ACAV100M features) are **CC BY-NC-SA 4.0 - non-commercial**.
See `models/wakeword/NOTICE.md`.

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

Alice LOCAL uses **only** its own SOUL file,
`%ProgramData%\AliceVoice\alice_local\SOUL.md` (`ALICEVOICE_SOUL_LOCAL`).
It is machine-wide so the Windows app and the Alice Voice web relay (which runs
as a service) can share one file. No agent SOUL (any Hermes
`profiles\*\SOUL.md`) is ever read, used as a fallback, or written; paths
under Hermes `profiles` are refused. If the file does not exist, a neutral
starter template is created and the editor shows a notice. Before each save
the previous file is copied to `alice_local\backups\SOUL.yyyyMMdd-HHmmss-fff.md`,
the new text is written atomically, and Alice LOCAL reloads it.

## Configuration

Settings come from `%LOCALAPPDATA%\AliceVoice\appsettings.json` (optional,
property names as in `AliceVoiceOptions`), overridden by environment variables:

`ALICEVOICE_PYTHON`, `ALICEVOICE_SCRIPTS`, `ALICEVOICE_SOUL_LOCAL`,
`ALICEVOICE_BACKGROUNDS`, `ALICEVOICE_PIPER`,
`ALICEVOICE_PIPER_VOICES`, `ALICEVOICE_ALLTALK_URL`, `ALICEVOICE_OLLAMA_URL`,
`ALICEVOICE_HERMES_URL`, `ALICEVOICE_STT_MODEL`, `ALICEVOICE_STT_COMPUTE`,
`ALICEVOICE_STT_CPU_MODEL`, `ALICEVOICE_STT_GPU`, `ALICEVOICE_UPDATE_URL`,
`ALICEVOICE_WAKEWORD_DIR`, `ALICEVOICE_WAKEWORD_MODEL`,
`ALICEVOICE_WAKEWORD_THRESHOLD`, `ALICEVOICE_WAKEWORD_PATIENCE`,
`ALICEVOICE_WAKE_END_SILENCE_MS`, `ALICEVOICE_WAKE_SPEECH_RMS`,
`ALICEVOICE_WAKE_NO_SPEECH_MS`, `ALICEVOICE_WAKE_MAX_SECONDS`.
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
"Update available" and never runs a download automatically. The app version
comes from `Directory.Build.props`.

### Update signing

`version.json` must be accompanied by `version.json.p7s`: a detached CMS/PKCS#7
signature (SHA-256) over the exact bytes of `version.json`. The updater fetches
it from the manifest URL with `.p7s` appended and only offers a newer release
when all of these hold (`src/AliceVoice.Core/Updates/UpdateSignature.cs`):

- the signature decodes, has exactly one signer, and is valid for the manifest bytes;
- the signer certificate has the Code Signing EKU (1.3.6.1.5.5.7.3.3) and digitalSignature key usage;
- it chains to the embedded **daemonlinks-AHC-CA** root, pinned by thumbprint
  `7ED67FFF3DF7360AA24E4DE1C01B300BFEBAFF0B` (custom root trust only; the Windows
  root store is not consulted, no certificate downloads);
- the signer certificate is within its validity period.

Unsigned, tampered, wrongly issued, or non-code-signing signatures mean the
update is not offered (logged as a warning). Because the signed manifest holds
the package `sha256`, the downloaded package is authenticated through the
existing hash checks (which, together with the `https` requirement, stay in place).

**Revocation (offline-tolerant).** Revocation is checked online with a 10 s
timeout. The CA publishes its CRL only via LDAP (`CN=CDP` in AD), so outside
the domain the status is usually *unknown*. `RevocationStatusUnknown` and
`OfflineRevocation` are tolerated and logged ("revocation not checked");
a certificate that is actually **revoked**, or any other chain error, is
rejected. Expired signer certificates are rejected, so re-sign `version.json`
with the renewed certificate before the old one expires.

### Releasing (signing `version.json`)

The signing certificate comes from the **AliceVoice Code Signing** template on
`daemonlinks-AHC-CA` (RSA 3072, 2 years, private key non-exportable, enrolment
limited to `DAEMONLINKS\ahc`). The key never leaves the Windows certificate
store; the tool only asks the store to sign.

1. One-time / on renewal, as `daemonlinks\ahc` on a domain machine:
   ```powershell
   Get-Certificate -Template AliceVoiceCodeSigning -CertStoreLocation Cert:\CurrentUser\My
   Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
     ? Issuer -like 'CN=daemonlinks-AHC-CA*' | fl Thumbprint, Subject, NotAfter
   ```
   (If Credential Manager holds a saved domain credential for `AHC-CORE`, Windows
   uses it for the enrolment and the certificate is issued to that account
   instead; remove it for the enrolment.)
2. Build and publish the package, then put its version, `https` URL and
   SHA-256 (`(Get-FileHash <package>).Hash.ToLower()`) in `version.json`.
3. Sign and verify:
   ```powershell
   dotnet run --project tools/AliceVoice.ReleaseSigner -c Release -- sign --thumbprint <THUMBPRINT> --file version.json
   dotnet run --project tools/AliceVoice.ReleaseSigner -c Release -- verify --file version.json
   ```
   `sign` writes `version.json.p7s` and immediately verifies it against the
   pinned root (exit code 0 = valid). The signature covers the LF form of
   `version.json` (what git stores and `raw.githubusercontent.com` serves);
   `sign` converts a CRLF working copy to LF first, and `verify` checks the
   LF form.
4. Commit `version.json` and `version.json.p7s` together (via PR). Any later
   content edit to `version.json` invalidates the signature; re-run step 3.

## Build

```powershell
dotnet build AliceVoice.sln -c Debug -p:Platform=x64
dotnet test  AliceVoice.sln -c Debug -p:Platform=x64
```

Requires: .NET SDK 10, Visual Studio 18 (WinUI workload), Windows App SDK 2.5.1;
for STT, a Python with `faster-whisper` and an NVIDIA driver (`nvidia-smi`).
The Ollama integration test skips itself when Ollama is not running.
