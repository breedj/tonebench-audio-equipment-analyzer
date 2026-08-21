import type uPlot from 'uplot'

/**
 * The frequency x-axis shared by SpectrumChart.vue and WaterfallChart.vue.
 *
 * The two charts are stacked and deliberately show one x span (see useChartDisplay.ts's
 * activeXRange) — they are only readable as one picture if a column means the same frequency in
 * both, which includes ticking and labelling it identically. Both spread `freqAxis()` into their
 * x axis, so there is exactly one place that decides where the ticks go and what they say.
 *
 * This replaces uPlot's built-in log-axis ticking rather than tuning it, because that one breaks
 * down on a zoom (both problems are visible in uPlot.esm.js's logAxisSplits/log10AxisValsFilt):
 *
 *  - its first split is *literally* scaleMin, and scaleMin here is a raw FFT bin edge or wherever
 *    a drag-zoom happened to end — so the first label read "5.859375" or "1234.5678";
 *  - it steps by 10^floor(log10(scaleMin)), so any window narrower than one such step contains
 *    exactly one split. Zoomed into, say, 990–1010 Hz that left a single label sitting half off the
 *    left edge, and no way to tell what range was on screen.
 *
 * Everything below is pure and unit-tested; the uPlot adapters at the bottom only fetch the span
 * and the plot width off the instance.
 */

/** Minimum pixels between two labelled ticks. Also the axis's `space`, so uPlot agrees on sizing. */
export const FREQ_AXIS_SPACE = 60

/**
 * Mantissas to try per decade, finest first — the finest set whose tightest pixel gap still clears
 * `minSpacePx` wins. Same 1/2/5 progression uPlot's own filter falls back through, which is why a
 * fully zoomed-out audio axis keeps the familiar 20 · 50 · 100 … 10k · 20k it always had.
 */
const MANTISSA_LADDER = [
  [1, 2, 3, 4, 5, 6, 7, 8, 9],
  [1, 2, 5],
  [1],
]

/**
 * Below this span ratio a decade ladder has nothing to offer — a window like 990–1010 Hz contains
 * no round mantissa at all — and the axis is narrow enough that log and linear spacing are visually
 * the same thing, so evenly spaced round numbers are both possible and nicer.
 */
const LOG_LADDER_MIN_RATIO = 3

/** Above this ratio the axis crosses enough magnitudes that each tick needs its own unit (Hz vs kHz). */
const PER_TICK_UNIT_MIN_RATIO = 10

/** Kills the float dust in 3 * 0.001 and friends, without touching a value's real precision. */
function tidy(v: number): number {
  return Number(v.toPrecision(12))
}

/** Round-number ticks of the form m·10^e, for m in `mantissas`, that land inside [min, max]. */
function decadeTicks(mantissas: number[], min: number, max: number): number[] {
  const out: number[] = []
  const lo = Math.floor(Math.log10(min))
  const hi = Math.floor(Math.log10(max))
  for (let e = lo; e <= hi; e++) {
    const base = Math.pow(10, e)
    for (const m of mantissas) {
      const v = tidy(m * base)
      if (v >= min * (1 - 1e-9) && v <= max * (1 + 1e-9)) out.push(v)
    }
  }
  return out
}

/** 1, 2 or 5 times a power of ten — the smallest such step that is at least `raw`. */
function niceStep(raw: number): number {
  const e = Math.pow(10, Math.floor(Math.log10(raw)))
  const m = raw / e
  return tidy((m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * e)
}

/** Evenly spaced round ticks across [min, max], for spans too narrow for a decade ladder. */
function linearTicks(min: number, max: number, widthPx: number, minSpacePx: number): number[] {
  const target = Math.max(2, Math.floor(widthPx / minSpacePx))
  const step = niceStep((max - min) / target)
  if (!(step > 0)) return [min, max]

  const out: number[] = []
  const firstIdx = Math.ceil(min / step - 1e-9)
  // The count is bounded by `target` by construction; the cap only stops a degenerate span from
  // spinning here.
  for (let i = 0; i < 1000; i++) {
    const v = tidy((firstIdx + i) * step)
    if (!(v <= max * (1 + 1e-12))) break
    out.push(v)
  }
  return out
}

/** The tightest gap between neighbouring ticks, in pixels. Infinity for fewer than two ticks. */
function minGapPx(ticks: number[], posOf: (v: number) => number): number {
  let gap = Infinity
  for (let i = 1; i < ticks.length; i++) gap = Math.min(gap, posOf(ticks[i]) - posOf(ticks[i - 1]))
  return gap
}

/**
 * Where to put the ticks on a log frequency axis spanning [min, max] across `widthPx` pixels.
 * Always returns round numbers, and always at least two of them, at any zoom depth.
 */
export function freqTickSplits(min: number, max: number, widthPx: number, minSpacePx = FREQ_AXIS_SPACE): number[] {
  if (!(min > 0) || !(max > min) || !(widthPx > 0)) return [min]

  const logMin = Math.log10(min)
  const logSpan = Math.log10(max) - logMin
  const posOf = (v: number) => ((Math.log10(v) - logMin) / logSpan) * widthPx

  if (max / min >= LOG_LADDER_MIN_RATIO) {
    for (const mantissas of MANTISSA_LADDER) {
      const ticks = decadeTicks(mantissas, min, max)
      if (ticks.length >= 2 && minGapPx(ticks, posOf) >= minSpacePx) return ticks
    }
    // Nothing cleared the spacing budget — a chart too narrow for one label per decade. Crowded
    // decades still beat a linear grid stretched across several magnitudes.
    const decades = decadeTicks([1], min, max)
    if (decades.length >= 2) return decades
  }

  return linearTicks(min, max, widthPx, minSpacePx)
}

/** Decimals needed for two values `gap` apart to print differently, clamped to something readable. */
function decimalsFor(gap: number, cap: number): number {
  if (!(gap > 0) || !Number.isFinite(gap)) return cap
  return Math.min(cap, Math.max(0, Math.ceil(-Math.log10(gap))))
}

function trimTrailingZeros(s: string): string {
  // Safe on integers too — toFixed always leaves a '.', so the trailing zeros it eats can only
  // ever be decimals.
  return s.includes('.') ? s.replace(/\.?0+$/, '') : s
}

/**
 * Tick text, decided from the axis as a whole rather than one value at a time.
 *
 * Which unit a tick gets is a property of the span, not of the tick: across the full 20 Hz–20 kHz
 * view every tick wants its own (20 · 50 · … · 1.0k · … · 20k), but zoomed into 990–1010 Hz that
 * same rule prints "998, 1.000k, 1.002k" and reads as three unrelated numbers. Under a decade of
 * span, the whole axis commits to one unit.
 *
 * How many decimals is a property of the neighbours: enough that no two adjacent ticks print the
 * same text, and no more than that — which is what kept the first label from reading "5.859375".
 */
export function freqTickLabels(splits: (number | null)[], min: number, max: number): (string | null)[] {
  const perTickUnit = min > 0 && max / min >= PER_TICK_UNIT_MIN_RATIO

  return splits.map((v, i) => {
    if (v == null || !Number.isFinite(v)) return null

    const prev = splits[i - 1]
    const next = splits[i + 1]
    let gap = Infinity
    if (prev != null) gap = Math.min(gap, Math.abs(v - prev))
    if (next != null) gap = Math.min(gap, Math.abs(next - v))

    // A lone tick has no neighbour to be distinguished from, so there is no floor to work up from
    // and no house style to match: print it as precisely as the cap allows and trim what that
    // leaves over.
    const lone = !Number.isFinite(gap)

    if (perTickUnit ? v >= 1000 : min >= 1000) {
      // The v >= 10000 ? 0 : 1 floor is the long-standing look of this axis (1.0k, 2.0k, …, 10k,
      // 20k); the gap only ever adds decimals to it, never takes any away.
      const decimals = lone ? 3 : Math.max(v >= 10000 ? 0 : 1, decimalsFor(gap / 1000, 3))
      const text = (v / 1000).toFixed(decimals)
      return `${lone ? trimTrailingZeros(text) : text}k`
    }
    return trimTrailingZeros(v.toFixed(decimalsFor(gap, 2)))
  })
}

/** Plot-area width in CSS pixels — the x scale's two edges are exactly the plot area's two edges. */
function plotWidthPx(u: uPlot, scaleKey: string, min: number, max: number): number {
  const width = Math.abs(u.valToPos(max, scaleKey) - u.valToPos(min, scaleKey))
  return Number.isFinite(width) && width > 0 ? width : 800
}

/**
 * The shared x-axis config. Spread into each chart's axis so the chart-specific bits (grid and
 * stroke colours) stay with the chart:
 *
 *   { ...freqAxis(), stroke: theme.axis, grid: { ... }, ticks: { ... } }
 */
export function freqAxis(): Pick<uPlot.Axis, 'space' | 'splits' | 'filter' | 'values'> {
  return {
    space: FREQ_AXIS_SPACE,
    splits: (u, axisIdx, scaleMin, scaleMax) => {
      const scaleKey = u.axes[axisIdx].scale ?? 'x'
      return freqTickSplits(scaleMin, scaleMax, plotWidthPx(u, scaleKey, scaleMin, scaleMax))
    },
    // Every split generated above is one worth labelling — uPlot's default log filter drops the
    // ones whose mantissa isn't round, which on a tight zoom is all of them.
    filter: (_u, splits) => splits,
    values: (u, splits, axisIdx) => {
      const sc = u.scales[u.axes[axisIdx].scale ?? 'x']
      return freqTickLabels(splits, sc?.min ?? splits[0], sc?.max ?? splits[splits.length - 1])
    },
  }
}
