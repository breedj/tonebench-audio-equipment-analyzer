"""Top spectral peaks of a Tonebench capture, to see what is actually arriving on the input."""
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
from measure_clock_drift import read_wav  # noqa: E402


def main():
    audio, rate = read_wav(sys.argv[1])
    n = min(len(audio), 1 << 20)
    print(f"{Path(sys.argv[1]).name}: {len(audio)} frames at {rate} Hz, analysing {n}")

    for ch in range(audio.shape[1]):
        x = audio[:n, ch]
        rms = float(np.sqrt(np.mean(x ** 2)))
        win = np.hanning(n)
        mag = np.abs(np.fft.rfft(x * win)) * 2 / np.sum(win)
        freqs = np.fft.rfftfreq(n, 1 / rate)
        mag[0] = 0

        print(f"\n  ch{ch}: rms {rms * 1000:.3f} mV   peak-to-peak {np.ptp(x) * 1000:.1f} mV")

        # Energy right at 1 kHz, where the tone should be, whatever else is louder.
        band = (freqs > 995) & (freqs < 1005)
        at1k = mag[band].max() if band.any() else 0.0
        print(f"       largest component within 995-1005 Hz: {at1k * 1000:.4f} mV peak")

        order = np.argsort(mag)[::-1]
        shown, seen = 0, []
        for i in order:
            f = freqs[i]
            if any(abs(f - s) < 5 for s in seen):
                continue
            seen.append(f)
            print(f"       {f:9.2f} Hz   {mag[i] * 1000:9.4f} mV peak")
            shown += 1
            if shown >= 8:
                break


if __name__ == "__main__":
    main()
