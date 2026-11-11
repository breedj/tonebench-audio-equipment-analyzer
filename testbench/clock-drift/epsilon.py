"""Clock difference between the two cards, from the 1 kHz tone in a Tonebench recording.

Two corrections over the first attempt, both of which it got wrong:

  * the tone is NOT the loudest thing in the capture -- a 60 Hz hum ladder is, at eight times its
    amplitude -- so the coarse search is restricted to a window around the commanded frequency
    rather than taking the global maximum; and
  * the recording ends with the generator silenced, and segments of silence contribute random
    phase that drags the straight-line fit (it read the stimulus as 1000.24 Hz, 242 ppm off a
    figure that is exact by construction).  So the tail is trimmed, and the stimulus file doubles
    as the check: it must come back at the commanded frequency to the microhertz.
"""
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
from measure_clock_drift import read_wav  # noqa: E402

DEFAULT_COMMANDED_HZ = 1000.0

# One analysis record, for turning ppm into something the measurement screens can be judged by.
RECORD_SAMPLES = 131072
# PortAudio's output ring on the bench this was written against, as a time budget.
RING_SECONDS = 0.180

# The two complaints, both empirical separators rather than derived bounds -- the measured figures
# they came from are in README.md, and they are wide: across ten recordings of one bench, every
# epsilon worth believing had the captured tone at -0.7 dB or better and every one that was
# nonsense had it at -27.4 dB or worse, so -10 lands in a 17 dB gap with nothing near it.
MAX_SELF_CHECK_PPM = 1.0
MIN_DOMINANCE_DB = -10.0


def tone_near(x, rate, want, halfwidth=20.0, segments=60):
    """Frequency of the tone nearest `want`, refined by phase slope.

    Returns (hz, amp_mv_peak, dominance_db) -- the last being the tone against everything else in
    the record, which is the check the stimulus self-check cannot give. A capture whose tone is
    buried in hum passes the self-check (the stimulus file is clean whatever the cable did) and
    still reports an epsilon that is mostly hum: measured on this bench, a 2.2 mV tone under a
    17 mV hum ladder read -11 ppm where the same pair at 150 mV read -104 ppm.
    """
    n = len(x)
    win = np.hanning(n)
    mag = np.abs(np.fft.rfft(x * win)) * 2 / np.sum(win)
    freqs = np.fft.rfftfreq(n, 1 / rate)

    band = (freqs > want - halfwidth) & (freqs < want + halfwidth)
    idx = np.where(band)[0]
    peak = idx[int(np.argmax(mag[idx]))]
    coarse = freqs[peak]

    power = mag ** 2
    power[0] = 0.0
    near = float(np.sum(power[max(0, peak - 3):peak + 4]))
    rest = float(np.sum(power)) - near
    dominance = 10 * np.log10(near / max(rest, 1e-30))

    # **The reference has to run on the ABSOLUTE sample index, not restart per segment.** Restarted,
    # each segment's phase advances by 2*pi*f*seg/rate rather than by the residual 2*pi*(f-coarse),
    # which aliases and gives a slope with no relation to the error -- it read a stimulus that is
    # 1000.000000 Hz by construction as 1000.638 Hz, 638 ppm out.
    seg = n // segments
    k = np.arange(seg)
    phases, centres = [], []
    for s in range(segments):
        start = s * seg
        ref = np.exp(-2j * np.pi * coarse * (start + k) / rate)
        phases.append(np.angle(np.dot(x[start:start + seg], ref)))
        centres.append((start + seg / 2) / rate)

    slope = np.polyfit(np.array(centres), np.unwrap(np.array(phases)), 1)[0]
    return coarse + slope / (2 * np.pi), mag[peak] * 1000, dominance


def main():
    if len(sys.argv) < 3:
        raise SystemExit(
            "usage: epsilon.py <recordings folder> <recording stem> [frames to keep] [commanded Hz]\n"
            "  stem is the file name without the -in.wav / -out.wav suffix"
        )
    folder = Path(sys.argv[1])
    stem = sys.argv[2]
    # Trim: the generator is silenced before the stream stops, and silence has no phase.
    keep = int(sys.argv[3]) if len(sys.argv) > 3 else 2_000_000
    commanded = float(sys.argv[4]) if len(sys.argv) > 4 else DEFAULT_COMMANDED_HZ

    results, rates, weak = {}, {}, []
    for side in ("out", "in"):
        audio, rate = read_wav(folder / f"{stem}-{side}.wav")
        rates[side] = rate
        x = audio[:keep, 0]
        hz, amp, dominance = tone_near(x, rate, commanded)
        results[side] = hz
        if dominance < MIN_DOMINANCE_DB:
            weak.append((side, dominance))
        what = "stimulus submitted to the output card" if side == "out" else "captured on the input card"
        print(f"[{side}] {what}")
        print(f"      {len(x)} frames ({len(x)/rate:.2f} s)   tone {hz:.6f} Hz   "
              f"{amp:.4f} mV peak   {dominance:+.1f} dB over the rest of the band")

    print()
    err_out = (results["out"] - commanded) / commanded * 1e6
    print(f"stimulus vs commanded {commanded:.3f} Hz : {err_out:+.3f} ppm   (a check on this method, not the capture)")
    if abs(err_out) > MAX_SELF_CHECK_PPM:
        print(f"  *** the stimulus reads {abs(err_out):.0f} ppm off the figure given, which it cannot be:")
        print("      it was synthesised at that frequency. Two causes, and they look identical here.")
        print("      Either the method is wrong on this file -- trim the silent tail with `frames` --")
        print("      or the figure is not what was played: Tonebench SNAPS a tone to a coherent bin,")
        print("      so pass the played frequency from the status bar, not the one that was typed. ***")
    for side, dominance in weak:
        print(f"  *** the {side} tone is only {dominance:+.1f} dB over the rest of its own record, under "
              f"{MIN_DOMINANCE_DB:+.0f}:")
        print("      whatever is louder than it is what the phase slope measured. Run peaks.py on it. ***")

    eps = (results["in"] - results["out"]) / results["out"] * 1e6
    print(f"CAPTURED vs SUBMITTED                  : {eps:+.3f} ppm")
    print()
    print("That is epsilon: how far the input card's clock runs from the output card's.")

    rate = rates["in"]
    for n in (RECORD_SAMPLES, RECORD_SAMPLES * 4):
        secs = n / rate
        slip_us = abs(eps) * secs
        print(f"  over a {n}-sample record ({secs:.2f} s): {slip_us:.1f} us, "
              f"{slip_us * 1e-6 * rate:.2f} samples of slip")

    if eps != 0:
        # A duplex lockstep loop drifts one ring's worth and then glitches, which is what makes a
        # single duplex stream across two cards unusable and stage 1 (one stream each) the answer.
        # RING_SECONDS is what PortAudio handed us when asked for the rings as a time budget --
        # see the sound-card rules in CLAUDE.md -- so re-measure it rather than trusting it here.
        print(f"  a {RING_SECONDS * 1000:.0f} ms ring would slip its whole depth in "
              f"{RING_SECONDS / abs(eps) * 1e6 / 60:.1f} minutes")


if __name__ == "__main__":
    main()
