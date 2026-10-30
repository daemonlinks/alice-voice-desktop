"""Train an openWakeWord-compatible classifier (16x96 embeddings -> score) and export ONNX.

    python train.py --features D:\\code\\oww-train\\features --acav D:\\code\\oww-train\\acav_part.npy \\
        --validation D:\\code\\oww-train\\validation_set_features.npy --out hey_alice_v0.1.onnx

Needs torch (CPU is plenty: the model has ~50k parameters) and onnx.
Negatives = synthesised near-misses/speech/noise plus precomputed ACAV100M features
(huggingface.co/datasets/davidscripka/openwakeword_features, CC BY-NC-SA 4.0).
The ~11 h false-positive validation set from the same dataset is held out and only
used to report false accepts per hour.
"""
import argparse
import json

import numpy as np
import torch
from torch import nn


class Net(nn.Module):
    """Same shape as openWakeWord's default DNN head."""

    def __init__(self, frames=16, dim=96, hidden=32):
        super().__init__()
        self.net = nn.Sequential(
            nn.Flatten(),
            nn.Linear(frames * dim, hidden), nn.LayerNorm(hidden), nn.ReLU(),
            nn.Linear(hidden, hidden), nn.LayerNorm(hidden), nn.ReLU(),
            nn.Linear(hidden, 1), nn.Sigmoid(),
        )

    def forward(self, x):
        return self.net(x)


def load_npy(path):
    a = np.load(path, mmap_mode="r", allow_pickle=False)
    return a


def acav_windows(path):
    """acav_part.npy may be a byte-range prefix of the 17 GB file; read what is complete."""
    with open(path, "rb") as f:
        version = np.lib.format.read_magic(f)
        if version == (1, 0):
            shape, fortran, dtype = np.lib.format.read_array_header_1_0(f)
        else:
            shape, fortran, dtype = np.lib.format.read_array_header_2_0(f)
        offset = f.tell()
        f.seek(0, 2)
        size = f.tell()
    per = int(np.prod(shape[1:])) * dtype.itemsize
    n = min(shape[0], (size - offset) // per)
    return np.memmap(path, dtype=dtype, mode="r", offset=offset, shape=(n,) + tuple(shape[1:]))


def val_windows(v):
    v = np.asarray(v, dtype=np.float32)
    if v.ndim == 3:
        return v
    idx = np.arange(16)[None, :] + np.arange(v.shape[0] - 15)[:, None]
    return v[idx]


@torch.no_grad()
def scores(model, x, bs=8192):
    out = []
    for i in range(0, len(x), bs):
        out.append(model(torch.from_numpy(np.asarray(x[i:i + bs], dtype=np.float32))).squeeze(1).numpy())
    return np.concatenate(out) if out else np.zeros(0)


def false_accepts(s, thr, refractory=12):
    n, last = 0, -10 ** 9
    for i in np.flatnonzero(s >= thr):
        if i - last > refractory:
            n += 1
        last = i
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--features", required=True)
    ap.add_argument("--acav", required=True)
    ap.add_argument("--validation", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--steps", type=int, default=30000)
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--max-neg-weight", type=float, default=10.0)
    ap.add_argument("--acav-batch", type=int, default=256)
    a = ap.parse_args()
    torch.manual_seed(a.seed)
    rng = np.random.default_rng(a.seed)

    pos = np.load(f"{a.features}/pos.npy").astype(np.float32)
    neg = np.load(f"{a.features}/neg_synth.npy").astype(np.float32)
    acav = acav_windows(a.acav)
    print(f"pos {pos.shape} neg_synth {neg.shape} acav {acav.shape} {acav.dtype}")

    def split(x):
        p = rng.permutation(len(x))
        k = int(len(x) * 0.9)
        return x[p[:k]], x[p[k:]]

    pos_tr, pos_te = split(pos)
    neg_tr, neg_te = split(neg)
    acav_n = len(acav)

    model = Net()
    opt = torch.optim.AdamW(model.parameters(), lr=a.lr, weight_decay=1e-3)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=a.lr, total_steps=a.steps)
    bce = nn.BCELoss(reduction="none")
    for step in range(a.steps):
        # negatives weigh more as training progresses (like openWakeWord's schedule)
        neg_w = 1.0 + (a.max_neg_weight - 1.0) * step / a.steps
        xp = pos_tr[rng.integers(len(pos_tr), size=128)]
        xn = neg_tr[rng.integers(len(neg_tr), size=128)]
        xa = np.asarray(acav[np.sort(rng.integers(acav_n, size=a.acav_batch))], dtype=np.float32)
        x = torch.from_numpy(np.concatenate([xp, xn, xa]))
        nn_ = 128 + a.acav_batch
        y = torch.cat([torch.ones(128), torch.zeros(nn_)])
        w = torch.cat([torch.ones(128), torch.full((nn_,), neg_w)])
        p = model(x).squeeze(1)
        loss = (bce(p, y) * w).mean()
        opt.zero_grad()
        loss.backward()
        opt.step()
        sched.step()
        if (step + 1) % 2000 == 0:
            model.eval()
            rec = float((scores(model, pos_te) >= 0.5).mean())
            fp = float((scores(model, neg_te) >= 0.5).mean())
            model.train()
            print(f"step {step + 1}: loss {loss.item():.4f} recall@0.5 {rec:.3f} synth-neg FPR@0.5 {fp:.4f}", flush=True)

    model.eval()
    val = val_windows(load_npy(a.validation))
    hours = len(val) * 0.08 / 3600
    sv = scores(model, val)
    sp = scores(model, pos_te)
    sn = scores(model, neg_te)
    report = {"validation_hours": round(hours, 2), "thresholds": {}}
    for thr in (0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9):
        report["thresholds"][str(thr)] = {
            "recall_heldout_synth": round(float((sp >= thr).mean()), 4),
            "synth_negative_fpr": round(float((sn >= thr).mean()), 5),
            "false_accepts_per_hour": round(false_accepts(sv, thr) / hours, 3),
        }
    print(json.dumps(report, indent=2))

    dummy = torch.zeros(1, 16, 96)
    torch.onnx.export(model, dummy, a.out, input_names=["input"], output_names=["output"],
                      dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}}, opset_version=13,
                      dynamo=False)
    with open(a.out + ".report.json", "w") as f:
        json.dump(report, f, indent=2)
    print("wrote", a.out)


if __name__ == "__main__":
    main()
