# Alice Voice App U — feature parity

Rule for this app: **the same behaviour as the WinUI app, or better.** The
WinUI app (`src/AliceVoice.App`, `AliceVoice.sln`) is not modified. App U is a
.NET MAUI program (Windows and Android only) that references `AliceVoice.Core`
and, on Windows, `AliceVoice.Audio` and `AliceVoice.Inference`.

Linux and Raspberry Pi are out of scope. Avalonia is not used.

Status words used below:

| Word | Meaning |
|---|---|
| Same | The Windows head calls the original type. The phone does the same thing. |
| Windows same | Windows head calls the original type. |
| Bridged | The phone does it by talking to App U on the Beast. The Beast runs the original code. |
| On-device | The phone does it locally, and the result is the same or a documented improvement. |
| Unchanged gap | The WinUI app already does not implement this. App U does not invent it. |
| Owner | Needs a decision recorded at the bottom. |

## Shape

```
WinUI app (untouched) ──► AliceVoice.Core ──► Python STT, Ollama, Hermes, Honcho, Piper, SOUL file
                              ▲
MAUI Windows head ────────────┤  same process, plus an optional HTTP bridge
MAUI Android head ────────────┘  HTTP to that bridge, plus on-device mic / speech / wake word
```

The conversation screen (modes, push-to-talk, wake, moods, profiles, voices,
transcript, remember, SOUL, metrics) lives in `VoiceSession` and is covered by
`AliceVoice.Universal.Core.Tests`. The WinUI window is still the reference for
wording; the session copies those strings (`SessionCopy`).

## Speech-to-text

| | WinUI | Windows App U | Android App U |
|---|---|---|---|
| Engine | One warm `faster_whisper_stt.py --server` process | Same `FasterWhisperSttClient` | `POST /v1/stt` on the bridge, which calls that same client. One GPU process, requests queued |
| Audio | 16 kHz mono Int16, whole utterance | Same | Phone captures 16 kHz mono and sends the PCM |
| Model | `large-v3`, compute `int8_float32` (Pascal has no fast fp16) | Same | Same, because it runs on the Beast |
| CPU fallback | `base` + `int8` when no eligible GPU | Same | The phone has no voice GPU. Without `ALICEVOICE_BEAST_HOST` it refuses instead of pretending to run Whisper locally |
| Language | `en` | Same | Same |
| Startup | Up to 3 minutes; request timeout 90 s | Same | Warm-up is `POST /v1/stt/warmup` |
| Privacy | Logs length and timing, not the transcript | Same | The bridge does not log the PCM or the text |
| Python lookup | `ALICEVOICE_PYTHON`, Hermes venv, then `python.exe` on PATH. The WindowsApps Store stub is skipped | Same `RuntimeLocator` | Not used on the phone |

GPU rule, verified again in the universal tests: index **0 is never used**.
A forced index of 0 is refused and the selector falls through. Otherwise the
eligible GPU (more than 2048 MiB free) with the most free VRAM wins, then the
lower index. On the Beast that is GPU 1. It is not a hardcoded constant.
`ALICEVOICE_STT_GPU=1` still forces GPU 1. `CUDA_DEVICE_ORDER=PCI_BUS_ID` and
`CUDA_VISIBLE_DEVICES` are set by the original client so the render GPU is
invisible to the process.

`AliceVoice.Inference`'s ONNX Whisper + DirectML path stays **unregistered**.
DirectML adapter numbers are not NVIDIA indexes and could land on GPU 0.

## Wake word

| | WinUI | Windows App U | Android App U |
|---|---|---|---|
| Phrase | "hey alice", patience 3, threshold 0.5, from the manifest | `WakeWordSetup` uses the manifest patience and threshold unless overridden. The options helper that defaults patience to 1 is not what the window uses, and App U follows the window | Same numbers, same SHA-256 check |
| Engine | openWakeWord pipeline, ONNX Runtime **CPU**, one thread. Never a GPU | Same `OnnxWakeWordFactory` | Same pipeline against ONNX Runtime 1.31 Android builds (`libonnxruntime.so` is in the APK for arm64-v8a and x86_64). Not yet run on a handset in this environment |
| Models | SHA-256 from `wakeword-models.json` before load. Mismatch is refused | Same locator | Models ship in the APK and are copied to app storage, then checked the same way |
| Search order | `WakeWordDir` / `ALICEVOICE_WAKEWORD_DIR`, `%ProgramData%\AliceVoice\wakeword`, `models\wakeword` next to the exe, then parents | Same, and the Windows project copies `models\wakeword` next to the output | The copied app-storage folder is passed as `WakeWordDir` |
| Interim model | `hey_jarvis` when `ALICEVOICE_WAKEWORD_MODEL=hey_jarvis`, labelled "(interim test model)" | Same label | Same |
| Sensitivity | Slider is `1 - threshold`, clamped 0.1–0.9 | Same, including the two-decimal rounding | Same |
| End of speech | Energy VAD, default 0.9 s silence, RMS 0.015, 5 s with no speech, 30 s cap | Same `EndOfSpeechSettings` from options | Same, on the phone, so VAD does not need a round trip |
| While Alice speaks | Detection pauses for the turn and for about 0.6 s after resume | `VoiceSession` uses the same busy scope | Same, plus a microphone foreground service so Android will not freeze the process when the screen locks |
| Default | Wake is off until the user selects it. The button is disabled when no model loads | Same | Same. If a model file is missing from the package the button stays disabled and the reason is logged |
| Push-to-talk during Wake | Still works. Holding Talk pauses detection | Same | Same |
| Licence | CC BY-NC-SA 4.0. See `models/wakeword/NOTICE.md` | Shipped with the head | Shipped inside the APK |

## Turns, agents, and memory

| | WinUI | Windows App U | Android App U |
|---|---|---|---|
| Push-to-talk | Hold to record, release to send. A tap under 350 ms latches. Keyboard toggles. 120 s cap sends. Under 0.3 s is "Too short" | Same gesture type and the same messages | Same. Press / release / click on the Talk button |
| Text mode | Enter or Send. Empty and disconnected sends are ignored | Same | Same |
| One path | Speech and typing both end in `RunTurnAsync` | Same method | Same method |
| Alice LOCAL | `AliceLocalBackend`: Ollama `/api/chat`, SOUL as the system prompt, rolling 20 messages, up to 4 tool turns | The window's backend is that class | The phone's backend is a proxy. The bridge builds its **own** `AliceLocalBackend` and runs `AskAsync` there, streaming `reply_partial` / `tool_call` / `reply_text`. The tool loop is not reimplemented |
| Style | Mood + profile become `AskOptions` for Alice LOCAL only. Fleet agents keep their Hermes style | Same. Quick 160, natural 320, full 1024 tokens. Mood lines match `ReplyStyle` | The phone sends `maxTokens` and `styleHint`. The Beast's original backend applies them |
| Moods | natural, comfort, playful, flirty, focused. Disabled for fleet, with the same tooltip | Same | Same |
| Profiles | Quick / Natural / Full, same hint strings | Same | Same |
| Fleet roster | Alice LOCAL, then Alice Voice, Alice 2, Alice 3, Alice 4, Alice (Windows), Alice 5/1/7 (WSL), Alice Arena, Alice 6 | Same `FleetRoster` | Same names. Chat is `POST /v1/fleet/send`. The Hermes `API_SERVER_KEY` is read on the Beast and is **not** sent to the phone. `HasApiKey` on the wire is only a boolean |
| Reachability | `GET /p/<profile>/v1/models`, 10 s | Same client | `POST /v1/fleet/reachable` uses that client |
| Reset | Clears the transcript and the model history (`reset` for Alice LOCAL, per-member history for fleet) | Same | Same routes (`/v1/local/command`, `/v1/fleet/reset`) |
| Cancel | A new turn cancels the previous one and stops playback | Same. Playback `Stop()` is wired to the NAudio player | Same, wired to `AudioTrack` |
| Streaming | Alice LOCAL partials update the bubble. A leading `{` is hidden (possible tool call) | The session shows partials. Hiding `{` still happens inside `AliceLocalBackend` | The bridge forwards the events the backend already raised |
| Copy | The WinUI transcript is a selectable text block | Message text is a selectable label. Drag highlights it. Ctrl+C copies the highlight. Right-click Copy uses the highlight, or the whole line when nothing is highlighted. The speaker line is not selectable | The same label. Long-press selects, and the system copy action copies the selection. A drag that has not started a selection still scrolls |
| Remember | "♥ remember" on Alice LOCAL replies, truncated to 1000 chars, `honcho_save`. Disabled for fleet with the same tooltip | Same | `POST /v1/local/remember`, which calls `AliceLocalBackend.RememberAsync` |
| History | One history in the window | The window and the bridge share one `RecordingAliceLocalBackend` and one `SharedTranscript`. Asks are serialized. A phone turn is appended on the Beast and the window redraws it; the phone polls `GET /v1/local/transcript` | Same store. The phone does not keep a second Alice LOCAL history |
| Local Ollama and the alice_voice agent backend | Registered, not selected by the window. Fleet members go through `IFleetChat` | Still registered, still not on the agent picker | Same |
| Context sliders | Key points (2), memory items (1), char limit (80). **Disabled.** Tooltip: context injection is controlled by the agent | Still disabled, same tooltip, same defaults shown | Same. Not implemented, on purpose |

Trigger phrases in Keywords & Tools match the web panel:
`Remember that… · Remember I… · What do you know about me? · Forget that… · Remove that… · Delete that… · I don't like that anymore · What time is it? · Search for…`
Tools: `honcho_search · honcho_save · honcho_forget · time · search (Alice LOCAL)`.
Saying one of those phrases in a turn runs that tool, then Alice speaks the result. A bare "I don't like that anymore" is left for the model, because the fact is in the conversation. The ♥ remember button saves `About Master: …`.

## Honcho and the tool bridge

| | Detail |
|---|---|
| Script | `scripts/alice_local_tools.py`. App U does not fork it. The model is taught to reply with only `{"tool","args"}`. The same phrases also run the tool before the model speaks |
| Honcho | `HONCHO_BASE`, default `http://localhost:8001`. Local server runs with auth disabled (`AUTH_USE_AUTH` false). `HONCHO_API_KEY` is sent only when it is set. No key is hardcoded |
| Workspace | Shared workspace `hermes` (Hermes in the Honcho UI). User peer `AHC`. Alice LOCAL's peer is `alice_local` (`observer` / `observer_id`). She is not a root workspace. The stray root workspace id `alice_local` is never used. `HONCHO_WORKSPACE`, `HONCHO_PEER`, and `HONCHO_AI_PEER` are ignored |
| Web search | Tavily. The key is read from the environment or `%ProgramData%\AliceVoice\.env` (`ALICEVOICE_ENV`). Never logged. Search results are not saved back into Honcho |
| Time | `time` with empty args. The script returns the Beast's local time. It does not call Honcho |
| Where it runs | Always on the Beast, inside `AliceLocalBackend`. The phone never runs Python and never sees the key file |
| Timeout | 60 s per tool, 4 tool turns, then the same "tangled up in my tools" line |

## Speech output

| Provider | WinUI | Windows App U | Android App U |
|---|---|---|---|
| AllTalk | HTTP `127.0.0.1:7851`, `/api/tts-generate`, chunked at 900 chars, plays the WAV's real rate. Default voice `female_07.wav`. Preferred list hit `female_06` | Original `AllTalkTtsProvider` | Same HTTP form, implemented in `AllTalkVoiceProvider`, played on the phone. URL follows the Beast host unless `ALICEVOICE_ALLTALK_URL` is set. No API key |
| Piper | `piper.exe` from the Hermes venv, voice dir `hermes\cache\piper-voices`, default `en_GB-southern_english_female-low`. Missing exe or voice returns without throwing | Original `PiperTtsProvider` | `POST /v1/tts/piper` runs `PiperSynth` (same arguments) on the Beast and returns one WAV. A missing bridge is an error the Test TTS button can show. A missing voice is a 404 and silence, matching a missing local voice |
| SAPI | Windows `System.Speech`, the default provider | Original `SapiTtsProvider` | Still SAPI's number (`1`) is unused on the phone. Android speech is its own provider |
| Test line | "Hello Master. This is a voice quality test." | Same | Same |
| Per-member voices | `%LOCALAPPDATA%\AliceVoice\voice-assignments.json`. Enum stored as a number | Same path and same JSON, so the two Windows apps read one file | The phone's private `voice-assignments.json`. It is not the PC file. Sync is an owner decision |
| Android speech | Not in the WinUI enum | `VoiceProviderKind.AndroidSpeech = 3`, appended so AllTalk stays 0, SAPI stays 1, Piper stays 2. If an assignment file names it, Windows logs that the provider is not on this device and uses SAPI. The file is not rewritten. `VoiceProviderManager` already skips a kind it was not given | Registered on the phone, listed in the provider picker, and saved in the same assignment JSON (`"Provider": 3`) with the Android voice name |
| Default when nothing is saved | The WinUI combo does not force a selection; the manager's default profile is SAPI | App U selects SAPI (or the first provider) so the first test has a voice. Equal or better | SAPI is not registered, so the first registered provider is used (AllTalk, then Android speech, then Piper) |

## SOUL editor

| | WinUI | Windows App U | Android App U |
|---|---|---|---|
| Only file | `%ProgramData%\AliceVoice\alice_local\SOUL.md`, or `ALICEVOICE_SOUL_LOCAL` | `LocalSoulGateway` → `SoulStore` | The editor talks to `GET/PUT /v1/soul`. The bridge calls `SoulStore`. The phone does not create a second SOUL |
| Agent SOULs | Paths under Hermes `profiles` are refused. Never read, never a fallback | Same check, inside `SoulStore` | Same check, on the Beast, before the write |
| Missing file | Neutral starter template, yellow notice, the path shown | Same strings | Same strings. The path in the notice is the Beast path returned by the bridge |
| Save | Timestamped backup `backups\SOUL.yyyyMMdd-HHmmss-fff.md`, atomic temp + replace. Empty text refused | Same | Same. After the write, every Alice LOCAL backend in the Beast process reloads (`SoulReloadHub`: the window's backend and the bridge's backend) |
| Who can open it | Alice LOCAL only. Fleet gets "SOUL editor is only available on Alice LOCAL" | Same | Same |
| Editor | Consolas, multi-line | MAUI `Editor` | Same |

## Services and ports

| Service | Default | Override | Windows App U | Android |
|---|---|---|---|---|
| Hermes API | `http://127.0.0.1:8643` | `ALICEVOICE_HERMES_URL` | Unchanged. Not port 8642 | Through the bridge, so the key stays on the PC. Explicit URL still wins on the Beast |
| Ollama | `http://localhost:11434` | `ALICEVOICE_OLLAMA_URL` | Unchanged | The bridge's Alice LOCAL uses the Beast's Ollama. If `ALICEVOICE_OLLAMA_URL` is set on the phone it is not what Alice LOCAL uses; the bridge uses the PC's options |
| AllTalk | `http://127.0.0.1:7851` | `ALICEVOICE_ALLTALK_URL` | Unchanged | `http://<Beast>:7851` unless the phone sets the URL |
| Honcho | `http://localhost:8001` | `HONCHO_BASE` / `.env` | Unchanged, via the Python script | Only on the Beast, via that script |
| App U bridge | off | `ALICEVOICE_BRIDGE_BIND` must be a Tailscale address (100.64.0.0/10, Tailscale IPv6, MagicDNS name, or `*.ts.net`). `0.0.0.0`, public addresses, and `192.168.x` are refused unless `ALICEVOICE_BRIDGE_ALLOW_UNSAFE_BIND=1`. Clients dial `100.64.237.3` port **8765** first, then `ahc-the-beast` if that fails. They never set `BridgeBind` | Listens only when the bind is accepted. A Windows PC without a bind dials the Beast instead of listening | Client. Settings can change the dial address. Cleartext HTTP inside Tailscale. Does not sync `Mirrored` or the PC voice file |

`BeastHost`, `BridgePort`, and `BridgeBind` may also sit in `appsettings.json`.
They are not fields of `AliceVoiceOptions`, so the WinUI schema is unchanged.
Environment variables win, same as the other settings.

Routes on the bridge: `/v1/health`, `/v1/stt`, `/v1/stt/warmup`, `/v1/soul`,
`/v1/local/connect`, `/v1/local/ask`, `/v1/local/remember`, `/v1/local/command`,
`/v1/fleet/send`, `/v1/fleet/reachable`, `/v1/fleet/reset`, `/v1/tts/piper`,
`/v1/tts/piper/voices`. Bodies are capped at 8 MB. No request logging of
prompts, transcripts, SOUL text, or memory text.

## Self-update

| | WinUI | Windows App U | Android App U |
|---|---|---|---|
| Manifest | `version.json` + `version.json.p7s` | Same `HttpAppUpdater` | Same check, same verifier |
| Trust | One signer, Code Signing EKU, digitalSignature, chain to daemonlinks-AHC-CA only, thumbprint `7ED67FFF3DF7360AA24E4DE1C01B300BFEBAFF0B`. Windows root store is not consulted. Revocation unknown / offline is tolerated and logged. Revoked or any other chain error is rejected | Same class | Same class. The phone does not get a looser check |
| Offer rules | Higher version, https URL, 64-hex sha256, valid signature. Otherwise silence | Same | Same |
| What the UI does | Shows "Update available: {version}". Does **not** download or launch | Same banner. `DownloadUpdateAsync` is not called | The Android head asks the updater for an Android package. A signed `android` object (`version`, https `downloadUrl`, `sha256`) is offered as "Update available: {version} (Android APK)". The app still does not download or install it. With no `android` object, the banner says the manifest only has a Windows package. `version.json` in git is unchanged; the Beast re-signs with `universal/scripts/Publish-AndroidRelease.ps1` |
| Version | `Directory.Build.props` (`0.1.0`) | The MAUI display version is that same `$(Version)` | Same |

`version.json` and `version.json.p7s` were not edited.

## Settings, paths, logging

Settings file: `%LOCALAPPDATA%\AliceVoice\appsettings.json`, then
`ALICEVOICE_*` env. App U on Android reads `appsettings.json` from the app's
private directory instead of `%LOCALAPPDATA%`, because that folder is a Windows
path. Env still wins.

| Env | What it does | App U |
|---|---|---|
| `ALICEVOICE_PYTHON` | python.exe for STT and tools | Windows only, original locator |
| `ALICEVOICE_SCRIPTS` | Folder with the two Python scripts | Windows project copies `scripts\*.py` next to the output. Locator still walks parents |
| `ALICEVOICE_SOUL_LOCAL` | SOUL file | Windows / the Beast bridge. The phone does not set this to a local file |
| `ALICEVOICE_BACKGROUNDS` | Scene images, jpg then png | Windows default is still `~/ahc-the-beast/voice/Mirrored`. Android uses the app `backgrounds` folder unless this is set. The Beast's Mirrored folder is not synced |
| `ALICEVOICE_PIPER`, `ALICEVOICE_PIPER_VOICES` | Piper exe and voices | Windows local, and the bridge's Piper route |
| `ALICEVOICE_ALLTALK_URL`, `ALICEVOICE_OLLAMA_URL`, `ALICEVOICE_HERMES_URL` | Service URLs | Android fills these from `ALICEVOICE_BEAST_HOST` only when they are unset. An explicit value always wins. Windows does **not** retarget them when `ALICEVOICE_BEAST_HOST` is set |
| `ALICEVOICE_STT_MODEL`, `ALICEVOICE_STT_COMPUTE`, `ALICEVOICE_STT_CPU_MODEL`, `ALICEVOICE_STT_GPU` | Whisper | Original client |
| `ALICEVOICE_UPDATE_URL` | Manifest | Original updater |
| `ALICEVOICE_WAKEWORD_DIR`, `ALICEVOICE_WAKEWORD_MODEL`, `ALICEVOICE_WAKEWORD_THRESHOLD`, `ALICEVOICE_WAKEWORD_PATIENCE` | Wake model | `WakeWordSetup` |
| `ALICEVOICE_WAKE_END_SILENCE_MS`, `ALICEVOICE_WAKE_SPEECH_RMS`, `ALICEVOICE_WAKE_NO_SPEECH_MS`, `ALICEVOICE_WAKE_MAX_SECONDS` | VAD | Same clamps as `AliceVoiceOptions` |
| `HERMES_HOME` | Hermes install, default `%LOCALAPPDATA%\hermes` | Original |
| `API_SERVER_KEY` | Else Hermes `.env` | Resolved on the Beast only, when the bridge handles fleet chat |
| `HONCHO_BASE`, `HONCHO_API_KEY`, `TAVILY_API_KEY`, `ALICEVOICE_ENV` | Tool script | Base URL and optional key only. `HONCHO_WORKSPACE`, `HONCHO_PEER`, and `HONCHO_AI_PEER` are ignored so Alice LOCAL stays on workspace `hermes`, peer `alice_local`, user `AHC` |
| `ALICEVOICE_BEAST_HOST`, `ALICEVOICE_BRIDGE_PORT`, `ALICEVOICE_BRIDGE_BIND`, `ALICEVOICE_BRIDGE_ALLOW_UNSAFE_BIND` | App U only | Android defaults the host to `ahc-the-beast`. The bind is Tailscale-only unless the override flag is `1` |

| Path | Role | App U |
|---|---|---|
| `%LOCALAPPDATA%\AliceVoice\logs` | Daily Serilog file, 14 kept, unbuffered | Windows: same directory (`alice-voice-YYYYMMDD.log`). Android: app `logs`, no Event Log |
| Windows Event Log source `AliceVoice` | Warnings and up, if the source can be created | Windows head uses `LoggingBootstrap`, so the same rule. Android never tries |
| `%LOCALAPPDATA%\AliceVoice\crash` | Fatal `crash-*.txt` and caught `failure-*.txt`, 32 kept. Fallback `%TEMP%\AliceVoice\crash` if that directory cannot be written | Windows: beside the logs. Android: `crash` beside the app logs, same fallback. WinUI `UnhandledException` is recorded and **not** marked handled, so a UI exception still terminates. See `universal/README.md` |
| `%ProgramData%\AliceVoice\alice_local\SOUL.md` | The only SOUL | Written only on the Beast |
| `%ProgramData%\AliceVoice\.env` | Optional Tavily / Honcho | Read only by the Python script |
| `%LOCALAPPDATA%\AliceVoice\updates` | Downloaded packages | The updater can still stage a file. The UI does not ask it to |
| `%LOCALAPPDATA%\AliceVoice\voice-assignments.json` | Voices | Shared by the two Windows apps |

Startup line is still "Talk to me, Master. Hold 🎤 Talk while you speak."
The window title is **Alice Voice App U** so it is obvious which process is which.
The badge is `v{version} · yours`.

Other window behaviour the session keeps: connect / disconnect, pipeline lamps,
12-second error banner, event log capped at 500 lines, latency book of 20 turns
and the same summary lines, scene previous / next / random (random needs two
images), VU meter from about -40 dBFS to 0 on a 100 px bar.

## What this run finished

- Parity matrix (this file) and build notes (`README.md`).
- `AliceVoice.Universal.Core`: session, wake setup, voice-assignment file, backgrounds, latency, Beast URL rules, update offer, AllTalk HTTP client, Piper synth, bridge server and client.
- Tests for those (49). Original suite still passes (133 passed, the Ollama live test skipped when Ollama is down).
- MAUI Android head builds. The Debug APK contains the wake models and `libonnxruntime.so`.
- MAUI Windows head is in the same project and is compiled only on Windows (`net10.0-windows10.0.19041.0`). This environment has no Windows SDK, so that target was not built here. It references the original audio and inference projects rather than reimplementing them.

## What is still open

- Run the Windows head on the Beast and confirm a full Talk → GPU STT → Ollama → AllTalk loop against the live services. The code path is the original one; it has not been executed on that machine in this run.
- Run the APK on a handset: mic permission, a wake detection, a bridged turn, SOUL save, remember. The package builds; the handset pass has not happened.
- Pixel-identical layout. App U has its own hierarchy: header, live status lamps, conversation, talk dock, then agent and the collapsed panels. It is not a copy of the WinUI XAML. The scene image is AspectFit on both heads.
- Context sliders, because the WinUI app leaves them disabled.
- The experimental ONNX Whisper engine, because the WinUI app does not register it.
- A handset install of a signed APK. The code will offer one after the Beast re-signs `version.json`. The committed manifest is still the original MSIX pair.

## Owner decisions, as implemented

1. **Bridge address.** Tailscale only. `0.0.0.0`, public IPs, and `192.168.x` are refused unless `ALICEVOICE_BRIDGE_ALLOW_UNSAFE_BIND=1`. The phone defaults to `ahc-the-beast`. A non-loopback prefix still needs `netsh http add urlacl` once on the Beast (see `universal/README.md`).
2. **One conversation.** The bridge process owns the Alice LOCAL history. The window and the phone both read and append it. Turns are serialized.
3. **Android updates.** The manifest schema accepts `android.version`, `android.downloadUrl`, and `android.sha256`. The Windows fields and the current verifier stay. This branch does not change `version.json` or `version.json.p7s`. Re-sign on the Beast with `universal/scripts/Publish-AndroidRelease.ps1` and the CN=AHC certificate `9F5116C32A280552E38F7FA87967EDED6C9C34C2` (valid until 2028-10-29).
4. **Android speech** is `VoiceProviderKind.AndroidSpeech = 3`. It shows in the phone's provider list and can be assigned. Windows ignores that assignment and does not renumber 0, 1, or 2.
5. **No Mirrored sync.** The phone does not copy `~/ahc-the-beast/voice/Mirrored` or the PC's voice-assignment file. AllTalk and Piper still run on the Beast.
