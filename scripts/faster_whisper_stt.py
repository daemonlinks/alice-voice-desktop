#!/usr/bin/env python
"""faster-whisper STT bridge for the Alice Voice native app.

Two modes:

* One-shot (default): read ALL raw 16 kHz mono Int16 PCM from stdin,
  transcribe, print the transcript (UTF-8) to stdout and exit.

      python faster_whisper_stt.py [--model base] [--device cuda] < raw.pcm

* Server (--server): load the model ONCE, keep it warm, and transcribe one
  utterance per request. The app keeps this process alive for its lifetime.

      request : 4-byte little-endian uint32 byte length, then that many bytes
                of Int16 PCM (16 kHz mono). Length 0 = ping.
      response: one JSON line per request on stdout:
                {"text": "...", "ms": 123}          or {"error": "..."}
      startup : {"ready": true, "device": "cuda", "device_index": 0, ...}

  The process exits cleanly when stdin closes (i.e. when the app exits or
  crashes), so it never orphans GPU memory.

GPU pinning: the app sets CUDA_DEVICE_ORDER=PCI_BUS_ID and
CUDA_VISIBLE_DEVICES=<chosen GPU> (never the render GPU 0), so inside this
process the voice GPU is always device_index 0.
"""
import argparse
import json
import os
import struct
import sys
import time

import numpy as np


def _pcm_to_float(raw: bytes) -> "np.ndarray":
    if len(raw) % 2:
        raw = raw[:-1]
    return np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0


def _transcribe(model, pcm, lang: str) -> str:
    segments, _ = model.transcribe(pcm, language=lang, beam_size=1, vad_filter=False)
    return "".join(seg.text for seg in segments).strip()


def _read_exact(stream, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = stream.read(n - len(buf))
        if not chunk:
            return bytes(buf)
        buf.extend(chunk)
    return bytes(buf)


def serve(args, out) -> int:
    from faster_whisper import WhisperModel

    t0 = time.time()
    model = WhisperModel(args.model, device=args.device,
                         device_index=args.device_index, compute_type=args.compute)
    # Warm the CUDA kernels with a short silent buffer so the first real
    # utterance is fast.
    try:
        _transcribe(model, np.zeros(16000, dtype=np.float32), args.lang)
    except Exception as e:  # warm-up failure is not fatal
        print(f"warm-up failed: {e}", file=sys.stderr, flush=True)

    def send(obj) -> None:
        out.write((json.dumps(obj) + "\n").encode("utf-8"))
        out.flush()

    send({"ready": True, "device": args.device, "device_index": args.device_index,
          "model": args.model, "compute": args.compute,
          "cuda_visible_devices": os.environ.get("CUDA_VISIBLE_DEVICES", ""),
          "load_ms": int((time.time() - t0) * 1000)})

    stdin = sys.stdin.buffer
    while True:
        header = _read_exact(stdin, 4)
        if len(header) < 4:
            return 0  # stdin closed: parent exited
        (length,) = struct.unpack("<I", header)
        if length == 0:
            send({"text": "", "ms": 0})
            continue
        raw = _read_exact(stdin, length)
        if len(raw) < length:
            return 0
        t1 = time.time()
        try:
            text = _transcribe(model, _pcm_to_float(raw), args.lang)
            send({"text": text, "ms": int((time.time() - t1) * 1000)})
        except Exception as e:
            send({"error": str(e)})


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="base")
    ap.add_argument("--device", default="cuda")
    ap.add_argument("--device_index", type=int, default=0)
    ap.add_argument("--lang", default="en")
    ap.add_argument("--compute", default="int8_float32")
    ap.add_argument("--server", action="store_true")
    args = ap.parse_args()

    # Keep the protocol channel clean: anything a library prints goes to stderr.
    out = sys.stdout.buffer
    sys.stdout = sys.stderr

    if args.server:
        return serve(args, out)

    raw = sys.stdin.buffer.read()
    if not raw:
        out.write(b"\n")
        out.flush()
        return 0

    from faster_whisper import WhisperModel
    model = WhisperModel(args.model, device=args.device,
                         device_index=args.device_index, compute_type=args.compute)
    text = _transcribe(model, _pcm_to_float(raw), args.lang)
    out.write((text + "\n").encode("utf-8"))
    out.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
