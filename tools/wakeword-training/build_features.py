"""Turn synthesised clips into openWakeWord embedding windows (N, 16, 96).

    python build_features.py --clips D:\\code\\oww-train\\clips --models ..\\..\\models\\wakeword \\
        --out D:\\code\\oww-train\\features

Needs `openwakeword` (0.6.x), numpy and scipy. Each window is 2.0 s of 16 kHz audio,
which the shared melspectrogram + embedding models turn into exactly 16 embedding
frames - the input the wake-word classifier sees when streaming.

Augmentation (random per copy): peak level, background white/pink/brown noise at
5-30 dB SNR, babble from other synthesised speech, synthetic room reverb, and a random
position for the phrase end within the last 0-300 ms of the window (positives) or
anywhere (negatives).
"""
import argparse
import pathlib
import wave

import numpy as np
from scipy.signal import fftconvolve, resample_poly

SR = 16000
WINDOW = 32000  # 2.0 s -> 16 embedding frames


def load(path):
    with wave.open(str(path), "rb") as w:
        rate = w.getframerate()
        a = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
    if rate != SR:
        g = np.gcd(rate, SR)
        a = resample_poly(a, SR // g, rate // g).astype(np.float32)
    # trim leading/trailing near-silence so we know where the phrase ends
    nz = np.flatnonzero(np.abs(a) > 0.01)
    if nz.size:
        a = a[max(0, nz[0] - 800): nz[-1] + 800]
    return a


def colored_noise(rng, n):
    kind = rng.integers(3)
    w = rng.standard_normal(n).astype(np.float32)
    if kind == 0:
        return w
    spec = np.fft.rfft(w)
    f = np.arange(spec.size, dtype=np.float32) + 1.0
    spec /= np.sqrt(f) if kind == 1 else f
    x = np.fft.irfft(spec, n).astype(np.float32)
    return x / (np.std(x) + 1e-9)


def reverb(rng, x):
    rt60 = rng.uniform(0.15, 0.7)
    n = int(rt60 * SR)
    t = np.arange(n) / SR
    ir = rng.standard_normal(n).astype(np.float32) * np.exp(-6.9 * t / rt60).astype(np.float32)
    ir[0] = 1.0
    ir /= np.sqrt(np.sum(ir ** 2))
    return fftconvolve(x, ir)[: x.size].astype(np.float32)


def rms(x):
    return float(np.sqrt(np.mean(x ** 2)) + 1e-9)


def place(rng, speech, positive, babble):
    out = np.zeros(WINDOW, dtype=np.float32)
    if positive:
        end = WINDOW - int(rng.uniform(0.0, 0.3) * SR)
        seg = speech[-end:]
        out[end - seg.size: end] = seg
    else:
        if speech.size >= WINDOW:
            s = rng.integers(0, speech.size - WINDOW + 1)
            out[:] = speech[s: s + WINDOW]
        else:
            s = rng.integers(0, WINDOW - speech.size + 1)
            out[s: s + speech.size] = speech
    if rng.random() < 0.5:
        out = reverb(rng, out)
    out *= rng.uniform(0.1, 0.9) / (np.max(np.abs(out)) + 1e-9)
    sig = rms(out)
    if rng.random() < 0.85:
        snr = rng.uniform(5, 30)
        out += colored_noise(rng, WINDOW) * sig / (10 ** (snr / 20))
    if babble and rng.random() < 0.3:
        b = babble[rng.integers(len(babble))]
        b = np.resize(b, WINDOW)
        out += b * sig / rms(b) / (10 ** (rng.uniform(10, 25) / 20))
    return np.clip(out * 32767, -32768, 32767).astype(np.int16)


def noise_window(rng, babble):
    """Background only: coloured noise at a random level, sometimes with distant babble."""
    out = colored_noise(rng, WINDOW) * (10 ** rng.uniform(-3.5, -1.2))
    if babble and rng.random() < 0.5:
        b = np.resize(babble[rng.integers(len(babble))], WINDOW)
        out += b * (10 ** rng.uniform(-2.5, -0.8)) / (np.max(np.abs(b)) + 1e-9)
    return np.clip(out * 32767, -32768, 32767).astype(np.int16)


def embed(af, batch):
    x = np.stack(batch)
    e = af.embed_clips(x, batch_size=64, ncpu=4)
    return e[:, -16:, :].astype(np.float32)


def build(af, rng, files, copies, positive, babble, label):
    out, batch = [], []
    for k, f in enumerate(files):
        speech = load(f)
        if speech.size < 1600:
            continue
        for _ in range(copies):
            batch.append(place(rng, speech, positive, babble))
            if len(batch) == 256:
                out.append(embed(af, batch))
                batch = []
        if (k + 1) % 500 == 0:
            print(f"  {label}: {k + 1}/{len(files)} clips", flush=True)
    if len(batch) > 1:
        out.append(embed(af, batch))
    return np.concatenate(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--clips", required=True)
    ap.add_argument("--models", required=True, help="dir with melspectrogram.onnx and embedding_model.onnx")
    ap.add_argument("--out", required=True)
    ap.add_argument("--pos-copies", type=int, default=3)
    ap.add_argument("--neg-copies", type=int, default=2)
    ap.add_argument("--noise-windows", type=int, default=3000)
    ap.add_argument("--seed", type=int, default=99)
    a = ap.parse_args()

    from openwakeword.utils import AudioFeatures
    m = pathlib.Path(a.models)
    af = AudioFeatures(melspec_model_path=str(m / "melspectrogram.onnx"),
                       embedding_model_path=str(m / "embedding_model.onnx"), ncpu=4)
    rng = np.random.default_rng(a.seed)
    clips = pathlib.Path(a.clips)
    out = pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)

    gen = sorted((clips / "gen").glob("*.wav"))
    babble = [load(f) for f in gen[:: max(1, len(gen) // 300)]]

    pos = build(af, rng, sorted((clips / "pos").glob("*.wav")), a.pos_copies, True, babble, "pos")
    np.save(out / "pos.npy", pos)
    print("pos", pos.shape)
    adv = build(af, rng, sorted((clips / "adv").glob("*.wav")), a.neg_copies, False, babble, "adv")
    # adversarial phrases also placed at the window end, where a positive would be
    adv_end = build(af, rng, sorted((clips / "adv").glob("*.wav")), 1, True, babble, "adv-end")
    gen_f = build(af, rng, gen, a.neg_copies, False, babble, "gen")
    noise = []
    for i in range(0, a.noise_windows, 256):
        batch = [noise_window(rng, babble) for _ in range(min(256, a.noise_windows - i))]
        noise.append(embed(af, batch))
    neg = np.concatenate([adv, adv_end, gen_f, np.concatenate(noise)])
    np.save(out / "neg_synth.npy", neg)
    print("neg_synth", neg.shape)


if __name__ == "__main__":
    main()
