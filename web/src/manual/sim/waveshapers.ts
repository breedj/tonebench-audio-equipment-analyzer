// The simulator's four nonlinear models — `SimulatedDut.Shape` — and a small harmonic analysis
// that runs a sine through one and reads the harmonics off it, which is what the THD screens do
// with a real capture. Constants mirror the `SimulatedDut` fields named beside them.

export const TRIODE_K2 = 0.01
export const TRIODE_K3 = 0.002
export const TRIODE_RAIL_VOLTS = 4

export const CROSSOVER_DEPTH = 0.2
export const CROSSOVER_WIDTH_VOLTS = 0.02

export const CLIP_RAIL_VOLTS = 1
export const CLIP_KNEE_FRACTION = 0.97
export const CLIP_K3 = 4e-5

export const CORE_CORNER_HZ = 30
export const CORE_FLUX_LIMIT_VOLTS = 1.26

/** SE triode: `y = v + k2 v² − k3 v³` on the input limited at the rail. `SimulatedDut.TriodeK2`. */
export function triode(x: number): number {
  const v = Math.min(Math.max(x, -TRIODE_RAIL_VOLTS), TRIODE_RAIL_VOLTS)
  return v + TRIODE_K2 * v * v - TRIODE_K3 * v * v * v
}

/** Class AB: a gain notch around zero, `y = x (1 − a e^(−x²/2w²))`. */
export function crossover(x: number): number {
  const w = CROSSOVER_WIDTH_VOLTS
  return x * (1 - CROSSOVER_DEPTH * Math.exp(-(x * x) / (2 * w * w)))
}

/** `SignalSynthesizer.SoftClip`: linear to the knee, then a tanh into the rail. */
export function softClip(sample: number, clipPeakV: number, kneeFraction: number): number {
  const threshold = clipPeakV * kneeFraction
  const magnitude = Math.abs(sample)
  if (magnitude <= threshold) return sample
  const headroom = clipPeakV - threshold
  const compressed = threshold + headroom * Math.tanh((magnitude - threshold) / headroom)
  return Math.sign(sample) * compressed
}

/** Solid state: a residual cubic, then the rail. */
export function clipper(x: number): number {
  return softClip(x - CLIP_K3 * x * x * x, CLIP_RAIL_VOLTS, CLIP_KNEE_FRACTION)
}

/**
 * Output transformer: the flux is the signal through a one-pole low-pass at the core corner, the
 * core passes `Φ·tanh(flux/Φ)` of it, and the output loses the difference. Stateful, so it is a
 * factory: one integrator per run.
 */
export function coreSaturation(sampleRateHz: number): (x: number) => number {
  const coefficient = 1 - Math.exp((-2 * Math.PI * CORE_CORNER_HZ) / sampleRateHz)
  let flux = 0
  return (x: number) => {
    flux += coefficient * (x - flux)
    const phi = CORE_FLUX_LIMIT_VOLTS
    const lost = flux - phi * Math.tanh(flux / phi)
    return x - lost
  }
}

export interface HarmonicReading {
  /** The fundamental's output amplitude, volts peak. */
  fundamentalPeakV: number
  /** Each harmonic's level relative to the fundamental, in percent: index 0 is H2. */
  harmonicsPercent: number[]
  thdPercent: number
}

/**
 * Drive `shape` with a sine of `peakV` at `freqHz` and read the harmonics off the settled output
 * by direct DFT. The tone is snapped to a bin of the record, exactly as the screens snap theirs, so every harmonic lands on a bin and nothing leaks. `settleSeconds` lets a stateful
 * shaper reach steady state first; the memoryless ones need none. Harmonics above `maxHarmonicHz`
 * are left out, as they are on the screen, where they lie above the analysed band.
 */
export function harmonicsOfSine(
  shape: (x: number) => number,
  peakV: number,
  freqHz: number,
  sampleRateHz: number,
  orders = 8,
  maxHarmonicHz = Infinity,
  settleSeconds = 0,
): HarmonicReading {
  // Sized so the fundamental sits at least 64 bins up (SweepResolution.TargetFundamentalBins),
  // which is what keeps a 20 Hz tone at 192 kHz from snapping to 23 Hz.
  const n = Math.min(1 << 20, Math.max(4096, 1 << Math.ceil(Math.log2((64 * sampleRateHz) / freqHz))))
  const snappedHz = (Math.max(1, Math.round((freqHz * n) / sampleRateHz)) * sampleRateHz) / n

  for (let i = 0; i < Math.round(settleSeconds * sampleRateHz); i++) {
    shape(peakV * Math.sin((2 * Math.PI * snappedHz * i) / sampleRateHz))
  }
  const y = new Float64Array(n)
  for (let i = 0; i < n; i++) y[i] = shape(peakV * Math.sin((2 * Math.PI * snappedHz * i) / sampleRateHz))

  // One bin by Goertzel: the same number a DFT gives, at one multiply-add per sample and no trig
  // in the loop, which is what keeps a 20 Hz analysis of a quarter-million samples under 10 ms.
  const amplitudeAt = (order: number): number => {
    const w = (2 * Math.PI * snappedHz * order) / sampleRateHz
    const coefficient = 2 * Math.cos(w)
    let s1 = 0
    let s2 = 0
    for (let i = 0; i < n; i++) {
      const s0 = y[i]! + coefficient * s1 - s2
      s2 = s1
      s1 = s0
    }
    const re = s1 - s2 * Math.cos(w)
    const im = s2 * Math.sin(w)
    return (2 * Math.hypot(re, im)) / n
  }

  const fundamental = amplitudeAt(1)
  const harmonicsPercent: number[] = []
  let sum = 0
  for (let k = 2; k <= orders + 1; k++) {
    if (k * snappedHz > maxHarmonicHz) break
    const ratio = amplitudeAt(k) / fundamental
    harmonicsPercent.push(100 * ratio)
    sum += ratio * ratio
  }
  return { fundamentalPeakV: fundamental, harmonicsPercent, thdPercent: 100 * Math.sqrt(sum) }
}

export type Point = [x: number, y: number]

/**
 * THD (and each harmonic, and the gain) against drive for a memoryless shaper at one frequency —
 * what the THD-vs-amplitude screen draws. Drive is in volts RMS, as the screen's axis is; the
 * shaper sees the peak. One analysis per level, shared by every trace a page draws from it.
 *
 * The default rate is 48 kHz rather than the screen's 192 kHz because nothing here depends on it:
 * a memoryless curve distorts a sine identically at any rate, and the record a low tone needs is
 * four times shorter — which is the difference between a page that opens at once and one that
 * takes seconds.
 */
export function thdVsAmplitude(
  shape: (x: number) => number,
  fromVrms: number,
  toVrms: number,
  stepsPerOctave = 5,
  maxHarmonicHz = Infinity,
  freqHz = 1000,
  sampleRateHz = 48_000,
): { thd: Point[]; d2: Point[]; d3: Point[]; d5: Point[]; gainDb: Point[] } {
  const count = Math.round(Math.log2(toVrms / fromVrms) * stepsPerOctave) + 1
  const thd: Point[] = []
  const d2: Point[] = []
  const d3: Point[] = []
  const d5: Point[] = []
  const gainDb: Point[] = []
  for (let i = 0; i < count; i++) {
    const vrms = fromVrms * Math.pow(2, i / stepsPerOctave)
    const r = harmonicsOfSine(shape, vrms * Math.SQRT2, freqHz, sampleRateHz, 8, maxHarmonicHz)
    thd.push([vrms, r.thdPercent])
    d2.push([vrms, r.harmonicsPercent[0] ?? 0])
    d3.push([vrms, r.harmonicsPercent[1] ?? 0])
    d5.push([vrms, r.harmonicsPercent[3] ?? 0])
    gainDb.push([vrms, 20 * Math.log10(r.fundamentalPeakV / (vrms * Math.SQRT2))])
  }
  return { thd, d2, d3, d5, gainDb }
}

/**
 * THD against frequency for the core-saturation model at one drive — the THD-vs-frequency screen.
 * Stateful, so a fresh integrator per point, settled for a few periods before reading.
 */
export function coreSaturationThdVsFrequency(
  vrms: number,
  fromHz: number,
  toHz: number,
  stepsPerOctave = 5,
  sampleRateHz = 48_000,
  maxHarmonicHz = 0.98 * 96_000,
): { thd: Point[]; d3: Point[] } {
  const count = Math.round(Math.log2(toHz / fromHz) * stepsPerOctave) + 1
  const thd: Point[] = []
  const d3: Point[] = []
  for (let i = 0; i < count; i++) {
    const f = fromHz * Math.pow(2, i / stepsPerOctave)
    // The flux integrator's corner is 30 Hz: a quarter of a second settles it at any frequency.
    const r = harmonicsOfSine(coreSaturation(sampleRateHz), vrms * Math.SQRT2, f, sampleRateHz, 8, maxHarmonicHz, 0.25)
    thd.push([f, r.thdPercent])
    d3.push([f, r.harmonicsPercent[1] ?? 0])
  }
  return { thd, d3 }
}
