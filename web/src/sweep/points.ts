/**
 * The sweep's frequency grid, mirroring Qa403.Dsp/SweepPoints.cs.
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

import type { SweepPreviewSeries } from '../api/client'

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
 * Mirrors Qa403.Dsp.SweepResolution — see there for why bin index and cycle count are the same
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
const SUPPORTED_SAMPLE_RATES_HZ = [48000, 96000, 192000]

function powerOfTwoAtLeast(value: number): number {
  let n = 1
  while (n < value && n < ADAPTIVE_MAX_FFT_SIZE) n *= 2
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

/** The FFT size a point at `freqHz` is analyzed at under adaptive resolution. */
export function adaptiveFftSize(freqHz: number, sampleRateHz: number): number {
  const { floor, ceiling } = adaptiveFftBounds(sampleRateHz)
  if (!(freqHz > 0)) return ceiling
  const n = powerOfTwoAtLeast((ADAPTIVE_TARGET_BINS * sampleRateHz) / freqHz)
  return Math.min(Math.max(n, floor), ceiling)
}

/** The sample rate an adaptive sweep ending at `endFreqHz` will run at. */
export function adaptiveSampleRateHz(endFreqHz: number): number {
  const highest = SUPPORTED_SAMPLE_RATES_HZ[SUPPORTED_SAMPLE_RATES_HZ.length - 1]
  if (!(endFreqHz > 0)) return highest
  const needed = ADAPTIVE_HARMONIC_ORDER * endFreqHz
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
): number {
  if (!(pointCount > 0) || !(sampleRateHz > 0)) return 0
  const recordSeconds = fftSize / sampleRateHz
  return pointCount * perPointSeconds(recordSeconds, averageCount, hopFraction, stimulusLatencySeconds)
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
): number {
  if (!(sampleRateHz > 0)) return 0
  let total = 0
  for (const freqHz of frequenciesHz) {
    const recordSeconds = adaptiveFftSize(freqHz, sampleRateHz) / sampleRateHz
    total += perPointSeconds(recordSeconds, averageCount, hopFraction, stimulusLatencySeconds)
  }
  return total
}

function perPointSeconds(
  recordSeconds: number,
  averageCount: number,
  hopFraction: number,
  stimulusLatencySeconds: number,
): number {
  const averages = Math.max(1, averageCount)
  const averageSeconds = (1 + (averages - 1) * hopFraction) * recordSeconds
  return recordSeconds + averageSeconds + stimulusLatencySeconds
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

/** dBc from a percentage of the fundamental — the chart's %/dB toggle. */
export function percentToDb(percent: number): number {
  return 20 * Math.log10(Math.max(percent, Number.EPSILON) / 100)
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
