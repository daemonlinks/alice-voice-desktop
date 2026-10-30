# Wake-word models: sources and licences

These files are loaded by Alice Voice's wake-word engine (an openWakeWord
pipeline re-implemented in .NET, see `src/AliceVoice.Core/WakeWord`). Each
file is checked against the SHA-256 in `wakeword-models.json` before it is
loaded; a mismatch is refused.

| File | Source | SHA-256 | Licence |
|---|---|---|---|
| `melspectrogram.onnx` | openWakeWord v0.5.1 release | `BA2B0E0F8B7B875369A2C89CB13360FF53BAC436F2895CCED9F479FA65EB176F` | CC BY-NC-SA 4.0 (openWakeWord pre-trained models) |
| `embedding_model.onnx` | openWakeWord v0.5.1 release (derived from Google's `speech_embedding`, Apache-2.0) | `70D164290C1D095D1D4EE149BC5E00543250A7316B59F31D056CFF7BD3075C1F` | CC BY-NC-SA 4.0 (openWakeWord pre-trained models) |
| `hey_alice_v0.1.onnx` | Trained for Alice Voice with `tools/wakeword-training` (Piper TTS synthetic clips, openWakeWord ACAV100M negative features) - default | `64720ED83FADE6C77078E9F48FB647F248085C9675B10CC4FD1597F4BAC5A8C7` | CC BY-NC-SA 4.0 (derivative of the openWakeWord feature models and the CC BY-NC-SA ACAV100M features) |
| `hey_jarvis_v0.1.onnx` | openWakeWord v0.5.1 release | `94A13CFE60075B132F6A472E7E462E8123EE70861BC3FB58434A73712EE0D2CB` | CC BY-NC-SA 4.0 |

Release: https://github.com/dscripka/openWakeWord/releases/tag/v0.5.1

- openWakeWord **code** is Apache-2.0 (David Scripka). Alice Voice does not
  ship openWakeWord's code; the pipeline was re-implemented from it.
- openWakeWord's **pre-trained models** (the melspectrogram, embedding and hey_jarvis files) are
  **Creative Commons Attribution-NonCommercial-ShareAlike 4.0**
  (https://creativecommons.org/licenses/by-nc-sa/4.0/). They may not be used
  commercially, and adaptations must be shared under the same licence.
  Attribution: openWakeWord by David Scripka, https://github.com/dscripka/openWakeWord
- `hey_alice` (default) was trained by us, see `tools/wakeword-training` and
  `tools/wakeword-training/hey_alice_v0.1.report.json`. `hey_jarvis` is kept
  as the original interim test model (`ALICEVOICE_WAKEWORD_MODEL=hey_jarvis`). A model trained by us with
  openWakeWord's feature models is a derivative of those models and is
  therefore also distributed under CC BY-NC-SA 4.0.

## Adding or replacing a model

Drop the `.onnx` next to this file (or in `%ProgramData%\AliceVoice\wakeword`
with a copy of this manifest), add an entry with its SHA-256
(`Get-FileHash -Algorithm SHA256 file.onnx`), and either make it the
`default` or select it with `ALICEVOICE_WAKEWORD_MODEL=<id>`.
