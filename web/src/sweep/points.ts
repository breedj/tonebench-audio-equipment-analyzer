/**
 * The sweep's frequency grid, mirroring Tonebench.Dsp/SweepPoints.cs.
 *
 * Duplicated rather than fetched because the config panel has to answer "how many points is that,
 * and how long will it take?" while the user is still typing — before anything has been sent to the
 * server. The server remains the authority: it re-derives the same grid and its answer is what
 * actually gets measured, so this only ever has to be close enough to size a warning.
 *
 * The bin snapping deliberately is NOT duplicated. That needs the coprime-to-N search
 * (CoherentFrequency.Nearest), and a slightly different answer here than on the server would be
 * worse than no answer at all — the point count shown is therefore an upper bound, since snapping
 * can only merge points, never add them.
 */

import type { SweepPreviewSeries, ThdMethodName, ThdSweepPoint } from '../api/client'

/** Log-spaced frequencies, `stepsPerOctave` per octave across [startHz, endHz], endpoints included. */
export function logPerOctave(startHz: number, endHz: number, stepsPerOctave: number): number[] {
  if (!(startHz > 0) || !(endHz >= startHz) || !(stepsPerOctave >= 1)) return []

  // The two clamps match the C#: a sub-octave span must not collapse to a single point.
  const octaves = Math.max(1, Math.log2(endHz / startHz))
  const totalSteps = Math.max(2, Math.floor(stepsPerOctave * octaves))
  const logStep = octaves / totalSteps

  const values: number[] = []
  for (let i = 0; i <= totalSteps; i++) {
    values.push(Math.min(startHz * Math.pow(2, i * logStep), endHz))
  }
  return values
}

/**
 * Continues a grid past its end at the nominal per-octave step: `fromHz·2^(k/stepsPerOctave)` for
 * k = 1, 2, … up to and including the first value at or above `toHz`. Mirrors
 * SweepPoints.ExtendPerOctave — the frequency-normalized method has to measure the fundamental at
 * the harmonics' own frequencies, so the sweep keeps going past the requested end. Not
 * logPerOctave over the widened span: that would re-space every requested point.
 */
export function extendGridPerOctave(fromHz: number, toHz: number, stepsPerOctave: number): number[] {
  if (!(fromHz > 0) || !(stepsPerOctave >= 1) || !(toHz > fromHz)) return []

  const values: number[] = []
  for (let k = 1; ; k++) {
    const hz = fromHz * Math.pow(2, k / stepsPerOctave)
    values.push(hz)
    if (hz >= toHz) break
  }
  return values
}

/** How far past `endFreqHz` a normalized sweep steps the fundamental — 5x, capped at the sweep's own ceiling. Mirrors ThdSweepRequest.ReferenceEndFreqHz. */
export function referenceEndFreqHz(endFreqHz: number, sampleRateHz: number): number {
  return Math.min(ADAPTIVE_HARMONIC_ORDER_FOR_REFERENCE * endFreqHz, 0.98 * (sampleRateHz / 2))
}

/**
 * Upper bound on how many points a sweep will produce — the log grid, minus anything above
 * `maxHz`. The real count can be lower: snapping two neighbours onto the same coherent bin merges
 * them, which is common at the bottom of a sweep where the log spacing is finer than the bin
 * spacing.
 */
export function estimatePointCount(
  startHz: number,
  endHz: number,
  stepsPerOctave: number,
  sampleRateHz: number,
): number {
  const maxHz = 0.98 * (sampleRateHz / 2)
  return logPerOctave(startHz, endHz, stepsPerOctave).filter((f) => f > 0 && f <= maxHz).length
}

/**
 * Mirrors Tonebench.Dsp.SweepResolution — see there for why bin index and cycle count are the same
 * number, and why that means one constant sizes the FFT. Duplicated for the same reason the
 * frequency grid is: the panel has to answer "how long will this take?" while the user is still
 * typing, before anything has reached the server.
 */
export const ADAPTIVE_TARGET_BINS = 64
export const ADAPTIVE_MAX_FFT_SIZE = 131072
const ADAPTIVE_MIN_FFT_SIZE = 1024
const ADAPTIVE_MAX_BIN_WIDTH_HZ = 46.875
const ADAPTIVE_MAX_RECORD_SECONDS = 131072 / 192000
const ADAPTIVE_HARMONIC_ORDER = 5
/** The same order, named for its second job: how far past the end a normalized sweep measures the fundamental. */
const ADAPTIVE_HARMONIC_ORDER_FOR_REFERENCE = ADAPTIVE_HARMONIC_ORDER
const SUPPORTED_SAMPLE_RATES_HZ = [48000, 96000, 192000]

function powerOfTwoAtLeast(value: number, cap = ADAPTIVE_MAX_FFT_SIZE): number {
  let n = 1
  while (n < value && n < cap) n *= 2
  return n
}

function powerOfTwoAtMost(value: number): number {
  let n = 1
  while (n * 2 <= value && n < ADAPTIVE_MAX_FFT_SIZE) n *= 2
  return n
}

/**
 * The FFT sizes available at a given sample rate — a bin-width floor and a record-length ceiling,
 * both of which mean the same thing at every rate. See SweepResolution.BoundsFor.
 */
export function adaptiveFftBounds(sampleRateHz: number): { floor: number; ceiling: number } {
  const ceiling = Math.min(
    Math.max(powerOfTwoAtMost(ADAPTIVE_MAX_RECORD_SECONDS * sampleRateHz), ADAPTIVE_MIN_FFT_SIZE),
    ADAPTIVE_MAX_FFT_SIZE,
  )
  const rawFloor = Math.min(
    Math.max(powerOfTwoAtLeast(sampleRateHz / ADAPTIVE_MAX_BIN_WIDTH_HZ), ADAPTIVE_MIN_FFT_SIZE),
    ADAPTIVE_MAX_FFT_SIZE,
  )
  return { floor: Math.min(rawFloor, ceiling), ceiling }
}

/**
 * The FFT size a point at `freqHz` is analyzed at under adaptive resolution.
 *
 * `maxFftSize` raises the record-time ceiling `adaptiveFftBounds` imposes — only the stepped-sine
 * response uses it, and only for a run that asks to start below what that ceiling can measure. See
 * SteppedResponseRequest.FftSizeAt, which this mirrors.
 */
export function adaptiveFftSize(freqHz: number, sampleRateHz: number, maxFftSize?: number): number {
  const { floor, ceiling: standardCeiling } = adaptiveFftBounds(sampleRateHz)
  const ceiling = Math.max(standardCeiling, maxFftSize ?? 0)
  if (!(freqHz > 0)) return ceiling
  const n = powerOfTwoAtLeast((ADAPTIVE_TARGET_BINS * sampleRateHz) / freqHz, ceiling)
  return Math.min(Math.max(n, floor), ceiling)
}

/**
 * The sample rate an adaptive sweep ending at `endFreqHz` will run at. Mirrors
 * SweepResolution.SampleRateFor.
 *
 * `harmonicOrder` is how many harmonics of the top point have to stay in band. Five for a THD
 * sweep, which measures them; **one** for a stepped-sine response, which measures only the tone
 * itself — and that is a real saving rather than a corner cut, since it drops a 20 kHz run from
 * 192 kHz to 48 kHz.
 */
export function adaptiveSampleRateHz(endFreqHz: number, harmonicOrder = ADAPTIVE_HARMONIC_ORDER): number {
  const highest = SUPPORTED_SAMPLE_RATES_HZ[SUPPORTED_SAMPLE_RATES_HZ.length - 1]
  if (!(endFreqHz > 0)) return highest
  const needed = harmonicOrder * endFreqHz
  const usable = SUPPORTED_SAMPLE_RATES_HZ.filter((rate) => 0.98 * (rate / 2) >= needed)
  return usable.length > 0 ? Math.min(...usable) : highest
}

/**
 * Roughly how long a sweep will take, in seconds.
 *
 * Per point: refill the FFT window after the flush (one whole record), then the averages. Averages
 * are paced by fresh signal rather than by the display tick — see
 * SpectrumPipelineOptions.AverageHopFraction — so N of them cost `1 + (N-1)*hop` records rather
 * than N/frameRate seconds. That is the term the old estimate got wrong in both directions: it
 * under-counted a large FFT (whose averages overlapped almost completely and bought nothing) and
 * over-counted a small one.
 *
 * Deliberately still an estimate for a "this will take about N minutes" hint — it does not model
 * the level-probe pass or the per-size noise-floor captures.
 */
export function estimateDurationSeconds(
  pointCount: number,
  fftSize: number,
  sampleRateHz: number,
  averageCount: number,
  hopFraction = 0.5,
  stimulusLatencySeconds = 0.065,
  recordsBeforeAverages = 1,
): number {
  if (!(pointCount > 0) || !(sampleRateHz > 0)) return 0
  const recordSeconds = fftSize / sampleRateHz
  return (
    pointCount *
    perPointSeconds(recordSeconds, averageCount, hopFraction, stimulusLatencySeconds, recordsBeforeAverages)
  )
}

/**
 * The adaptive counterpart: every point carries its own record length, so the total is a sum over
 * the grid rather than one per-point figure times a count. The frequencies are the pre-snap log
 * grid, which is close enough — snapping moves a point by at most half a bin.
 */
export function estimateAdaptiveDurationSeconds(
  frequenciesHz: number[],
  sampleRateHz: number,
  averageCount: number,
  hopFraction = 0.5,
  stimulusLatencySeconds = 0.065,
  options: AdaptiveDurationOptions = {},
): number {
  if (!(sampleRateHz > 0)) return 0
  let total = 0
  for (const freqHz of frequenciesHz) {
    const recordSeconds = adaptiveFftSize(freqHz, sampleRateHz, options.maxFftSize) / sampleRateHz
    total += perPointSeconds(
      recordSeconds, averageCount, hopFraction, stimulusLatencySeconds, options.recordsBeforeAverages,
    )
  }
  return total
}

export interface AdaptiveDurationOptions {
  /** Raises the record-time ceiling — see `adaptiveFftSize`. */
  maxFftSize?: number
  /**
   * Whole records spent before the averages begin. 1 for a THD sweep, whose first record after the
   * stimulus change is the one it measures. 2 for the stepped-sine response, which throws that
   * record away because it holds part of the previous tone — a level measurement cannot tolerate
   * what a ratio of harmonics within one record cancels. See
   * SteppedResponseRunner.CaptureWithStimulusAsync.
   */
  recordsBeforeAverages?: number
}

function perPointSeconds(
  recordSeconds: number,
  averageCount: number,
  hopFraction: number,
  stimulusLatencySeconds: number,
  recordsBeforeAverages = 1,
): number {
  const averages = Math.max(1, averageCount)
  const averageSeconds = (1 + (averages - 1) * hopFraction) * recordSeconds
  return recordsBeforeAverages * recordSeconds + averageSeconds + stimulusLatencySeconds
}

/** "2 m 30 s" / "45 s" — for the config panel's duration hint. */
export function formatDuration(seconds: number): string {
  if (!(seconds > 0)) return '—'
  const rounded = Math.round(seconds)
  if (rounded < 60) return `${rounded} s`
  const minutes = Math.floor(rounded / 60)
  const rest = rounded % 60
  return rest === 0 ? `${minutes} m` : `${minutes} m ${rest} s`
}

/** dBc from a percentage of the fundamental — the ratio alone, before the dB view's gain term. */
export function percentToDb(percent: number): number {
  return 20 * Math.log10(Math.max(percent, Number.EPSILON) / 100)
}

/**
 * A distortion or noise percentage as the **dB** view plots it: the ratio in dBc *plus the point's
 * own gain*, which is the one place that compensation is decided for the chart, the Autofit extent
 * and the metrics table alike.
 *
 * The percent view answers "how much of the DUT's output is distortion"; the dB view answers "how
 * big is the distortion against the volt that was put in", which is what ARTA's `dB V/V` axis shows
 * and what the previous app plots (frmThdFrequency.cs PlotMagnitude — its percent plot is
 * uncompensated in exactly the same way). The identity is worth keeping in mind, because it is why
 * the term is the gain and not something else:
 *
 *     harmonic_dBV − fundamental_dBV + gain_dB  ≡  harmonic_dBV − generator_dBV
 *
 * since `gainDb` is `fundamentalDbv − generatorDbv` (ThdSweepRunner). So every trace on the dB axis
 * — THD, D2..D6+, Noise and Magnitude itself — is referred to one volt of generator drive, which is
 * what makes them readable against each other: a flat DUT's harmonics sit a fixed distance under
 * its Magn curve, and a bump in the response moves the whole family together instead of silently
 * dividing itself out of the distortion traces.
 *
 * **The same gain, at the swept point, for every trace and both THD methods.** A frequency-
 * normalized run already divides each harmonic by the fundamental measured at *that harmonic's*
 * frequency; adding that harmonic's gain back as well would cancel the normalization exactly
 * (`harmonic_dBV − generator_dBV` either way) and make nTHD plot identically to THD, which is the
 * one thing the method exists not to do. The point's own gain is also what the reference adds — its
 * left-channel D2 line reaches for `step.Right.Gain_dB`, which is a slip, not a convention.
 *
 * NaN when the point carries no gain (a sweep stored before `gain_db` was written), so the trace
 * draws a gap rather than a figure that quietly means something else. Its Magn trace is already
 * absent for the same reason.
 */
export function sweepDistortionDb(percent: number, gainDb: number): number {
  return Number.isFinite(gainDb) ? percentToDb(percent) + gainDb : Number.NaN
}

/**
 * The point nearest `target` on the given channel, or null if that channel has no points at all. A
 * plain linear scan rather than a binary search over the sorted array: a sweep tops out around a
 * few hundred points, so the O(n) cost here is not worth the bug surface of a hand-rolled
 * nearest-neighbour binary search.
 *
 * `valueOf` is what "nearest" is measured along, defaulting to frequency. An amplitude sweep passes
 * the chart's own x accessor instead (sweep/xAxis.ts), so hovering the chart selects the point that
 * is nearest *on screen* rather than the one nearest in a quantity that never varies.
 */
export function nearestSweepPoint<T extends { channel: number; freqHz: number }>(
  points: T[],
  channel: number,
  target: number,
  valueOf: (point: T) => number = (point) => point.freqHz,
): T | null {
  let best: T | null = null
  let bestDist = Infinity

  for (const point of points) {
    if (point.channel !== channel) continue
    const dist = Math.abs(valueOf(point) - target)
    if (dist < bestDist) {
      bestDist = dist
      best = point
    }
  }

  return best
}

/** Left/right (min, max) resampled onto one shared x axis — see mergePreviewSeries. */
export interface MergedPreviewSeries {
  x: number[]
  leftMin: (number | null)[]
  leftMax: (number | null)[]
  rightMin: (number | null)[]
  rightMax: (number | null)[]
}

/** The value of `key` at whichever of `series.x` sits closest to `x`. `series.x` is never empty when this is called. */
function nearestValue(series: SweepPreviewSeries, x: number, key: 'min' | 'max'): number {
  let bestIdx = 0
  let bestDist = Infinity
  for (let i = 0; i < series.x.length; i++) {
    const dist = Math.abs(series.x[i] - x)
    if (dist < bestDist) {
      bestDist = dist
      bestIdx = i
    }
  }
  return series[key][bestIdx]
}

/**
 * Left and right preview series onto one shared x axis, for a chart that draws both channels at
 * once. `left`/`right` are independent decimations — the FFT preview's x grid is identical between
 * channels by construction (both channels of one sweep point share the same FFT size, bin count and
 * DisplayLoFreqHz, and log-decimation boundaries depend on none of the signal itself), but the scope
 * preview's is not: each channel triggers off its own zero-crossing (see BuildScopePreview
 * server-side), so the two can differ in both length and alignment. Rather than special-case the two
 * shapes, both go through the same union-plus-nearest-match here: the union of every x value either
 * channel has, and at each of those, whichever of that channel's own points is closest to it. At
 * ≤ SpectrumPipeline.PreviewMaxColumns (200) points per channel this is cheap, and for the FFT case
 * (identical grids) "nearest" is an exact match, so it degrades to nothing more than a direct lookup.
 *
 * A channel that's absent (not part of this sweep, i.e. disabled in the generator) contributes
 * nothing to the union and reads back all null — which a chart with `points: {show:false}` and no
 * fallback line renders as nothing, exactly "don't show a disabled channel" without the chart
 * needing its own branch for it.
 */
export function mergePreviewSeries(left: SweepPreviewSeries | null, right: SweepPreviewSeries | null): MergedPreviewSeries {
  const xs = new Set<number>()
  for (const v of left?.x ?? []) xs.add(v)
  for (const v of right?.x ?? []) xs.add(v)
  const x = [...xs].sort((a, b) => a - b)

  return {
    x,
    leftMin: left ? x.map((v) => nearestValue(left, v, 'min')) : x.map(() => null),
    leftMax: left ? x.map((v) => nearestValue(left, v, 'max')) : x.map(() => null),
    rightMin: right ? x.map((v) => nearestValue(right, v, 'min')) : x.map(() => null),
    rightMax: right ? x.map((v) => nearestValue(right, v, 'max')) : x.map(() => null),
  }
}

// ---------------------------------------------------------------------------
// Which of a point's figures a trace shows
// ---------------------------------------------------------------------------

/** The sweep chart's traces, in its own order. */
export type SweepTraceKey = 'magnitude' | 'thd' | 'thdn' | 'd2' | 'd3' | 'd4' | 'd5' | 'd6plus' | 'noise' | 'snr'

/**
 * One point's value for a trace, under the method the point's run used. NaN where the point has
 * no such figure — a harmonic above Nyquist, or a normalized order whose reference the sweep has
 * not measured — which the chart draws as a gap.
 *
 * The one place this is decided: ThdSweepChart plots with it and SweepDisplayPanel's Autofit
 * ranges with it, so the two cannot disagree about what is on screen. A frequency-normalized run
 * shows its normalized figures for THD and the harmonics; Magnitude and Noise are the same number
 * under either method (the fundamental is the fundamental, and the residual is referenced to it at
 * the stimulus frequency either way).
 */
export function sweepTraceValue(point: ThdSweepPoint, key: SweepTraceKey, method: ThdMethodName): number {
  const normalized = method === 'FrequencyNormalized'
  switch (key) {
    case 'magnitude':
      return point.gainDb
    case 'thd':
      return normalized ? (point.normalizedThdPercent ?? Number.NaN) : point.thdPercent
    case 'd2':
    case 'd3':
    case 'd4':
    case 'd5': {
      const order = Number(key.slice(1)) - 2
      return normalized
        ? (point.normalizedHarmonicPercent?.[order] ?? Number.NaN)
        : (point.harmonicPercent[order] ?? Number.NaN)
    }
    case 'd6plus':
      return normalized ? (point.normalizedD6PlusPercent ?? Number.NaN) : point.d6PlusPercent
    case 'noise':
      return point.noisePercent
    case 'thdn':
      // Not normalized: THD+N is an integrated figure over the measurement band, so there is no
      // per-harmonic reference to divide it by -- the same reason Magnitude and Noise are not.
      return point.thdNPercent
    case 'snr':
      // SNR is the one figure here that counts upward, and this axis counts down from the
      // fundamental. So what is drawn is the noise the figure measures -- 100*10^(-SNR/20), i.e.
      // N-D as a share of the fundamental -- which puts a 95 dB SNR at 0.0018 % / -95 dB, beside
      // THD, THD+N and the noise floor where it can be compared with them. Drawing it upward would
      // need a second y axis for one trace. An IMD point has no SNR (imd/points.ts sets NaN), and
      // NaN is what the chart draws as a gap.
      return Number.isFinite(point.snrDb) ? 100 * Math.pow(10, -point.snrDb / 20) : Number.NaN
  }
}

/**
 * What a trace is called under a method: the distortion traces gain an "n" on a normalized run
 * (nTHD, nD2 …) so a normalized run overlaid on a conventional one reads as two different
 * measurements, which it is. Magnitude and Noise keep their names — see sweepTraceValue.
 */
export function sweepTraceLabel(key: SweepTraceKey, method: ThdMethodName, family: SweepTraceFamily = 'thd'): string {
  const base: Record<SweepTraceKey, string> = {
    magnitude: 'Magn',
    thd: 'THD',
    d2: 'D2',
    d3: 'D3',
    d4: 'D4',
    d5: 'D5',
    d6plus: 'D6+',
    noise: 'Noise',
    thdn: 'THD+N',
    snr: 'SNR',
  }
  // The IMD sweep draws its figures through the same trace slots (see imd/points.ts), so only
  // the names differ: the total and the orders, never the magnitude or the noise.
  const imd: Record<SweepTraceKey, string> = {
    ...base,
    thd: 'IMD',
    d2: 'IM2',
    d3: 'IM3',
    d4: 'IM4',
    d5: 'IM5',
    d6plus: 'IM6+',
  }
  const label = (family === 'imd' ? imd : base)[key]
  if (method !== 'FrequencyNormalized' || family === 'imd') return label
  // THD+N and SNR keep their names for the reason Magnitude and Noise do: they are integrated over
  // the band rather than referenced to one harmonic, so there is no normalized counterpart.
  return key === 'magnitude' || key === 'noise' || key === 'thdn' || key === 'snr' ? label : `n${label}`
}

/**
 * Which measurement a set of sweep points came from: a THD sweep (the figures are THD and
 * harmonics D2..D5) or the IMD sweep (IMD and its orders IM2..IM5, mapped onto the same slots by
 * imd/points.ts). Only labels depend on it; the chart, the alignment and the export do not.
 */
export type SweepTraceFamily = 'thd' | 'imd'

/**
 * The method a stored sweep ran with, read off its as-run `params_json`
 * (ThdVsFrequencyParameters.thdMethod). Conventional when the field is absent — every row written
 * before the method existed — or when the JSON is not a sweep's at all.
 */
export function sweepMethodFromParams(paramsJson: string | null | undefined): ThdMethodName {
  const parsed = parseParams(paramsJson)
  return parsed?.thdMethod === 'FrequencyNormalized' ? 'FrequencyNormalized' : 'Conventional'
}

/**
 * The end frequency a stored sweep was asked for, read off its as-run `params_json`, or null when
 * it does not say. A normalized run measured past this (see extendGridPerOctave), and Autofit
 * fits the band that was asked for rather than the reference points above it.
 */
export function sweepEndFreqHzFromParams(paramsJson: string | null | undefined): number | null {
  const parsed = parseParams(paramsJson)
  return typeof parsed?.endFreqHz === 'number' && parsed.endFreqHz > 0 ? parsed.endFreqHz : null
}

function parseParams(paramsJson: string | null | undefined): { thdMethod?: unknown; endFreqHz?: unknown } | null {
  if (!paramsJson) return null
  try {
    const parsed: unknown = JSON.parse(paramsJson)
    return parsed !== null && typeof parsed === 'object' ? (parsed as { thdMethod?: unknown; endFreqHz?: unknown }) : null
  } catch {
    return null
  }
}
