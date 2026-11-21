/**
 * What the cursors say on the chart itself — the two styles a bench scope offers.
 *
 * <p><b>Fixed</b> puts every figure in one box on the plot; <b>Following</b> pins each cursor's own
 * value to its line and draws a double-headed arrow between the pair carrying the difference. They
 * are two answers to the same question — <em>where does the reader look for the number</em> — and
 * which is better depends entirely on the trace underneath, which is why this is a control rather
 * than a decision.</p>
 *
 * <p>Both were read off the display rail's table before this, a long way from the lines they
 * describe. The table stays: it is the one place every figure is listed in text, and it is what a
 * reader falls back to when the box would cover the very part of the trace being measured.</p>
 *
 * <p>Here: the arithmetic and the words. Nothing in a <code>.vue</code> is testable under the node
 * environment vitest runs in, and the two things most easily got wrong are exactly arithmetic — how
 * big the box has to be for its own content, and where it may sit without leaving the plot. The
 * text measurement is <b>injected</b>, as <code>charts/annotationLayout.ts</code> does it, so a test
 * can hand in a metric it controls instead of needing a canvas.</p>
 */

import type { SegmentedOption } from '../panels/segmented'
import {
  cursorLevelsRead,
  cursorReadings,
  formatCursorFrequency,
  type ScopeCursors,
} from './cursors'
import { formatMathValue } from './math'
import { formatSeconds, type ScopeReading } from './readings'
import { formatScopeTime } from './timeAxis'
import { formatVolts } from './voltAxis'

/** Which of the two the chart is drawing. */
export type ScopeCursorStyle = 'Fixed' | 'Following'

/**
 * Fixed by default, which is the Siglent's own default and the safer of the two: a box sits in one
 * place the reader can learn, where Following puts text wherever the cursors happen to be — over
 * the trace as often as not.
 */
export const DEFAULT_SCOPE_CURSOR_STYLE: ScopeCursorStyle = 'Fixed'

/** The rail's control, in the order the Siglent offers them. */
export const SCOPE_CURSOR_STYLES: readonly SegmentedOption<ScopeCursorStyle>[] = Object.freeze([
  {
    value: 'Fixed' as const,
    label: 'Fixed',
    title:
      'Every cursor figure in one box, which you can drag anywhere in the plot. One place to look, and it never lands on the part of the trace you are measuring unless you put it there.',
  },
  {
    value: 'Following' as const,
    label: 'Following',
    title:
      'Each cursor carries its own value, with a double-headed arrow between the pair carrying the difference. The figures are where the lines are, at the cost of text that moves as you drag.',
  },
])

/** Whether a stored value is one of the two, so an unknown one falls back rather than renders blank. */
export function isScopeCursorStyle(value: unknown): value is ScopeCursorStyle {
  return value === 'Fixed' || value === 'Following'
}

/** Which of the four lines a label belongs to. Shared with the chart's own grips. */
export type CursorId = 'timeA' | 'timeB' | 'voltA' | 'voltB'

/**
 * The slew rate between the two pairs — the Siglent's <code>ΔY/ΔX</code>.
 *
 * <p><b>A reading about a pair of pairs</b>, so it needs both: one pair alone names a difference in
 * one quantity and nothing to divide it by. Null is the honest answer there, and null again when
 * Δt is zero.</p>
 *
 * <p>Written through <code>formatMathValue</code>, which is the math channel's own formatter and
 * carries the rule this needs: <b>the denominator is scaled, never the numerator</b>, so a slew
 * rate reads 20.30 V/ms and not 20300 V/s. The two are the same figure and the first is the one an
 * amplifier datasheet quotes — and using the same function is what stops this screen growing a
 * second opinion about how a rate is written.</p>
 */
export function cursorSlopeReading(cursors: ScopeCursors): ScopeReading | null {
  if (!cursors.timeOn || !cursorLevelsRead(cursors)) return null
  const dt = cursors.timeBS - cursors.timeAS
  const dv = cursors.voltBV - cursors.voltAV
  if (!Number.isFinite(dt) || !Number.isFinite(dv) || dt === 0) return null
  return {
    label: 'ΔV/Δt',
    value: formatMathValue(dv / dt, 'VoltPerSecond'),
    title:
      'The level difference divided by the time between the cursors — the average slope of whatever they have been put either side of. Put them on the straight part of an edge and it is a slew rate; put them anywhere else and it is the average slope of what is between them, which is a different claim.',
  }
}

/**
 * Every row the Fixed box prints.
 *
 * <p><code>cursorReadings</code> plus the slope, so the box and the rail's table cannot disagree
 * about a figure — the one rule worth keeping as the set grows.</p>
 */
export function cursorBoxRows(
  cursors: ScopeCursors,
  voltsPerDivision: number,
  spanMs: number,
): ScopeReading[] {
  const rows = cursorReadings(cursors, voltsPerDivision, spanMs)
  const slope = cursorSlopeReading(cursors)
  if (slope) rows.push(slope)
  return rows
}

/**
 * What one cursor's own line carries under Following, or null where that cursor has no value.
 *
 * <p>The value alone: the letter is already drawn on the line by the chart, so repeating it here
 * would print <code>A A 11.05 µs</code>.</p>
 */
export function cursorValueLabel(
  cursors: ScopeCursors,
  id: CursorId,
  voltsPerDivision: number,
  spanMs: number,
): string | null {
  switch (id) {
    case 'timeA':
    case 'timeB': {
      if (!cursors.timeOn) return null
      const seconds = id === 'timeA' ? cursors.timeAS : cursors.timeBS
      if (!Number.isFinite(seconds)) return null
      return formatScopeTime(seconds * 1000, spanMs)
    }
    case 'voltA':
    case 'voltB': {
      if (!cursorLevelsRead(cursors)) return null
      const volts = id === 'voltA' ? cursors.voltAV : cursors.voltBV
      if (!Number.isFinite(volts)) return null
      return formatVolts(volts, voltsPerDivision)
    }
  }
}

/**
 * What the horizontal arrow between the time cursors carries.
 *
 * <p>Δt and its reciprocal, and the slope when both pairs are on — which is where the Siglent puts
 * it too, and the right place: the slope is a rate <em>over</em> that stretch, so it belongs on the
 * arrow that marks the stretch.</p>
 */
export function cursorTimeSpanLabels(cursors: ScopeCursors): string[] {
  if (!cursors.timeOn) return []
  const dt = Math.abs(cursors.timeBS - cursors.timeAS)
  if (!Number.isFinite(dt) || dt === 0) return []
  const rows = [`Δt ${formatSeconds(dt)}`, `1/Δt ${formatCursorFrequency(1 / dt)}`]
  const slope = cursorSlopeReading(cursors)
  if (slope) rows.push(`${slope.label} ${slope.value}`)
  return rows
}

/** What the vertical arrow between the level cursors carries. */
export function cursorLevelSpanLabels(cursors: ScopeCursors, voltsPerDivision: number): string[] {
  if (!cursors.voltOn) return []
  const dv = Math.abs(cursors.voltBV - cursors.voltAV)
  if (!Number.isFinite(dv)) return []
  return [`ΔV ${formatVolts(dv, voltsPerDivision)}`]
}

// ---------------------------------------------------------------------------
// The Fixed box's geometry
// ---------------------------------------------------------------------------

/** The box's inset from its own edge, in CSS pixels before the canvas scale. */
export const CURSOR_BOX_PADDING = 6
/** The gap between a row's label and its value. */
export const CURSOR_BOX_GAP = 10
/** One row's height. */
export const CURSOR_BOX_LINE_HEIGHT = 13

export interface CursorBoxMetrics {
  width: number
  height: number
  /** The label column's width, so the values line up on one edge. */
  labelWidth: number
  lineHeight: number
  padding: number
  gap: number
}

/**
 * How big the box has to be for the rows it holds.
 *
 * <p>Measured from its own content rather than given a fixed width, because the figures it carries
 * span everything from <code>0.000 Hz</code> to <code>−1.234 mV/µs</code> and a box sized for the
 * short ones clips the long ones — which on a readout is the one failure that looks like a figure
 * rather than like a layout fault.</p>
 */
export function layoutCursorBox(
  rows: readonly ScopeReading[],
  measure: (text: string) => number,
): CursorBoxMetrics {
  let labelWidth = 0
  let valueWidth = 0
  for (const row of rows) {
    labelWidth = Math.max(labelWidth, measure(row.label))
    valueWidth = Math.max(valueWidth, measure(row.value))
  }
  return {
    width: CURSOR_BOX_PADDING * 2 + labelWidth + CURSOR_BOX_GAP + valueWidth,
    height: CURSOR_BOX_PADDING * 2 + Math.max(rows.length, 1) * CURSOR_BOX_LINE_HEIGHT,
    labelWidth,
    lineHeight: CURSOR_BOX_LINE_HEIGHT,
    padding: CURSOR_BOX_PADDING,
    gap: CURSOR_BOX_GAP,
  }
}

/** A rectangle in the chart's own pixels. */
export interface CursorBoxRect {
  left: number
  top: number
  width: number
  height: number
}

/** Where the box starts life: just inside the top-left corner, the Siglent's own place. */
export const DEFAULT_CURSOR_BOX_POSITION: Readonly<{ x: number; y: number }> = Object.freeze({
  x: 0,
  y: 0,
})

function clamp01(value: number): number {
  if (!Number.isFinite(value)) return 0
  return Math.min(Math.max(value, 0), 1)
}

/**
 * The box's pixel position from its stored fraction.
 *
 * <p><b>A fraction of the free travel, not of the plot</b> — so 0 is flush against one edge and 1
 * flush against the other, whatever size the box happens to be. Held that way because the box is
 * session state and the plot is not a fixed size: a resize, an expand or a zoom that changes the
 * axis gutters would otherwise push a box stored in pixels off the canvas, and a reading nobody can
 * find is worse than one in the wrong corner.</p>
 */
export function clampCursorBox(
  fraction: { x: number; y: number },
  box: { width: number; height: number },
  plot: CursorBoxRect,
): { left: number; top: number } {
  const travelX = Math.max(plot.width - box.width, 0)
  const travelY = Math.max(plot.height - box.height, 0)
  return {
    left: plot.left + clamp01(fraction.x) * travelX,
    top: plot.top + clamp01(fraction.y) * travelY,
  }
}

/**
 * The fraction a dragged box lands on — the exact inverse of <code>clampCursorBox</code>.
 *
 * <p>Exact inverses on purpose: the drag writes through this and the draw reads through that, so a
 * box that is not moved must not creep.</p>
 */
export function cursorBoxFraction(
  position: { left: number; top: number },
  box: { width: number; height: number },
  plot: CursorBoxRect,
): { x: number; y: number } {
  const travelX = Math.max(plot.width - box.width, 0)
  const travelY = Math.max(plot.height - box.height, 0)
  return {
    x: travelX > 0 ? clamp01((position.left - plot.left) / travelX) : 0,
    y: travelY > 0 ? clamp01((position.top - plot.top) / travelY) : 0,
  }
}

/**
 * Whether a double-headed arrow may be drawn between two positions.
 *
 * <p><b>Both ends inside the plot, or none of it.</b> A cursor off the window is already drawn
 * nowhere rather than clamped — a clamped line would claim a crossing it is not at — so an arrow to
 * where that line would have been points at nothing, and an arrow with one end pinned to the edge
 * reads as a measurement to the edge. The minimum length keeps a pair a few pixels apart from
 * drawing two heads on top of each other with a label between them.</p>
 */
export const MIN_CURSOR_ARROW_PX = 24

export function cursorArrowFits(
  a: number | null,
  b: number | null,
  lo: number,
  hi: number,
): boolean {
  if (a === null || b === null) return false
  if (!Number.isFinite(a) || !Number.isFinite(b)) return false
  if (a < lo || a > hi || b < lo || b > hi) return false
  return Math.abs(b - a) >= MIN_CURSOR_ARROW_PX
}
