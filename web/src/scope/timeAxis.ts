/**
 * The oscilloscope's horizontal axis: how a time window is read, written and labelled.
 *
 * <p><b>Two units are in play and the split is deliberate.</b> The window travels and is stored in
 * <b>seconds</b> — that is what the server's <code>fromS</code>/<code>toS</code> carry, what
 * <code>OscilloscopeParameters.ChartTimeFromS</code> is named for, and the one unit in which a
 * stored window cannot mean two things. What uPlot plots is <b>milliseconds</b>, which is
 * <code>OscilloscopeChart</code>'s own long-standing convention on the FFT screen and is what makes
 * the <code>ms</code> annotation quantity line up with the scale a note is dropped onto: a note
 * stored as 1.5 and labelled "1.500 ms" is right, where a scale in seconds would store 0.0015 under
 * that same label. The conversions live here, as named functions, so that nothing else in the scope
 * spells <code>* 1000</code>.</p>
 *
 * <p><b>The displayed unit is a third thing again</b>, and follows the window's own size — µs for a
 * 50 µs record, ms for a 20 ms one, s for a long capture — the arrangement
 * <code>response/delayAxis.ts</code> already uses for the delay window, and for the same reason:
 * a 20 µs record read in milliseconds is "-0.005 … 0.015", which is arithmetic rather than a
 * reading. One formatter serves the axis ticks, the cursor readout and the panel's boxes, so the
 * three can never disagree about what a figure means.</p>
 */

/**
 * A scope's horizontal graticule, and since 2026-11-20 the thing the record is built from.
 *
 * <p>It used to be a label divisor and nothing else: the record length was typed in milliseconds
 * and the s/div readout was whatever the window happened to be over ten. That was deliberate — the
 * chart draws a window <em>into</em> the record here, so a long record costs memory rather than
 * detail — and what it cost is that the one control every scope user reaches for first did not
 * exist. Now the timebase sets the record (<code>10 × time/div</code>) and the zoom stays as a
 * second look, which is how a DSO's own zoom window behaves.</p>
 */
export const HORIZONTAL_DIVISIONS = 10

/**
 * The timebase's detents, in seconds per division.
 *
 * <p>A 1-2-5 ladder like the V/div one beside it, because a graticule is read by counting squares
 * and that only works if a square is a round number — which is the whole argument for a knob with
 * detents rather than the free time the record box used to take.</p>
 *
 * <p><b>Its ends are the server's own, divided by ten</b>: <code>OscilloscopeRequest</code>'s
 * <code>MinRecordSeconds</code> (20 µs) and <code>MaxRecordSeconds</code> (1 s, the figure the
 * <code>.tbench</code> size budget sets). So the bounds need no second opinion and cannot drift
 * from the refusal the server would answer with.</p>
 */
export const SCOPE_TIME_PER_DIVISION_LADDER: readonly number[] = Object.freeze([
  2e-6, 5e-6,
  1e-5, 2e-5, 5e-5,
  1e-4, 2e-4, 5e-4,
  1e-3, 2e-3, 5e-3,
  1e-2, 2e-2, 5e-2,
  1e-1,
])

/** The whole record a timebase asks for: ten divisions of it. */
export function recordSecondsFor(timePerDivisionS: number): number {
  return HORIZONTAL_DIVISIONS * timePerDivisionS
}

/** And back — what a record length reads as on the timebase. */
export function timePerDivisionFor(recordSeconds: number): number {
  return recordSeconds / HORIZONTAL_DIVISIONS
}

/**
 * The nearest rung to a figure, or the figure itself where it is already one.
 *
 * <p><b>Nearest in the logarithm</b>, not in seconds: the ladder spans five decades, so a linear
 * distance would snap almost everything to the top rung. The reader's sense of "nearest" on a
 * 1-2-5 ladder is a ratio, which is what this measures.</p>
 *
 * <p>Used only to read an off-ladder stored record back onto the control. A measurement captured at
 * some odd length is never re-captured at a rung behind the user's back — the panel folds its own
 * figure into the list instead.</p>
 */
export function snapTimePerDivision(timePerDivisionS: number): number {
  if (!Number.isFinite(timePerDivisionS) || timePerDivisionS <= 0) return SCOPE_TIME_PER_DIVISION_LADDER[8]
  let best = SCOPE_TIME_PER_DIVISION_LADDER[0]
  let bestDistance = Number.POSITIVE_INFINITY
  for (const rung of SCOPE_TIME_PER_DIVISION_LADDER) {
    const distance = Math.abs(Math.log(timePerDivisionS / rung))
    if (distance < bestDistance) {
      bestDistance = distance
      best = rung
    }
  }
  return best
}

/** The display units a time window can be read in, coarsest first. */
export type ScopeTimeUnit = 's' | 'ms' | 'us'

/** How a window of a given size is read: which unit, and what one unit is worth in milliseconds. */
export interface ScopeTimeScale {
  unit: ScopeTimeUnit
  /** The label, with the micro sign spelled out — 'µs' rather than the ASCII 'us' the id uses. */
  label: string
  /** Milliseconds per unit: 1000 for seconds, 1 for ms, 0.001 for µs. */
  msPerUnit: number
}

const SECONDS: ScopeTimeScale = { unit: 's', label: 's', msPerUnit: 1000 }
const MILLI: ScopeTimeScale = { unit: 'ms', label: 'ms', msPerUnit: 1 }
const MICRO: ScopeTimeScale = { unit: 'us', label: 'µs', msPerUnit: 0.001 }

/** Seconds as the chart plots them. */
export function toChartMs(seconds: number): number {
  return seconds * 1000
}

/** And back, for anything that leaves the chart — a dragged window, a stored field. */
export function fromChartMs(ms: number): number {
  return ms / 1000
}

/**
 * The unit a window of <paramref name="spanMs"/> milliseconds is read in.
 *
 * <p>The thresholds are the units' own boundaries rather than a judgement: a span of a millisecond
 * or more has whole milliseconds in it to label, and a span of a second or more has whole seconds.
 * A non-finite or non-positive span falls back to milliseconds, since a chart with no window yet
 * still has to put something on its axis.</p>
 */
export function scopeTimeScale(spanMs: number): ScopeTimeScale {
  if (!Number.isFinite(spanMs) || spanMs <= 0) return MILLI
  if (spanMs >= 1000) return SECONDS
  if (spanMs >= 1) return MILLI
  return MICRO
}

/**
 * How many decimals a figure wants, given the window it is read against.
 *
 * <p>Four significant figures across the window, which is a thousandth of the span — finer than a
 * reader can pick a point off the plot, and coarse enough that a figure does not sprawl. Fewer
 * would round a cursor's own position away: at a 20 ms window one decimal is a twentieth of a
 * division, and two readings a pixel apart would print the same number.</p>
 */
function decimalsFor(scale: ScopeTimeScale, spanMs: number): number {
  const perUnit = spanMs / scale.msPerUnit
  if (perUnit >= 100) return 1
  if (perUnit >= 10) return 2
  return 3
}

/**
 * A time in milliseconds, written for a window of <paramref name="spanMs"/> — the one formatter the
 * axis ticks, the cursor readout and the panel's boxes all go through.
 *
 * <p><paramref name="withUnit"/> is false for an axis tick, where the unit belongs on the axis
 * label and repeating it on every tick is noise, and true everywhere a figure stands alone.</p>
 */
export function formatScopeTime(ms: number, spanMs: number, withUnit = true): string {
  if (!Number.isFinite(ms)) return '—'
  const scale = scopeTimeScale(spanMs)
  const value = ms / scale.msPerUnit
  const text = value.toFixed(decimalsFor(scale, spanMs))
  // -0.00 is arithmetic showing through; a reader wants 0.00.
  const cleaned = /^-0(\.0*)?$/.test(text) ? text.slice(1) : text
  return withUnit ? `${cleaned} ${scale.label}` : cleaned
}

/**
 * The per-division figure a scope's status line quotes — "2.00 ms/div".
 *
 * <p>Its unit comes from the <em>division</em> rather than from the whole window, which is what
 * makes the reading match the graticule a user is counting squares on.</p>
 */
export function formatPerDivision(spanMs: number): string {
  if (!Number.isFinite(spanMs) || spanMs <= 0) return '—'
  const perDiv = spanMs / HORIZONTAL_DIVISIONS
  const scale = scopeTimeScale(perDiv)
  return `${(perDiv / scale.msPerUnit).toFixed(2)} ${scale.label}/div`
}

/** A window, in the seconds everything outside the chart holds it in. */
export interface ScopeTimeWindow {
  fromS: number
  toS: number
}

/**
 * The window a record of <paramref name="recordSeconds"/> covers with
 * <paramref name="preTriggerFraction"/> of it ahead of the trigger.
 *
 * <p>This is what the chart draws <b>before anything has been captured</b>, which is why it is
 * derived from the settings rather than from a record: an empty scope draws the axes the next
 * capture will land on, the way every chart here answers an empty state with empty axes rather than
 * by tearing its plot down.</p>
 */
export function recordWindow(recordSeconds: number, preTriggerFraction: number): ScopeTimeWindow {
  const span = Number.isFinite(recordSeconds) && recordSeconds > 0 ? recordSeconds : 0.02
  const pre = Number.isFinite(preTriggerFraction) ? Math.min(Math.max(preTriggerFraction, 0), 1) : 0.25
  return { fromS: -span * pre, toS: span * (1 - pre) }
}

/**
 * A window brought inside <paramref name="bounds"/> without being turned inside out.
 *
 * <p>Three things it has to survive, all of them reachable by typing into the panel's boxes or by
 * dragging a zoom box off the end of the plot: a reversed pair, a pair with no span at all, and a
 * pair that reaches outside the record. A reversed or empty window is <b>refused</b> — the bounds
 * come back instead — rather than silently sorted, because a window of zero width doubled is still
 * zero and blanks the plot, which reads as a chart that has broken rather than a figure that was
 * mistyped.</p>
 */
export function clampWindow(window: ScopeTimeWindow, bounds: ScopeTimeWindow): ScopeTimeWindow {
  const { fromS: lo, toS: hi } = bounds
  if (!Number.isFinite(lo) || !Number.isFinite(hi) || hi <= lo) return { fromS: -0.005, toS: 0.015 }
  if (!Number.isFinite(window.fromS) || !Number.isFinite(window.toS)) return { fromS: lo, toS: hi }

  const from = Math.min(Math.max(window.fromS, lo), hi)
  const to = Math.min(Math.max(window.toS, lo), hi)
  if (!(to > from)) return { fromS: lo, toS: hi }
  return { fromS: from, toS: to }
}

/** True when the window is the whole of the bounds — what decides whether a Reset zoom button is offered. */
export function isWholeRecord(window: ScopeTimeWindow, bounds: ScopeTimeWindow): boolean {
  const span = bounds.toS - bounds.fromS
  if (!(span > 0)) return true
  // A thousandth of the record: tighter than any drag and looser than floating-point noise.
  const slack = span * 1e-3
  return Math.abs(window.fromS - bounds.fromS) <= slack && Math.abs(window.toS - bounds.toS) <= slack
}

/**
 * The window after the record's own geometry has moved under it.
 *
 * <p>Needed because the Acquisition panel is live: the record length and the pre-trigger fraction
 * can both change while the scope is running, and a window left where it was can then hold no
 * samples at all — the chart draws nothing with a live capture on hand and no error anywhere, which
 * is the Roll-crossing fault in another costume.</p>
 *
 * <p><b>A window that was the record follows it; a deliberate zoom is only clamped.</b> That is the
 * whole rule, and the distinction matters: rewriting a zoom would throw away a stretch the reader
 * chose to look at, while leaving an unzoomed window behind would make shortening the record read
 * as the trace disappearing.</p>
 */
export function followRecordWindow(
  window: ScopeTimeWindow,
  oldBounds: ScopeTimeWindow,
  newBounds: ScopeTimeWindow,
): ScopeTimeWindow {
  if (isWholeRecord(window, oldBounds)) return { fromS: newBounds.fromS, toS: newBounds.toS }
  return clampWindow(window, newBounds)
}

/**
 * Where the trigger sits inside the record, from a pointer dragged along the time axis.
 *
 * <p>The axis is <b>trigger-relative</b>, so t = 0 never moves on it — what the gesture sets is how
 * much of the record is captured before the trigger, which is the fraction the Acquisition panel
 * types as a percentage. A pointer at the record's own start is 0 and at its end is 1.</p>
 *
 * <p>Clamped rather than refused past either end: a drag that runs off the plot means "all of it
 * one side", which is a setting the panel allows, and stopping the grip dead at 93 % because the
 * pointer left the canvas would read as the drag having broken.</p>
 */
export function preTriggerFromPointer(pointerS: number, recordSeconds: number, oldFraction: number): number {
  const fallback = Number.isFinite(oldFraction) ? Math.min(Math.max(oldFraction, 0), 1) : 0.25
  if (!Number.isFinite(pointerS) || !Number.isFinite(recordSeconds) || recordSeconds <= 0) return fallback
  // The record runs from -fraction*T to (1-fraction)*T, so the pointer's distance from the record's
  // own start, over the record's length, is the fraction it is asking for -- and the old fraction is
  // what says where that start is.
  const start = -recordSeconds * fallback
  return Math.min(Math.max((pointerS - start) / recordSeconds, 0), 1)
}
