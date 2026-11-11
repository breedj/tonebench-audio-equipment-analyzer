"""Measure the clock difference between two sound cards from a Tonebench stream recording.

The generator plays a tone the OUTPUT device clocks out; the analyzer samples it on the INPUT
device's clock.  Both are told the same nominal rate, so the frequency the recording reports is
commanded * (1 + a) / (1 + b) where a and b are each card's own error -- i.e. the ratio gives
epsilon = a - b directly, which is the figure the whole two-device question turns on.

Frequency is estimated in two stages because neither alone is enough:
  1. an FFT over the whole record puts the tone inside half a bin, and
  2. the phase of a DFT evaluated at that frequency, segment by segment, is a straight line whose
     slope is the residual -- which beats the bin spacing by orders of magnitude and is immune to
     the window's shape.
"""
import json
import struct
import sys
from pathlib import Path

import numpy as np


def read_wav(path):
    """Minimal RIFF reader: Tonebench writes 32-bit float, so the stdlib wave module is no use."""
    raw = Path(path).read_bytes()
    if raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise SystemExit(f"{path}: not a RIFF/WAVE file")

    pos, fmt, data = 12, None, None
    while pos + 8 <= len(raw):
        cid = raw[pos:pos + 4]
        size = struct.unpack_from("<I", raw, pos + 4)[0]
        body = raw[pos + 8:pos + 8 + size]
        if cid == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", body, 0)
        elif cid == b"data":
            data = body
        pos += 8 + size + (size & 1)

    if fmt is None or data is None:
        raise SystemExit(f"{path}: missing fmt or data chunk")

    tag, channels, rate, _, _, bits = fmt
    if tag != 3 or bits != 32:
        raise SystemExit(f"{path}: expected 32-bit float (tag 3), got tag {tag} bits {bits}")

    samples = np.frombuffer(data, dtype="<f4")
    usable = (len(samples) // channels) * channels
    return samples[:usable].reshape(-1, channels).astype(np.float64), rate


def tone_frequency(x, rate, segments=40):
    """Frequency of the dominant tone in x, refined by phase slope. Returns (hz, rel_level_db)."""
    n = len(x)
    win = np.hanning(n)
    spec = np.fft.rfft(x * win)
    mag = np.abs(spec)
    mag[0] = 0.0
    peak = int(np.argmax(mag))
    coarse = peak * rate / n

    # Level of the tone against the rest of the band, as a sanity check that there IS a tone.
    total = np.sum(mag ** 2)
    near = np.sum(mag[max(0, peak - 3):peak + 4] ** 2)
    snr_db = 10 * np.log10(near / max(total - near, 1e-30))

    # Phase slope: DFT at the coarse frequency over each segment, then a straight-line fit.
    seg = n // segments
    if seg < 64:
        return coarse, snr_db
    k = np.arange(seg)
    ref = np.exp(-2j * np.pi * coarse * k / rate)
    phases, centres = [], []
    for s in range(segments):
        chunk = x[s * seg:(s + 1) * seg]
        if len(chunk) < seg:
            break
        phases.append(np.angle(np.dot(chunk, ref)))
        centres.append((s * seg + seg / 2) / rate)

    phases = np.unwrap(np.array(phases))
    centres = np.array(centres)
    slope = np.polyfit(centres, phases, 1)[0]
    return coarse + slope / (2 * np.pi), snr_db


def main():
    folder = Path(sys.argv[1])
    ins = sorted(folder.glob("*-in.wav"))
    if not ins:
        raise SystemExit("no recordings found")
    target = Path(sys.argv[2]) if len(sys.argv) > 2 else ins[-1]
    stem = target.name[:-len("-in.wav")]
    print(f"recording: {stem}")

    info = folder / f"{stem}-info.json"
    if info.exists():
        meta = json.loads(info.read_text(encoding="utf-8-sig"))
        for key in ("sampleRateHz", "inputFrames", "stimulusFrames", "seconds", "droppedBuffers"):
            if key in meta:
                print(f"  {key:16} {meta[key]}")

    for side in ("out", "in"):
        path = folder / f"{stem}-{side}.wav"
        if not path.exists():
            print(f"\n[{side}] missing")
            continue
        audio, rate = read_wav(path)
        secs = len(audio) / rate
        label = "STIMULUS submitted to the output card" if side == "out" else "CAPTURE from the input card"
        print(f"\n[{side}] {label}: {len(audio)} frames, {secs:.3f} s at {rate} Hz nominal")
        for ch in range(audio.shape[1]):
            col = audio[:, ch]
            rms = float(np.sqrt(np.mean(col ** 2)))
            if rms < 1e-7:
                print(f"   ch{ch}: silent (rms {rms:.2e})")
                continue
            hz, snr = tone_frequency(col, rate)
            print(f"   ch{ch}: {hz:.6f} Hz   rms {rms * 1000:.3f} mV   tone/rest {snr:+.1f} dB")


if __name__ == "__main__":
    main()
