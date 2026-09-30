import type uPlot from 'uplot'
import type { SweepXAxisName, ThdSweepPoint } from '../api/client'
import { FREQ_AXIS_SPACE, freqAxis, freqMinorSplits, freqTickSplits, plotWidthPx } from '../freqAxis'

/**
 * What a sweep chart plots along x.
 *
 * A THD-vs-frequency sweep has only one answer; a THD-vs-amplitude sweep has three, ported from the
 * previous app's `E_X_AxisType`, and all three are DUT-referred:
 *
 * - `outputVolt` — the fundamental actually *measured*, i.e. the voltage at the DUT's output.
 * - `outputWatt` — that voltage into the configured load, which is how amplifier distortion is
 *   normally quoted ("0.1 % at 1 W").
 * - `inputVolt`  — the generator voltage driving the DUT, i.e. the swept variable itself.
 *
 * The first two are measurements and the third is a setting, and that difference is visible on
 * screen: an `inputVolt` sweep lands exactly on the requested grid and both channels share one x
 * value, while the output-referred axes put each channel on its own x values, wherever the DUT
 * actually delivered. The chart's shared-x union handles that; see ThdSweepChart's `xValues`.
 */
export type SweepXAxis = 'frequency' | SweepXAxisName

/** The x-axis choices offered on the amplitude screen, in menu order. `outputVolt` is the default. */
export const AMPLITUDE_X_AXES: { value: SweepXAxisName; label: string }[] = [
  { value: 'outputVolt', label: 'Output voltage' },
  { value: 'outputWatt', label: 'Output power' },
  { value: 'inputVolt', label: 'Input voltage' },
]

/** The unit an axis' values are in — 'Hz', 'V' or 'W'. */
export function sweepXUnit(axis: SweepXAxis): string {
  return axis === 'frequency' ? 'Hz' : axis === 'outputWatt' ? 'W' : 'V'
}

/** The axis' name on its own, for a column header or a settings label. */
export function sweepXLabel(axis: SweepXAxis): string {
  switch (axis) {
    case 'frequency':
      return 'Frequency'
    case 'outputVolt':
      return 'Output voltage'
    case 'outputWatt':
      return 'Output power'
    case 'inputVolt':
      return 'Input voltage'
  }
}

/**
 * One point's position on the chosen axis.
 *
 * Everything here is already on the point — nothing is re-derived from a second source — so the
 * three amplitude axes are three views of one measurement rather than three measurements. A point
 * whose fundamental was never found comes back as 0 (its `fundamentalDbv` is -Infinity), which the
 * chart drops the same way it drops any non-positive value on a log scale.
 */
export function sweepXValue(point: Pick<ThdSweepPoint, 'freqHz' | 'fundamentalDbv' | 'powerWatt' | 'generatorDbv'>, axis: SweepXAxis): number {
  switch (axis) {
    case 'frequency':
      return point.freqHz
    case 'outputVolt':
      return dbvToVolt(point.fundamentalDbv)
    case 'outputWatt':
      return point.powerWatt
    case 'inputVolt':
      return dbvToVolt(point.generatorDbv)
  }
}

export function dbvToVolt(dbv: number): number {
  return Number.isFinite(dbv) ? 10 ** (dbv / 20) : 0
}

/**
 * A value split into its mantissa and its SI prefix — 0.25 becomes `{ value: '250', prefix: 'm' }`.
 *
 * Three significant figures, trailing zeros trimmed: the ticks on these axes are round numbers
 * spanning several decades, and "0.001" next to "1" reads as two different quantities where "1m"
 * next to "1" reads as one axis.
 */
function siParts(value: number): { value: string; prefix: string } {
  if (!Number.isFinite(value)) return { value: '', prefix: '' }
  if (value === 0) return { value: '0', prefix: '' }

  const abs = Math.abs(value)
  if (abs < 1e-6) return { value: trim((value * 1e9).toPrecision(3)), prefix: 'n' }
  if (abs < 1e-3) return { value: trim((value * 1e6).toPrecision(3)), prefix: 'µ' }
  if (abs < 1) return { value: trim((value * 1e3).toPrecision(3)), prefix: 'm' }
  if (abs < 1000) return { value: trim(value.toPrecision(3)), prefix: '' }
  return { value: trim((value / 1000).toPrecision(3)), prefix: 'k' }
}

/**
 * A value with an SI prefix and no unit — `1m`, `100m`, `1`, `7.9`, `2.5k`. What an axis tick reads,
 * where the unit is named once on the axis label rather than on every tick.
 */
export function siPrefixed(value: number): string {
  const { value: mantissa, prefix } = siParts(value)
  return `${mantissa}${prefix}`
}

/**
 * `1 mV`, `250 mW`, `7.9 V` — the readable form, for a legend row or a metrics cell.
 *
 * The prefix binds to the unit, not to the number: "250 mV" is one quantity where "250m V" is a
 * typo. That is the whole reason siParts keeps the two apart.
 */
export function formatSweepX(value: number, axis: SweepXAxis): string {
  if (!(value > 0) || !Number.isFinite(value)) return '—'
  if (axis === 'frequency') return value >= 1000 ? `${(value / 1000).toFixed(value >= 10000 ? 1 : 2)} kHz` : `${value.toFixed(value >= 100 ? 0 : 1)} Hz`

  const { value: mantissa, prefix } = siParts(value)
  return `${mantissa} ${prefix}${sweepXUnit(axis)}`
}

/**
 * The exact powers of ten inside [min, max] — 0.001, 0.01, 0.1, 1, 10.
 *
 * Values are rebuilt from the exponent rather than stepped by repeated multiplication, so a decade
 * is exactly `10 ** e` and {@link decimalLabel} can recognise it without a tolerance argument. The
 * bounds are nudged by a hair before rounding so a scale sitting exactly on a decade (which is what
 * the display panel's own From/To produce) does not lose its end labels to float error.
 */
export function decadeSplits(min: number, max: number): number[] {
  if (!(min > 0) || !(max > min)) return []

  const first = Math.ceil(Math.log10(min) - 1e-9)
  const last = Math.floor(Math.log10(max) + 1e-9)

  const out: number[] = []
  for (let e = first; e <= last; e++) out.push(10 ** e)
  return out
}

/**
 * The 2..9 positions between the decades — the thin lines a log axis needs to be readable as a log
 * axis. Drawn by a second, label-less axis on the same scale (see {@link sweepXMinorAxis}), because
 * uPlot styles an axis' whole grid as one and cannot give some of its own splits a heavier stroke.
 *
 * Empty whenever {@link decadeSplits} has fewer than two entries: at that zoom the major axis has
 * fallen back to labelled 1/2/5 ticks of its own, and a second set of lines under them would be
 * noise rather than structure.
 */
export function minorLogSplits(min: number, max: number): number[] {
  if (decadeSplits(min, max).length < 2) return []

  const out: number[] = []
  for (let e = Math.floor(Math.log10(min)); e <= Math.ceil(Math.log10(max)); e++) {
    const decade = 10 ** e
    for (let m = 2; m <= 9; m++) {
      const v = m * decade
      if (v > min && v < max) out.push(v)
    }
  }
  return out
}

/**
 * A tick's text as a plain decimal — `0.0001`, `0.001`, `0.01`, `0.1`, `1`, `10`.
 *
 * Decades are written out from their exponent rather than formatted, which is what keeps `0.0001`
 * from arriving as `0.00009999999999999999`. Anything that is not a decade — only reachable by
 * zooming inside one, where no decade is on screen to be inconsistent with — falls back to the
 * short SI form, as does a decade too small to write out without becoming a row of zeros.
 */
export function decimalLabel(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return ''

  const exp = Math.round(Math.log10(value))
  const isDecade = Math.abs(value - 10 ** exp) <= value * 1e-9

  if (!isDecade || exp > 6 || exp < -9) return siPrefixed(value)
  return exp >= 0 ? String(10 ** exp) : `0.${'0'.repeat(-exp - 1)}1`
}

/**
 * Decade splits for any log scale, falling back to labelled 1/2/5 ticks once fewer than two decades
 * are on screen — a zoom that lands inside one decade would otherwise leave the axis unlabelled.
 *
 * Shared by both emphasised axes: {@link sweepXAxis} for the level axis and the distortion axis in
 * ThdSweepChart. `plotWidthPx` measures the scale's own extent in pixels, which for a vertical scale
 * is the plot height, so the fallback's spacing budget is right on either.
 */
export function decadeOrFallbackSplits(
  u: uPlot,
  axisIdx: number,
  scaleMin: number,
  scaleMax: number,
  minSpacePx = FREQ_AXIS_SPACE,
): number[] {
  const decades = decadeSplits(scaleMin, scaleMax)
  if (decades.length >= 2) return decades

  const scaleKey = u.axes[axisIdx].scale ?? 'x'
  return freqTickSplits(scaleMin, scaleMax, plotWidthPx(u, scaleKey, scaleMin, scaleMax), minSpacePx)
}

/**
 * The uPlot x-axis config for a sweep chart, spread into the chart's own axis the same way
 * {@link freqAxis} is:
 *
 *   { ...sweepXAxis(axis), stroke: theme.axis, grid: { ... } }
 *
 * The frequency axis is {@link freqAxis} unchanged — the FFT chart and the two preview charts share
 * it, and it has a decade of Hz-specific label behaviour worth not re-deriving.
 *
 * The volt and watt axes tick on **decades only**, labelled as plain decimals, with the 2..9
 * positions drawn as thin unlabelled lines by {@link sweepXMinorAxis}. That is the convention the
 * previous app's graphs used and the one a distortion-vs-level plot is read with: the decades are
 * the numbers anyone quotes, and everything between them is there to show the shape of the curve,
 * not to be read off. Below two decades of span there are no decades left to label, so this falls
 * back to `freqTickSplits` — pure log/linear tick maths with no hertz in it — which keeps a deep
 * zoom labelled instead of blank.
 */
export function sweepXAxis(axis: SweepXAxis): Pick<uPlot.Axis, 'space' | 'splits' | 'filter' | 'values' | 'label'> {
  if (axis === 'frequency') return freqAxis()

  return {
    space: FREQ_AXIS_SPACE,
    label: `${sweepXLabel(axis)} (${sweepXUnit(axis)})`,
    splits: (u, axisIdx, scaleMin, scaleMax) => decadeOrFallbackSplits(u, axisIdx, scaleMin, scaleMax),
    // Same reason as freqAxis: every split it generated is one worth labelling, and uPlot's default
    // log filter drops the ones whose mantissa is not round -- on a tight zoom, all of them.
    filter: (_u, splits) => splits,
    values: (_u, splits) => splits.map((v) => (v == null ? null : decimalLabel(v))),
  }
}

/**
 * The companion axis that draws only the 2..9 gridlines, with no ticks and no labels. Used for both
 * the level axis and the distortion axis — nothing in it is horizontal.
 *
 * A second axis on the same scale is the only way to get two grid weights out of uPlot: `grid` is
 * one stroke and one width for the whole axis, with no per-split hook. Everything visual is left to
 * the caller — the chart owns the theme — and `size: 0` keeps this one out of the layout so it
 * costs no space alongside the plot.
 */
/**
 * The same companion axis for a **frequency** scale, whose labelled ticks are freqAxis' 1/2/5 ladder
 * rather than bare decades. Fills in the mantissas that ladder left out — so a decade reads
 * 10 · 20 · 30 · … · 90 · 100 in lines, labelled wherever the labels fit — and takes the complement
 * from {@link freqMinorSplits} so no position is drawn twice. See there for why that matters.
 */
export function freqMinorGridAxis(): Pick<uPlot.Axis, 'space' | 'splits' | 'filter' | 'values'> {
  return {
    space: 0,
    splits: (u, axisIdx, scaleMin, scaleMax) => {
      const scaleKey = u.axes[axisIdx].scale ?? 'x'
      return freqMinorSplits(scaleMin, scaleMax, plotWidthPx(u, scaleKey, scaleMin, scaleMax))
    },
    filter: (_u, splits) => splits,
    values: (_u, splits) => splits.map(() => null),
  }
}

export function logMinorGridAxis(): Pick<uPlot.Axis, 'space' | 'splits' | 'filter' | 'values'> {
  return {
    space: 0,
    splits: (_u, _axisIdx, scaleMin, scaleMax) => minorLogSplits(scaleMin, scaleMax),
    filter: (_u, splits) => splits,
    values: (_u, splits) => splits.map(() => null),
  }
}

function trim(s: string): string {
  return s.includes('.') ? s.replace(/\.?0+$/, '') : s
}
