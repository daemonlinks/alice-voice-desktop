#!/usr/bin/env python
"""faster-whisper STT bridge for the Alice Voice native app.

Reads raw 16 kHz mono Int16 PCM from stdin, transcribes on the GPU
(faster-whisper / CTranslate2, int8_float32 for Pascal), and writes the
transcript to stdout. The .NET app shells out to this for the local Ollama
backend's STT — magnitude faster than the relay round-trip.

Usage:
    python faster_whisper_stt.py [--model base] [--device cuda] [--lang en]
        < raw.pcm > transcript.txt
"""
import argparse
import io
import struct
import sys

import numpy as np
from faster_whisper import WhisperModel


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="base")
    ap.add_argument("--device", default="cuda")
    ap.add_argument("--lang", default="en")
    ap.add_argument("--compute", default="int8_float32")
    args = ap.parse_args()

    # Read all raw Int16 PCM from stdin.
    raw = sys.stdin.buffer.read()
    if not raw:
        print("", flush=True)
        return 0

    n = len(raw) // 2
    pcm = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0

    model = WhisperModel(args.model, device=args.device, compute_type=args.compute)
    segments, _ = model.transcribe(pcm, language=args.lang, beam_size=1)

    text = "".join(seg.text for seg in segments).strip()
    print(text, flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
