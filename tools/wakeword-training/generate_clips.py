"""Synthesise positive / adversarial / generic speech clips with Piper.

Run with a Python that has `piper-tts` (>= 1.3) installed:

    python generate_clips.py --voices DIR [DIR ...] --out D:\\code\\oww-train\\clips

Every *.onnx voice (with its .onnx.json) in the given directories is used, and every
speaker of multi-speaker voices up to --max-speakers per voice. Each clip gets a
random length / noise scale so the model sees varied prosody. WAVs are written at the
voice's native rate; build_features.py resamples to 16 kHz.
"""
import argparse
import json
import multiprocessing as mp
import pathlib
import random
import wave

import numpy as np

from phrases import ADVERSARIAL, GENERIC, POSITIVE

_voice_cache = {}


def _voice(path):
    from piper import PiperVoice
    if path not in _voice_cache:
        _voice_cache.clear()
        _voice_cache[path] = PiperVoice.load(path)
    return _voice_cache[path]


def _synth(voice, text, speaker, rng):
    from piper import SynthesisConfig
    cfg = SynthesisConfig(
        speaker_id=speaker,
        length_scale=rng.uniform(0.75, 1.35),
        noise_scale=rng.uniform(0.4, 0.9),
        noise_w_scale=rng.uniform(0.5, 1.1),
    )
    chunks = list(voice.synthesize(text, syn_config=cfg))
    audio = np.concatenate([c.audio_int16_array for c in chunks])
    return audio, chunks[0].sample_rate


def _write(path, audio, rate):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(audio.astype("<i2").tobytes())


def _job(args):
    voice_path, speaker, out, n_pos, n_adv, n_gen, seed = args
    rng = random.Random(seed)
    voice = _voice(voice_path)
    stem = f"{pathlib.Path(voice_path).stem}_s{speaker if speaker is not None else 0}"
    made = 0
    for kind, texts, n in (("pos", POSITIVE, n_pos), ("adv", ADVERSARIAL, n_adv), ("gen", GENERIC, n_gen)):
        for i in range(n):
            text = rng.choice(texts)
            audio, rate = _synth(voice, text, speaker, rng)
            _write(pathlib.Path(out) / kind / f"{stem}_{i:02d}.wav", audio, rate)
            made += 1
    return made


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--voices", nargs="+", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--max-speakers", type=int, default=400)
    ap.add_argument("--pos", type=int, default=8, help="positives per speaker")
    ap.add_argument("--adv", type=int, default=6, help="adversarial clips per speaker")
    ap.add_argument("--gen", type=int, default=3, help="generic sentences per speaker")
    ap.add_argument("--workers", type=int, default=max(1, mp.cpu_count() // 2))
    ap.add_argument("--seed", type=int, default=1234)
    a = ap.parse_args()

    out = pathlib.Path(a.out)
    for kind in ("pos", "adv", "gen"):
        (out / kind).mkdir(parents=True, exist_ok=True)

    rng = random.Random(a.seed)
    jobs = []
    for d in a.voices:
        for onnx in sorted(pathlib.Path(d).glob("*.onnx")):
            cfg = json.loads(pathlib.Path(str(onnx) + ".json").read_text(encoding="utf-8"))
            n = int(cfg.get("num_speakers", 1))
            speakers = [None] if n <= 1 else sorted(rng.sample(range(n), min(n, a.max_speakers)))
            # single-speaker voices get proportionally more clips
            mult = 6 if n <= 1 else 1
            for s in speakers:
                jobs.append((str(onnx), s, str(out), a.pos * mult, a.adv * mult, a.gen * mult, rng.randrange(1 << 30)))
    # group by voice so each worker reuses its loaded model
    jobs.sort(key=lambda j: j[0])
    print(f"{len(jobs)} speaker jobs")
    total = 0
    with mp.Pool(a.workers) as pool:
        for k, made in enumerate(pool.imap(_job, jobs, chunksize=8), 1):
            total += made
            if k % 50 == 0:
                print(f"  {k}/{len(jobs)} speakers, {total} clips", flush=True)
    print(f"done: {total} clips in {out}")


if __name__ == "__main__":
    main()
