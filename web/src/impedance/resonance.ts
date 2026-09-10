/**
 * The numbers an impedance curve gives up once you look at its shape rather than at one frequency:
 * where it is smallest, and — for anything with a motional resonance — the classic driver
 * parameters that fall out of the peak and its skirts.
 *
 * Pure, so it can be tested: nothing in a `.vue` is reachable under the node environment vitest
 * runs in, which is why every other piece of impedance arithmetic (`equivalent.ts`, `standard.ts`)
 * lives out here too.
 *
 * **Everything here reads the same array the chart draws as its line** — the per-column maximum. The
 * cursor readout and the trace's own `peakDbOhms` are that array too, so the resonance block cannot
 * disagree with the peak already on the panel. (For a stepped sweep the question does not arise:
 * every column holds one measured point, so min and max are the same number.)
 */

/** The extremes of a curve, and where they are. */
export interface ImpedanceExtremes {
  minOhms: number
  minFreqHz: number
  maxOhms: number
  maxFreqHz: number
}

/**
 * The smallest and largest |Z| in the trace.
 *
 * <p>The minimum is the one that is missing from the panel and the one an amplifier actually cares
 * about: it is the worst-case load, the point where a speaker draws the most current from the output
 * stage. A nominal "8 Ω" driver routinely dips below 6.</p>
 */
export function findExtremes(freqHz: number[], ohms: number[]): ImpedanceExtremes | null {
  const n = Math.min(freqHz.length, ohms.length)
  if (n === 0) return null

  let lo = -1
  let hi = -1

  for (let i = 0; i < n; i++) {
    const z = ohms[i]
    if (!Number.isFinite(z) || z <= 0) continue
    if (lo < 0 || z < ohms[lo]) lo = i
    if (hi < 0 || z > ohms[hi]) hi = i
  }

  if (lo < 0 || hi < 0) return null

  return { minOhms: ohms[lo], minFreqHz: freqHz[lo], maxOhms: ohms[hi], maxFreqHz: freqHz[hi] }
}

/** A driver's motional resonance, as read off its impedance curve. */
export interface ResonanceReading {
  /** Resonance frequency: where |Z| peaks. */
  fsHz: number
  /** |Z| at resonance. */
  zMaxOhms: number
  /**
   * The voice-coil resistance, taken as the minimum |Z| **above** resonance — the dip between the
   * motional peak and the rise from the coil's own inductance. That is the conventional reading from
   * a sweep, and it is close to but not identical with a DC ohmmeter's answer.
   */
  reOhms: number
  /** Zmax/Re, the height of the resonance. */
  r0: number
  /**
   * The two frequencies either side of resonance where |Z| falls to sqrt(Re·Zmax). Null when the
   * measured band does not reach far enough to contain one of them, which is exactly when a Q read
   * from it would be a lower bound dressed up as a measurement.
   */
  f1Hz: number | null
  f2Hz: number | null
  /** Mechanical, electrical and total Q. Null whenever the skirts are. */
  qms: number | null
  qes: number | null
  qts: number | null
}

/**
 * Smallest peak, relative to Re, that is worth calling a resonance.
 *
 * <p>Below this the arithmetic stops meaning anything rather than merely getting noisy: Qes is
 * <c>Qms/(r0 − 1)</c>, so a peak that barely clears its own baseline divides by almost nothing and
 * reports an enormous Q for what is really a flat curve. 1.5 keeps every real driver (3 to 10 is
 * ordinary) and rejects a resistor, a cable, or a peak that is one noisy column.</p>
 */
const MIN_RESONANCE_RATIO = 1.5

/** Samples that must lie above the peak before the minimum above it can be called Re. */
const MIN_SAMPLES_ABOVE = 3

/**
 * The resonance, or null when the curve has not got one.
 *
 * <p>Null rather than a half-filled reading whenever the shape is not a resonance at all: a peak at
 * either end of the sweep (which is a tail, not a peak), a sweep that stops too soon above the peak
 * to show where the curve comes back down, or a peak too small to clear
 * {@link MIN_RESONANCE_RATIO}.</p>
 */
export function findResonance(freqHz: number[], ohms: number[]): ResonanceReading | null {
  const n = Math.min(freqHz.length, ohms.length)
  if (n < 5) return null

  // Interior only. A curve that is still climbing at the last sample has its maximum there, and
  // calling that a resonance would put fs at the edge of the sweep for every inductor ever measured.
  let peak = -1
  for (let i = 1; i < n - 1; i++) {
    if (!Number.isFinite(ohms[i]) || ohms[i] <= 0) continue
    if (peak < 0 || ohms[i] > ohms[peak]) peak = i
  }

  // `peak < 0` only when every sample was unusable; the loop above already cannot select either end.
  if (peak < 0) return null

  // What actually rejects a rising tail. An inductor's largest interior sample is the last one, and
  // there is then nothing above it to read Re from -- the curve has not come back down, so there is
  // no baseline and no resonance, only a sweep that stopped too soon to know either way.
  if (n - 1 - peak < MIN_SAMPLES_ABOVE) return null

  const fsHz = freqHz[peak]
  const zMaxOhms = ohms[peak]

  let reOhms = Number.POSITIVE_INFINITY
  for (let i = peak + 1; i < n; i++) {
    if (Number.isFinite(ohms[i]) && ohms[i] > 0) reOhms = Math.min(reOhms, ohms[i])
  }

  if (!Number.isFinite(reOhms) || reOhms <= 0) return null

  const r0 = zMaxOhms / reOhms
  if (!(r0 >= MIN_RESONANCE_RATIO)) return null

  // The classic construction: the skirts are read where |Z| is the geometric mean of the peak and
  // the baseline, which is what makes Qms depend only on the width and not on the units.
  const target = Math.sqrt(reOhms * zMaxOhms)

  const f1Hz = crossingBelow(freqHz, ohms, peak, target)
  const f2Hz = crossingAbove(freqHz, ohms, peak, target)

  let qms: number | null = null
  let qes: number | null = null
  let qts: number | null = null

  if (f1Hz !== null && f2Hz !== null && f2Hz > f1Hz) {
    qms = (fsHz * Math.sqrt(r0)) / (f2Hz - f1Hz)
    qes = qms / (r0 - 1)
    qts = (qms * qes) / (qms + qes)
  }

  return { fsHz, zMaxOhms, reOhms, r0, f1Hz, f2Hz, qms, qes, qts }
}

/** Walking down in frequency from the peak, the frequency where the curve first falls to `target`. */
function crossingBelow(freqHz: number[], ohms: number[], peak: number, target: number): number | null {
  for (let i = peak; i > 0; i--) {
    if (ohms[i - 1] <= target && ohms[i] >= target) {
      return interpolate(freqHz[i - 1], ohms[i - 1], freqHz[i], ohms[i], target)
    }
  }
  return null
}

/** And upward. */
function crossingAbove(freqHz: number[], ohms: number[], peak: number, target: number): number | null {
  for (let i = peak; i < ohms.length - 1; i++) {
    if (ohms[i] >= target && ohms[i + 1] <= target) {
      return interpolate(freqHz[i], ohms[i], freqHz[i + 1], ohms[i + 1], target)
    }
  }
  return null
}

/**
 * Where `target` sits between two samples, interpolated in <b>log</b> frequency.
 *
 * <p>Log because the grid is log-spaced — a chirp trace is decimated logarithmically and a stepped
 * one is stepped per octave — so a linear reading between two neighbours at the bottom of the band
 * would land visibly off. Half way from 100 Hz to 400 Hz is 200 Hz, not 250.</p>
 */
function interpolate(f0: number, z0: number, f1: number, z1: number, target: number): number {
  if (z1 === z0) return f0

  const t = (target - z0) / (z1 - z0)
  return Math.exp(Math.log(f0) + t * (Math.log(f1) - Math.log(f0)))
}
