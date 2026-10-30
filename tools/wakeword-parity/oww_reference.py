"""Reference scores from the Python openwakeword package, for the .NET parity test.

Run with a throwaway venv that has `openwakeword==0.6.0` (and onnxruntime):

    python oww_reference.py pad   --out tests/fixtures/wakeword  raw/*.wav
    python oww_reference.py noise --out tests/fixtures/wakeword/silence_noise.wav
    python oww_reference.py score --models models/wakeword --model hey_jarvis \
        --out tests/fixtures/wakeword/parity_hey_jarvis.json tests/fixtures/wakeword/*.wav

`pad` adds 1.5 s of leading and 1.0 s of trailing low-level noise to 16 kHz
mono int16 WAVs (so the reference's random feature-buffer init has scrolled
out before speech starts). `score` streams each WAV through
openwakeword.Model in 1280-sample chunks, exactly like a live mic, and writes
one score per 80 ms frame.
"""
import argparse
import hashlib
import json
import os
import sys
import wave

import numpy as np

RATE = 16000
CHUNK = 1280


def read_wav(path):
    with wave.open(path, "rb") as w:
        if w.getframerate() != RATE or w.getnchannels() != 1 or w.getsampwidth() != 2:
            sys.exit(f"{path}: need 16 kHz mono 16-bit (got {w.getframerate()} Hz, {w.getnchannels()} ch, {8 * w.getsampwidth()} bit)")
        return np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)


def write_wav(path, samples):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(samples.astype(np.int16).tobytes())


def noise(n, seed, amp=40):
    return np.random.default_rng(seed).integers(-amp, amp + 1, n).astype(np.int16)


def cmd_pad(a):
    for i, src in enumerate(a.wavs):
        x = read_wav(src)
        y = np.concatenate([noise(int(RATE * a.lead), 100 + i), x, noise(int(RATE * a.tail), 200 + i)])
        dst = os.path.join(a.out, os.path.basename(src))
        write_wav(dst, y)
        print(f"{dst}: {len(y) / RATE:.2f}s")


def cmd_noise(a):
    write_wav(a.out, noise(int(RATE * a.seconds), 7, a.amp))
    print(a.out)


def sha256(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest().upper()


def cmd_score(a):
    import openwakeword
    from openwakeword.model import Model

    manifest = json.load(open(os.path.join(a.models, "wakeword-models.json")))
    entry = next(m for m in manifest["models"] if m["id"] == a.model)
    mel = os.path.join(a.models, manifest["melspectrogram"]["file"])
    emb = os.path.join(a.models, manifest["embedding"]["file"])
    clf = os.path.join(a.models, entry["file"])

    out = {
        "generator": f"openwakeword {openwakeword.__version__ if hasattr(openwakeword, '__version__') else '?'} via tools/wakeword-parity/oww_reference.py",
        "model": a.model,
        "sha256": {os.path.basename(p): sha256(p) for p in (mel, emb, clf)},
        "chunk": CHUNK,
        "files": {},
    }
    for path in a.wavs:
        m = Model(wakeword_models=[clf], inference_framework="onnx",
                  melspec_model_path=mel, embedding_model_path=emb)
        key = os.path.splitext(os.path.basename(clf))[0]
        x = read_wav(path)
        scores = []
        for i in range(0, len(x) - CHUNK + 1, CHUNK):
            scores.append(round(float(m.predict(x[i:i + CHUNK])[key]), 6))
        out["files"][os.path.basename(path)] = scores
        print(f"{os.path.basename(path)}: {len(scores)} frames, max {max(scores):.4f}")
    with open(a.out, "w", newline="\n") as f:
        json.dump(out, f, indent=1)
    print(a.out)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("pad"); s.add_argument("--out", required=True); s.add_argument("--lead", type=float, default=1.5)
    s.add_argument("--tail", type=float, default=1.0); s.add_argument("wavs", nargs="+"); s.set_defaults(fn=cmd_pad)
    s = sub.add_parser("noise"); s.add_argument("--out", required=True); s.add_argument("--seconds", type=float, default=4.0)
    s.add_argument("--amp", type=int, default=40); s.set_defaults(fn=cmd_noise)
    s = sub.add_parser("score"); s.add_argument("--models", required=True); s.add_argument("--model", required=True)
    s.add_argument("--out", required=True); s.add_argument("wavs", nargs="+"); s.set_defaults(fn=cmd_score)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
