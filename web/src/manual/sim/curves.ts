// What the manual's simulator pages draw: the expected outcome of each measurement, derived from a
// model's transfer function (models.ts) rather than captured from a run. Pure, so it is testable
// under vitest's node environment, and so a page component is nothing but markup and a few calls
// in here.
import { magnitude, type Cascade } from './biquad'
import { GENERATOR_HARMONIC_DBC, NOISE_DENSITY_DBV_PER_ROOT_HZ } from './models'

export type Point = [x: number, y: number]

/** `count` frequencies spaced evenly in log f over [fromHz, toHz], both ends included. */
export function logSpace(fromHz: number, toHz: number, count: number): number[] {
  const a = Math.log10(fromHz)
  const b = Math.log10(toHz)
  const out: number[] = []
  for (let i = 0; i < count; i++) out.push(Math.pow(10, a + ((b - a) * i) / (count - 1)))
  return out
}

/**
 * |H| at `freqHz` after fractional-octave smoothing, the way `FractionalOctaveSmoother` does it:
 * the RMS of the linear magnitude over a band 1/`denominator` octave wide centred on the
 * frequency. Power averaging, not dB averaging, for the reason that class gives — dB averaging is
 * dragged toward every null. 0 means none. What this changes is exactly what the screen's own
 * smoothing changes: the loudspeaker's reflection comb averages out to a +0.26 dB lift wherever
 * the band spans a comb period, and a Q-500 peak 2 Hz wide reads +2.2 dB through a 1/24-octave
 * band instead of +12.
 */
export function smoothedMagnitude(dut: Cascade, freqHz: number, denominator: number, samples = 33): number {
  if (denominator <= 0) return magnitude(dut.responseAt(freqHz))
  const halfWidth = 1 / (2 * denominator)
  let sum = 0
  for (let i = 0; i < samples; i++) {
    const f = freqHz * Math.pow(2, -halfWidth + (2 * halfWidth * i) / (samples - 1))
    const h = magnitude(dut.responseAt(f))
    sum += h * h
  }
  return Math.sqrt(sum / samples)
}

/** The magnitude response in dB against log frequency, smoothed as the screen would smooth it. */
export function magnitudeCurve(dut: Cascade, fromHz: number, toHz: number, count = 400, smoothingDenominator = 0): Point[] {
  return logSpace(fromHz, toHz, count).map((f) => [f, 20 * Math.log10(smoothedMagnitude(dut, f, smoothingDenominator))])
}

/**
 * The phase response in degrees, unwrapped along the curve so a pole's 180° turn reads as a turn
 * rather than as a jump. Started from the lowest frequency's wrapped value, which is what the
 * app's own unwrap does too.
 *
 * `removeDelaySamples` takes a pure delay back out, as the frequency-response screen does with the
 * delay it recovers from the impulse peak: a linear phase of 360·f·d/fs is added back. Measured
 * against the simulator's tube amp the screen reports about 0.97 samples — the 46 kHz low-pass
 * shifts the impulse peak by that much — and its phase trace is the model's plus exactly that term.
 */
export function phaseCurve(dut: Cascade, fromHz: number, toHz: number, count = 400, removeDelaySamples = 0): Point[] {
  const out: Point[] = []
  let previous = 0
  let offset = 0
  logSpace(fromHz, toHz, count).forEach((f, i) => {
    const wrapped = dut.phaseDeg(f) + (360 * f * removeDelaySamples) / dut.sampleRateHz
    if (i > 0) {
      const step = wrapped - previous
      if (step > 180) offset -= 360
      else if (step < -180) offset += 360
    }
    previous = wrapped
    out.push([f, wrapped + offset])
  })
  return out
}

export interface BandwidthReading {
  /** The reference level the corners were measured against, dB. */
  referenceDb: number
  /** ±1 dB band: where the response first leaves the band around the reference, either way. */
  oneDbLowHz: number | null
  oneDbHighHz: number | null
  /** −3 dB band: where the response first falls 3 dB below the reference. One-sided, by convention. */
  threeDbLowHz: number | null
  threeDbHighHz: number | null
}

/**
 * The bandwidth figures the frequency-response screen would report under its default
 * **Passband median** reference: the median level over `bandLowHz`–`bandHighHz`, then a walk
 * outward from the middle of that band until the curve crosses each threshold. The ±1 dB row is
 * symmetric and the −3 dB row one-sided, for the reasons the frequency-response page gives.
 *
 * The median is taken over linear frequency, as the app's is (it takes it over FFT bins), so it is
 * weighted toward the top of the band — harmless where the DUT is flat, which is where the band
 * must be put.
 */
export function bandwidth(
  dut: Cascade,
  sweepFromHz: number,
  sweepToHz: number,
  bandLowHz = 200,
  bandHighHz = 2000,
  smoothingDenominator = 0,
): BandwidthReading {
  const level = (f: number) => 20 * Math.log10(smoothedMagnitude(dut, f, smoothingDenominator))
  const bandCount = 512
  const bandDb: number[] = []
  for (let i = 0; i < bandCount; i++) {
    bandDb.push(level(bandLowHz + ((bandHighHz - bandLowHz) * i) / (bandCount - 1)))
  }
  bandDb.sort((a, b) => a - b)
  const referenceDb = bandDb[bandCount >> 1]!

  // A fine log grid to walk. A crossing is interpolated in log f between the last point inside
  // and the first point outside, against the threshold that point crossed.
  const grid = logSpace(sweepFromHz, sweepToHz, 4000)
  const db = grid.map(level)
  const startIndex = grid.findIndex((f) => f >= Math.sqrt(bandLowHz * bandHighHz))

  const walk = (direction: -1 | 1, lowDb: number, highDb: number): number | null => {
    for (let i = startIndex; i >= 0 && i < grid.length; i += direction) {
      const level = db[i]!
      if (level >= lowDb && level <= highDb) continue
      const j = i - direction
      if (j < 0 || j >= grid.length) return grid[i]!
      const inside = db[j]!
      const target = level < lowDb ? lowDb : highDb
      const t = (target - inside) / (level - inside)
      return Math.pow(10, Math.log10(grid[j]!) + t * (Math.log10(grid[i]!) - Math.log10(grid[j]!)))
    }
    return null
  }

  return {
    referenceDb,
    oneDbLowHz: walk(-1, referenceDb - 1, referenceDb + 1),
    oneDbHighHz: walk(1, referenceDb - 1, referenceDb + 1),
    threeDbLowHz: walk(-1, referenceDb - 3, Infinity),
    threeDbHighHz: walk(1, referenceDb - 3, Infinity),
  }
}

/**
 * THD in percent against fundamental frequency for a *linear* model driven by the simulated
 * generator, whose tones carry the fixed harmonic stack in `GENERATOR_HARMONIC_DBC`. Each harmonic
 * is a tone in its own right, so the DUT shapes it by its own response: THD is the RMS sum of
 * `h_k · |H(k·f)| / |H(f)|` over the orders that land below `maxHarmonicHz`, which for the THD
 * sweep is 98 % of Nyquist.
 */
export function linearThdPercent(dut: Cascade, freqHz: number, maxHarmonicHz: number): number {
  const h1 = magnitude(dut.responseAt(freqHz))
  let sum = 0
  GENERATOR_HARMONIC_DBC.forEach((dbc, i) => {
    const order = i + 2
    const f = order * freqHz
    if (f > maxHarmonicHz) return
    const hk = magnitude(dut.responseAt(f))
    const ratio = Math.pow(10, dbc / 20) * (hk / h1)
    sum += ratio * ratio
  })
  return 100 * Math.sqrt(sum)
}

export function thdVsFrequencyCurve(
  dut: Cascade,
  fromHz: number,
  toHz: number,
  maxHarmonicHz: number,
  stepsPerOctave = 5,
): Point[] {
  const count = Math.round(Math.log2(toHz / fromHz) * stepsPerOctave) + 1
  return logSpace(fromHz, toHz, count).map((f) => [f, linearThdPercent(dut, f, maxHarmonicHz)])
}

/** One harmonic's own level relative to the fundamental, in percent, along the same sweep. */
export function harmonicVsFrequencyCurve(
  dut: Cascade,
  order: number,
  fromHz: number,
  toHz: number,
  maxHarmonicHz: number,
  stepsPerOctave = 5,
): Point[] {
  const dbc = GENERATOR_HARMONIC_DBC[order - 2]
  if (dbc === undefined) return []
  const count = Math.round(Math.log2(toHz / fromHz) * stepsPerOctave) + 1
  const out: Point[] = []
  for (const f of logSpace(fromHz, toHz, count)) {
    if (order * f > maxHarmonicHz) break
    const h1 = magnitude(dut.responseAt(f))
    const hk = magnitude(dut.responseAt(order * f))
    out.push([f, 100 * Math.pow(10, dbc / 20) * (hk / h1)])
  }
  return out
}

/**
 * The harmonic stack a single tone at `fundamentalDbv` produces through a linear DUT, as the FFT
 * screen's spectrum would show it: the fundamental, then each order at its dBc plus whatever the
 * response does at that frequency. Orders above `maxHz` are not in the analysed band.
 */
export function toneStems(
  dut: Cascade,
  fundamentalHz: number,
  fundamentalDbv: number,
  maxHz: number,
): Point[] {
  const out: Point[] = [[fundamentalHz, fundamentalDbv + dut.magnitudeDb(fundamentalHz)]]
  GENERATOR_HARMONIC_DBC.forEach((dbc, i) => {
    const f = (i + 2) * fundamentalHz
    if (f <= maxHz) out.push([f, fundamentalDbv + dbc + dut.magnitudeDb(f)])
  })
  return out
}

/**
 * Where the simulated noise floor sits on an FFT screen, per bin: the −150 dBV/√Hz density
 * integrated over the window's noise bandwidth (Hann: 1.5 bins). What the chart draws as a band is
 * the min/max spread of the decimated columns; a single line at this level is roughly its middle.
 */
export function noiseFloorDbvPerBin(sampleRateHz: number, fftSize: number, enbwBins = 1.5): number {
  return NOISE_DENSITY_DBV_PER_ROOT_HZ + 10 * Math.log10((sampleRateHz / fftSize) * enbwBins)
}

/**
 * The impulse response over a time window around t = 0, as the impulse-response screen draws it
 * in its volts view: linear amplitude, here relative to the peak, on a linear time axis. Samples
 * before zero are empty for a causal DUT, and the screen's pre-gate shows that emptiness on purpose.
 */
export function impulseCurve(dut: Cascade, preMs: number, postMs: number): Point[] {
  const fs = dut.sampleRateHz
  const post = Math.ceil((postMs * fs) / 1000)
  const pre = Math.ceil((preMs * fs) / 1000)
  const h = dut.impulse(post + 1)
  let peak = 0
  for (const v of h) peak = Math.max(peak, Math.abs(v))
  const out: Point[] = []
  for (let n = -pre; n <= post; n++) {
    out.push([(n * 1000) / fs, n < 0 ? 0 : h[n]! / peak])
  }
  return out
}

// --- Impedance ---------------------------------------------------------------------------------

export interface ImpedanceCurves {
  magnitudeOhms: Point[]
  phaseDeg: Point[]
}

/**
 * What the impedance screen reads through a fixture: the divider ratio `H` inverted as
 * `Z = R·H / (1 − H)`, exactly as the app does it, so what is drawn includes whatever the bilinear
 * mapping did to the analog circuit — which is what a run against the simulator measures.
 */
export function impedanceCurves(fixture: Cascade, referenceOhms: number, fromHz: number, toHz: number, count = 400): ImpedanceCurves {
  const magnitudeOhms: Point[] = []
  const phaseDeg: Point[] = []
  for (const f of logSpace(fromHz, toHz, count)) {
    const h = fixture.responseAt(f)
    // Z = R h / (1 - h), complex.
    const dRe = 1 - h.re
    const dIm = -h.im
    const den = dRe * dRe + dIm * dIm
    const zRe = (referenceOhms * (h.re * dRe + h.im * dIm)) / den
    const zIm = (referenceOhms * (h.im * dRe - h.re * dIm)) / den
    magnitudeOhms.push([f, Math.hypot(zRe, zIm)])
    phaseDeg.push([f, (Math.atan2(zIm, zRe) * 180) / Math.PI])
  }
  return { magnitudeOhms, phaseDeg }
}

// --- Impulse-domain views ----------------------------------------------------------------------

/** Direct DFT magnitude of `samples[start .. start+length)` under `window`, at one frequency. */
function windowedMagnitude(
  samples: Float64Array,
  start: number,
  window: Float64Array,
  freqHz: number,
  sampleRateHz: number,
): number {
  const w = (2 * Math.PI * freqHz) / sampleRateHz
  let re = 0
  let im = 0
  for (let i = 0; i < window.length; i++) {
    const n = start + i
    if (n < 0 || n >= samples.length) continue
    const v = samples[n]! * window[i]!
    re += v * Math.cos(w * i)
    im -= v * Math.sin(w * i)
  }
  return Math.hypot(re, im)
}

/**
 * The impulse-response screen's <em>Gated response</em>: the impulse windowed with half a Hann
 * rising over `preMs` and half falling over `postMs` (as `ImpulseWindow` does), transformed, in dB
 * relative to the DUT's own passband. Below `1 / (preMs + postMs)` it describes the window.
 */
export function gatedMagnitudeCurve(dut: Cascade, _preMs: number, postMs: number, fromHz: number, toHz: number, count = 300): Point[] {
  const fs = dut.sampleRateHz
  const post = Math.round((postMs * fs) / 1000)
  const h = dut.impulse(post + 1)
  // Every model here is causal, so the pre side of the gate holds zeros whatever its width and only
  // the falling half of the window does anything; the parameter is kept so a call reads like the panel.
  const window = new Float64Array(post + 1)
  for (let i = 0; i <= post; i++) window[i] = 0.5 * (1 + Math.cos((Math.PI * i) / post))
  return logSpace(fromHz, toHz, count).map((f) => [f, 20 * Math.log10(windowedMagnitude(h, 0, window, f, fs))])
}

export interface CsdSlice {
  timeMs: number
  points: Point[]
}

/**
 * A few slices of the cumulative spectral decay the screen draws as a waterfall: the apodizing
 * window of `CumulativeSpectralDecay.BuildWindow` (a `riseMs` raised-cosine rise, then a fall over
 * the rest of `fftLength`) slid to each time in `timesMs`, each slice's spectrum in dB relative to
 * the loudest point of the front slice.
 */
export function csdSlices(
  dut: Cascade,
  fftLength: number,
  riseMs: number,
  timesMs: readonly number[],
  fromHz: number,
  toHz: number,
  count = 160,
): CsdSlice[] {
  const fs = dut.sampleRateHz
  const rise = Math.min(Math.max(Math.round((riseMs * fs) / 1000), 1), fftLength / 2)
  const window = new Float64Array(fftLength)
  for (let i = 0; i < rise; i++) window[i] = 0.5 * (1 - Math.cos((Math.PI * i) / rise))
  const fall = fftLength - rise
  for (let i = rise; i < fftLength; i++) window[i] = 0.5 * (1 + Math.cos((Math.PI * (i - rise)) / fall))

  const lastStart = Math.round((Math.max(...timesMs) * fs) / 1000) - rise + fftLength
  const h = dut.impulse(lastStart + 1)
  const freqs = logSpace(fromHz, toHz, count)

  const raw = timesMs.map((timeMs) => {
    const start = Math.round((timeMs * fs) / 1000) - rise
    return { timeMs, values: freqs.map((f) => windowedMagnitude(h, start, window, f, fs)) }
  })
  const top = Math.max(...raw[0]!.values)
  return raw.map((s) => ({
    timeMs: s.timeMs,
    points: freqs.map((f, i) => [f, 20 * Math.log10(Math.max(s.values[i]! / top, 1e-15))] as Point),
  }))
}

/**
 * One frequency's burst decay: the impulse convolved with a Gaussian-enveloped wavelet
 * `periodsWide` periods wide at half maximum (four, for the screen's Prefer frequency setting),
 * its envelope in dB below its own maximum against time in periods of that frequency.
 */
export function burstDecaySlice(
  dut: Cascade,
  freqHz: number,
  maxPeriods: number,
  pointsPerPeriod = 8,
  periodsWide = 4,
): Point[] {
  const fs = dut.sampleRateHz
  const period = fs / freqHz
  const sigma = (periodsWide * period) / 2.3548200450309493
  const half = Math.ceil(3 * sigma)
  const w = (2 * Math.PI * freqHz) / fs
  const total = Math.ceil(maxPeriods * period) + half + 1
  const h = dut.impulse(total)

  const envelopeAt = (centre: number): number => {
    let re = 0
    let im = 0
    for (let k = -half; k <= half; k++) {
      const n = centre + k
      if (n < 0 || n >= h.length) continue
      const g = Math.exp(-(k * k) / (2 * sigma * sigma))
      re += h[n]! * g * Math.cos(w * k)
      im += h[n]! * g * Math.sin(w * k)
    }
    return Math.hypot(re, im)
  }

  const out: [number, number][] = []
  for (let i = 0; i <= maxPeriods * pointsPerPeriod; i++) {
    const periods = i / pointsPerPeriod
    out.push([periods, envelopeAt(Math.round(periods * period))])
  }
  const top = Math.max(...out.map(([, v]) => v))
  return out.map(([p, v]) => [p, 20 * Math.log10(Math.max(v / top, 1e-15))])
}
