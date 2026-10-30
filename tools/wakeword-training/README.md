# Training the "hey alice" wake word

Scripts that produce `models/wakeword/hey_alice_v0.1.onnx`, an openWakeWord-compatible
classifier (16 x 96 embedding frames -> score). The shared `melspectrogram.onnx` and
`embedding_model.onnx` front end is unchanged, so the new model drops into the same
manifest and the same .NET pipeline.

Everything runs on CPU: the classifier has ~50 k parameters and trains in a few
minutes; the slow part is synthesising speech. (Tuner's spare GPU was checked and
was idle, but it is not needed for a model this size, and Tuner has no Python/torch
installed.)

## 1. Environment (throwaway venv, outside the repo)

```powershell
uv venv D:\code\oww-ref-venv --python 3.11
uv pip install --python D:\code\oww-ref-venv\Scripts\python.exe openwakeword==0.6.0 piper-tts
uv pip install --python D:\code\oww-ref-venv\Scripts\python.exe torch onnx --index-url https://download.pytorch.org/whl/cpu --extra-index-url https://pypi.org/simple
```

## 2. Data

* Piper voices (`*.onnx` + `.onnx.json`) from https://huggingface.co/rhasspy/piper-voices:
  `en_US-libritts_r-medium` (904 speakers), `en_GB-vctk-medium` (109), `en_GB-semaine-medium`,
  `en_GB-northern_english_male-medium`, `en_US-lessac-medium`, plus the en_GB voices already
  in the Hermes Piper cache.
* Negative features (CC BY-NC-SA 4.0) from
  https://huggingface.co/datasets/davidscripka/openwakeword_features:
  `validation_set_features.npy` (~11 h, used only to measure false accepts) and the
  first 1.5 GB of `openwakeword_features_ACAV100M_2000_hrs_16bit.npy` (byte range
  `0-1572864127`, ~512 k windows; `train.py` reads a truncated prefix fine):

```powershell
curl.exe -L -o validation_set_features.npy https://huggingface.co/datasets/davidscripka/openwakeword_features/resolve/main/validation_set_features.npy
curl.exe -L -r 0-1572864127 -o acav_part.npy https://huggingface.co/datasets/davidscripka/openwakeword_features/resolve/main/openwakeword_features_ACAV100M_2000_hrs_16bit.npy
```

## 3. Run

```powershell
$py = 'D:\code\oww-ref-venv\Scripts\python.exe'; $w = 'D:\code\oww-train'
& $py generate_clips.py --voices "$env:LOCALAPPDATA\hermes\cache\piper-voices" $w\voices --out $w\clips --workers 10
& $py build_features.py --clips $w\clips --models ..\..\models\wakeword --out $w\features
& $py train.py --features $w\features --acav $w\acav_part.npy --validation $w\validation_set_features.npy --out $w\hey_alice_v0.1.onnx
```

`phrases.py` holds the positive phrasings, the near misses the model must reject
("alice", "hey alex", "malice", "palace", "hey jarvis", ...) and generic sentences.
`train.py` prints recall / false accepts per hour at several thresholds and writes
`<out>.report.json`.

## 4. Test and install

1. Score held-out real-TTS clips (Windows SAPI voices, which the model never saw) with the
   reference implementation, e.g. `tests/fixtures/wakeword/hey_alice_*.wav` (positives) and
   `other_speech_*`, `hey_jarvis_*`, `silence_noise` (negatives):
   `python ..\wakeword-parity\oww_reference.py score --models ..\..\models\wakeword --model hey_alice --out ..\..\tests\fixtures\wakeword\parity_hey_alice.json <wavs>`
   (the parity test then also checks the .NET pipeline against it).
2. Copy the `.onnx` to `models/wakeword/`, add a manifest entry with its SHA-256
   (`Get-FileHash -Algorithm SHA256`), and make it `default`.

## 5. Results for hey_alice_v0.1 (30 October 2026)

Trained with the defaults above (30 000 steps, max negative weight 10, 256 ACAV
windows per batch; ~4 min on the Beast CPU) from 4 392 synthetic positives
(13 176 augmented windows) and 16 176 synthetic negative windows plus 512 k ACAV
windows. Full numbers: `hey_alice_v0.1.report.json`.

* Held-out synthetic recall at threshold 0.5: 0.945; synthetic near-miss FPR 0.042.
* SAPI voices never seen in training (Zira, Hazel): "hey alice" scores 1.00
  (5-6 consecutive frames over 0.5); "hey jarvis", other speech and noise 0.0000.
* False accepts on the 10.7 h openWakeWord validation set (dense speech, music,
  noise), counted as in the app (one per 25-frame refractory window):

  | threshold | patience 1 | 2 | 3 | 4 |
  |---|---|---|---|---|
  | 0.5 | 20.3 /h | 7.2 /h | 1.96 /h | 0.47 /h |
  | 0.9 | 13.9 /h | 4.7 /h | 1.22 /h | 0.37 /h |

  The shipped manifest uses threshold 0.5 with **patience 3**. If it wakes up
  by itself too often, set `ALICEVOICE_WAKEWORD_PATIENCE=4` or lower the
  Sensitivity slider; if it misses you, use patience 2.
* A second run with heavier negatives (`--max-neg-weight 40 --acav-batch 768`,
  40 000 steps) was worse (18-23 FA/h at patience 1) and was discarded.

Synthetic-only training is a starting point: recordings of the real speaker(s) saying
"hey alice" (and a few hours of the room's background audio as negatives) are the
best next improvement.
