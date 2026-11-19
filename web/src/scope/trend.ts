/**
 * One measurement's readings plotted against the records they came from — the trend.
 *
 * <p><b>What it answers that a statistics column cannot.</b> Mean, Min, Max and Stdev are
 * <em>order-free</em>: a reading that drifted steadily across a run and one that sat still and
 * jumped once give the same five figures, and so does one that alternates between two values every
 * record. Those are three different faults — a warming-up DUT, an intermittent connection, and
 * something switching — and each of them is one glance at a plot. This is the plot.</p>
 *
 * <p><b>Here rather than in the component</b> for this project's usual reason: nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in, and every rule below is
 * arithmetic that is quietly wrong rather than visibly broken when it is.</p>
 *
 * <p><b>Gaps stay gaps.</b> A record the measurement had no reading on is a NaN in the series and
 * is drawn as a break in the line, never as a zero and never by joining across it: a frequency that
 * keeps losing its second crossing is exactly the thing this plot is reached for, and a line drawn
 * straight through those records would hide it.</p>
 */

/** A stretch of the trend that can be stroked as one path — the gaps are between them. */
export interface TrendSegment {
  /** Points in plot coordinates, x increasing. */
  points: readonly { x: number; y: number }[]
}

export interface TrendAxis {
  /** The value at the bottom of the plot and at the top. */
  min: number
  max: number
  /** Where the labelled lines sit, in value. */
  ticks: readonly number[]
}

export interface TrendLayout {
  /** The segments to stroke, in order. Empty where nothing finite has been read. */
  segments: readonly TrendSegment[]
  axis: TrendAxis
  /** The mean of the readings drawn, in plot coordinates, or null with nothing to average. */
  meanY: number | null
  /** The ±1σ band in plot coordinates — top first, as the canvas measures. */
  band: { top: number; bottom: number } | null
  /** The record numbers at the two ends of the x axis, counting from one. */
  firstRecord: number
  lastRecord: number
  /** How many of the readings drawn were absent, so the caption can say so. */
  gaps: number
}

/**
 * A tick written against the middle of the axis — the middle itself in full, the others as a
 * signed offset from it.
 *
 * <p><b>This is the whole legibility of the plot and not a flourish.</b> Every reading on this
 * screen is written to four significant figures, which is right for a reading and useless for an
 * axis fitted to a spread: a peak-to-peak of 1.419 V that wanders over a millivolt has three ticks
 * that all read <code>1.419 V</code>, so the picture shows a spread the labels say is not there.
 * Written as <code>1.419 V</code> with <code>+1.200 mV</code> above it and
 * <code>−1.200 mV</code> below, both numbers are at their own magnitude — which is the same rule
 * the statistics columns already follow, applied to an axis.</p>
 *
 * <p>The formatter is the row's own, so the offsets are in the reading's units: a level goes
 * through the SI-prefix ladder and comes back in millivolts, where a percentage or a bare ratio
 * keeps its decimals.</p>
 */
export function trendTickLabel(
  value: number,
  centre: number,
  format: (value: number) => string,
): string {
  const delta = value - centre
  // A hair either side of the middle is the middle: an axis whose span is zero to floating point
  // would otherwise label its centre as an offset of 1e-19.
  if (delta === 0) return format(centre)
  return `${delta > 0 ? '+' : '−'}${format(Math.abs(delta))}`
}

/** How far a flat trend is opened out, as a fraction of the value. */
const FLAT_PAD_FRACTION = 0.05

/**
 * The smallest window a flat trend is drawn in, so a reading of exactly zero every record has an
 * axis at all rather than a zero-height one that divides by nothing.
 */
const FLAT_PAD_FLOOR = 1e-12

/**
 * Lays the trend out inside a plot box.
 *
 * <p><b>The y axis is fitted to the readings and not to zero</b>, which is the opposite of the rule
 * for a bar chart and right here: the spread is the subject, and a 306.864 mV peak-to-peak that
 * wanders over 757 µV is a flat line on any axis that starts at zero. A trend with nothing to show
 * is therefore drawn as a line across the middle of a window a few per cent wide rather than as an
 * empty box — "it did not move" is a reading, and it should look like one.</p>
 *
 * @param series The readings, oldest first, NaN where a record had none.
 * @param firstRecord Which record the first reading was, counting from one.
 * @param box The plot area in canvas pixels.
 */
export function layOutTrend(
  series: ArrayLike<number>,
  firstRecord: number,
  box: { left: number; top: number; width: number; height: number },
): TrendLayout {
  const count = series.length
  const lastRecord = count === 0 ? 0 : firstRecord + count - 1

  let min = Number.POSITIVE_INFINITY
  let max = Number.NEGATIVE_INFINITY
  let sum = 0
  let finite = 0
  for (let i = 0; i < count; i += 1) {
    const v = series[i]
    if (!Number.isFinite(v)) continue
    finite += 1
    sum += v
    if (v < min) min = v
    if (v > max) max = v
  }

  if (finite === 0) {
    return {
      segments: [],
      axis: { min: 0, max: 1, ticks: [] },
      meanY: null,
      band: null,
      firstRecord,
      lastRecord,
      gaps: count,
    }
  }

  const axis = trendAxis(min, max)
  const mean = sum / finite

  // The deviation over the readings DRAWN rather than the accumulator's own: the band has to belong
  // to the picture it is drawn on, and the accumulator counts records this series no longer keeps.
  let m2 = 0
  for (let i = 0; i < count; i += 1) {
    const v = series[i]
    if (!Number.isFinite(v)) continue
    m2 += (v - mean) * (v - mean)
  }
  const stdev = Math.sqrt(m2 / finite)

  const toY = (value: number) =>
    box.top + box.height * (1 - (value - axis.min) / (axis.max - axis.min))

  // One point per reading, spread across the box. A single reading sits in the middle rather than
  // at the left edge, where a lone dot in a corner reads as a drawing fault.
  const toX = (index: number) =>
    count === 1 ? box.left + box.width / 2 : box.left + (box.width * index) / (count - 1)

  const segments: TrendSegment[] = []
  let current: { x: number; y: number }[] = []
  let gaps = 0
  for (let i = 0; i < count; i += 1) {
    const v = series[i]
    if (!Number.isFinite(v)) {
      gaps += 1
      if (current.length > 0) {
        segments.push({ points: current })
        current = []
      }
      continue
    }
    current.push({ x: toX(i), y: toY(v) })
  }
  if (current.length > 0) segments.push({ points: current })

  return {
    segments,
    axis,
    meanY: toY(mean),
    band: { top: toY(mean + stdev), bottom: toY(mean - stdev) },
    firstRecord,
    lastRecord,
    gaps,
  }
}

/**
 * The value window and its labelled lines.
 *
 * <p>Three lines, at the bottom, the middle and the top of the window, rather than a 1/2/5 ladder.
 * The window is fitted to the data, so a ladder would put its ticks at arbitrary places inside it
 * and frequently only one of them — and what a reader takes off this axis is the <em>extent</em>
 * ("it moved by this much"), which three labelled lines give exactly.</p>
 */
export function trendAxis(min: number, max: number): TrendAxis {
  let lo = min
  let hi = max

  if (!(hi > lo)) {
    const pad = Math.max(Math.abs(lo) * FLAT_PAD_FRACTION, FLAT_PAD_FLOOR)
    lo -= pad
    hi += pad
  } else {
    // A tenth of the span either side, so the extremes are not drawn on the frame itself.
    const pad = (hi - lo) * 0.1
    lo -= pad
    hi += pad
  }

  return { min: lo, max: hi, ticks: [lo, (lo + hi) / 2, hi] }
}
