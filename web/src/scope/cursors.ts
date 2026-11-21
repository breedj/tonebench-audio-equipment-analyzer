/**
 * The oscilloscope's cursors — two draggable pairs, one in time and one in volts, reading Δt, 1/Δt
 * and ΔV.
 *
 * <p><b>A cursor is a transient reading that belongs to the session, not a note that belongs to the
 * measurement.</b> That is the whole difference from an annotation, and it decides everything else
 * here: the positions are not in <code>params_json</code>, they are not restored by
 * <code>scopeDisplayFromParams</code>, and they are not in a report. A report is a record of what
 * was measured; where somebody happened to leave a cursor while reading it is not.</p>
 *
 * <p>The arithmetic is here rather than in the chart for the usual reason — nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in — and because the two
 * rules that matter are exactly the kind that look obvious and are not: when a pair has to be
 * <em>placed</em>, and when it names a gate.</p>
 */

import type { SegmentedOption } from '../panels/segmented'
import { formatSeconds, type ScopeReading } from './readings'
import { formatScopeTime } from './timeAxis'
import { formatVolts } from './voltAxis'

/**
 * Whether the level pair is placed by hand or follows the trace.
 *
 * <p><b>Track is the Siglent's own mode and the one worth having</b>: the two time cursors are the
 * only things dragged, and the level cursors go wherever the waveform is at those times — so ΔY is
 * what the signal did between them and ΔY/ΔX is its average slope over that stretch, both without
 * anybody having to place a horizontal line on a curve by eye. Manual keeps the two pairs
 * independent, which is what you want for measuring against a level you already know (a rail, a
 * threshold, a specified limit) rather than against the trace.</p>
 */
export type ScopeCursorMode = 'Manual' | 'Track'

export const SCOPE_CURSOR_MODES: readonly SegmentedOption<ScopeCursorMode>[] = Object.freeze([
  {
    value: 'Manual' as const,
    label: 'Manual',
    title:
      'Both pairs are placed by hand. For measuring against a level you already know — a rail, a threshold, a limit — where the point is that the line does not move with the trace.',
  },
  {
    value: 'Track' as const,
    label: 'Track',
    title:
      'The level cursors follow the trace at the two time cursors, with a square drawn where each one sits. ΔY is then what the signal did between them and ΔY/Δt is its average slope, with nothing to line up by eye.',
  },
])

/** Whether a stored value is one of the two, so an unknown one falls back rather than renders blank. */
export function isScopeCursorMode(value: unknown): value is ScopeCursorMode {
  return value === 'Manual' || value === 'Track'
}

/** The staged cursors, one object so the chart and the panel can watch them as a unit. */
export interface ScopeCursors {
  /** Whether the time pair is shown. Off is the default: a scope opens with a clean screen. */
  timeOn: boolean
  /** The time cursors, in seconds from the trigger. Either may be the earlier one. */
  timeAS: number
  timeBS: number
  /** Whether the level pair's own horizontal lines are drawn — the Siglent's <em>Show CursorY</em>. */
  voltOn: boolean
  /** The level cursors, in volts. Either may be the higher one. */
  voltAV: number
  voltBV: number
  /** Placed by hand, or following the trace. See <code>ScopeCursorMode</code>. */
  mode: ScopeCursorMode
  /**
   * Which trace the level pair follows under Track — a source key, as the readings table uses
   * (<code>ch0</code>, <code>ch1</code>, <code>math1</code>, <code>math2</code>).
   *
   * <p>A choice rather than "whatever is drawn first", because with two traces on screen the
   * reading is meaningless until it says which one it is of — and the one being measured is
   * routinely not the one at the top of the list.</p>
   */
  trackSource: string
  /**
   * Whether the B time box is typed as a span from A rather than as a position.
   *
   * <p>The Siglent's <code>X2-X1</code>, and the reason it earns a control: a cursor pair is placed
   * either to mark two <em>places</em> or to mark one <em>interval</em>, and in the second case the
   * figure the reader has in mind is the interval. Typing 400 µs beats working out what 400 µs
   * after −65 µs comes to. Only the box changes — <code>timeBS</code> is always an absolute
   * position, so nothing downstream has to know which way it was typed.</p>
   */
  timeBIsSpan: boolean
}

/**
 * What the cursors start at: both pairs off, and both at zero.
 *
 * <p>Zero rather than a position inside the first record, because there is no record yet — and a
 * pair at zero is exactly the state <code>cursorPairNeedsPlacing</code> answers true for, so
 * switching one on places it in the window on screen rather than stacking both cursors on the
 * trigger where neither can be found to drag.</p>
 */
export const DEFAULT_SCOPE_CURSORS: Readonly<ScopeCursors> = Object.freeze({
  timeOn: false,
  timeAS: 0,
  timeBS: 0,
  voltOn: false,
  voltAV: 0,
  voltBV: 0,
  mode: 'Manual' as ScopeCursorMode,
  trackSource: 'ch0',
  timeBIsSpan: false,
})

/** Where a pair goes when it has to be placed: a quarter and three quarters across the window. */
export function placeCursorPair(min: number, max: number): { a: number; b: number } {
  if (!Number.isFinite(min) || !Number.isFinite(max) || max === min) return { a: min, b: max }
  const lo = Math.min(min, max)
  const span = Math.abs(max - min)
  return { a: lo + span * 0.25, b: lo + span * 0.75 }
}

/**
 * Whether a pair has to be placed before it is shown.
 *
 * <p>Two cases, and the second is the one that bites after a measurement switch: a pair with no span
 * (both cursors on the same value, which is what the defaults are) has nothing to read and nothing
 * to grab, and a pair left over from a 20 ms record is off the screen entirely on a 1 s one. Either
 * way the honest answer is to put them where they can be seen, since a cursor is session state and
 * nobody chose these numbers for <em>this</em> window.</p>
 */
export function cursorPairNeedsPlacing(a: number, b: number, min: number, max: number): boolean {
  if (!Number.isFinite(a) || !Number.isFinite(b)) return true
  if (a === b) return true
  const lo = Math.min(min, max)
  const hi = Math.max(min, max)
  return a < lo || a > hi || b < lo || b > hi
}

/**
 * The level of a drawn trace at one time, by straight interpolation between the two samples either
 * side — what Track puts its level cursor at.
 *
 * <p><b>Of the drawn line, not of the record</b>, and that is the decision: where the slice is
 * decimated the chart plots each column's maximum, so this follows that same array and the square
 * lands <em>on the line the reader can see</em>. Reading the record instead would put the marker
 * off the trace wherever a column covers more than one sample, which is exactly the case where a
 * reader most wants to know what the picture is claiming.</p>
 *
 * <p>Null outside the trace's own span and wherever either neighbour is null — a gap in a trace is
 * a stretch the analyzer did not measure, and interpolating across one would invent a level. Null
 * is what the readout prints as a dash.</p>
 */
export function trackLevelAt(
  xs: readonly number[],
  ys: readonly (number | null | undefined)[],
  x: number,
): number | null {
  if (!Number.isFinite(x) || xs.length === 0) return null
  const last = Math.min(xs.length, ys.length) - 1
  if (last < 0 || x < xs[0] || x > xs[last]) return null

  // Binary search for the sample at or before x. The x array is ascending by construction -- it is
  // the chart's own time axis -- so this needs no scan.
  let lo = 0
  let hi = last
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1
    if (xs[mid] <= x) lo = mid
    else hi = mid - 1
  }

  const here = ys[lo]
  if (here === null || here === undefined || !Number.isFinite(here)) return null
  if (lo === last || xs[lo] === x) return here
  const next = ys[lo + 1]
  if (next === null || next === undefined || !Number.isFinite(next)) return here
  const span = xs[lo + 1] - xs[lo]
  if (!(span > 0)) return here
  return here + ((next - here) * (x - xs[lo])) / span
}

/** What the B time box shows: a position, or the span from A. */
export function cursorTimeBBox(cursors: ScopeCursors): number {
  return cursors.timeBIsSpan ? cursors.timeBS - cursors.timeAS : cursors.timeBS
}

/** And back: what a figure typed into that box means as an absolute position. */
export function cursorTimeBFromBox(cursors: ScopeCursors, typed: number): number {
  return cursors.timeBIsSpan ? cursors.timeAS + typed : typed
}

/**
 * Whether the level pair is following the trace.
 *
 * <p>Both halves: Track with no time pair has nothing to follow, so it is not tracking — which is
 * why this is a function rather than a comparison written out at six call sites.</p>
 */
export function cursorsTrack(cursors: ScopeCursors): boolean {
  return cursors.mode === 'Track' && cursors.timeOn
}

/**
 * Whether the level figures are worth printing at all.
 *
 * <p><code>voltOn</code> is what draws the two horizontal <em>lines</em> — the Siglent's
 * <em>Show CursorY</em> — and under Track the levels are a reading whether or not those lines are
 * on: the squares on the trace are already saying where they are.</p>
 */
export function cursorLevelsRead(cursors: ScopeCursors): boolean {
  return cursors.voltOn || cursorsTrack(cursors)
}

/** A window, ordered, the way the record fetch wants it. */
export interface CursorGate {
  fromS: number
  toS: number
}

/**
 * The window the measurements are gated to, or null when there is none.
 *
 * <p>Only the <b>time</b> pair gates: a measurement is taken over a stretch of the record, and a
 * pair of levels names no stretch of anything. The pair is ordered here rather than at the call
 * site, so B dragged left of A reads the same as A dragged left of B — a cursor pair has no
 * direction, only two edges.</p>
 */
export function cursorGate(cursors: ScopeCursors): CursorGate | null {
  if (!cursors.timeOn) return null
  const { timeAS: a, timeBS: b } = cursors
  if (!Number.isFinite(a) || !Number.isFinite(b) || a === b) return null
  return { fromS: Math.min(a, b), toS: Math.max(a, b) }
}

/**
 * What the pairs read, in the units the axes they sit on are in.
 *
 * <p><b>1/Δt is given as a frequency and is not the same claim as the measured period.</b> It is
 * what the reader placed the cursors on, so it belongs beside Δt and not in the Measurements panel:
 * two cursors a cycle apart give the frequency of whatever the reader thinks a cycle is, where
 * <code>ScopeSliceMeasurements.FrequencyHz</code> is measured between crossings of the settled
 * middle. They should agree, and when they do not, that disagreement is the useful reading.</p>
 */
export function cursorReadings(cursors: ScopeCursors, voltsPerDivision: number, spanMs: number): ScopeReading[] {
  const rows: ScopeReading[] = []
  const t = (seconds: number) => formatScopeTime(seconds * 1000, spanMs)

  if (cursors.timeOn && Number.isFinite(cursors.timeAS) && Number.isFinite(cursors.timeBS)) {
    const dt = Math.abs(cursors.timeBS - cursors.timeAS)
    rows.push({ label: 'Time A', value: t(cursors.timeAS), title: 'Where the first time cursor sits, measured from the trigger.' })
    rows.push({ label: 'Time B', value: t(cursors.timeBS), title: 'Where the second time cursor sits.' })
    // The positions are written in the axis's own unit, so the pair can be compared against the
    // ticks and against each other; Δt is written in whatever unit *it* is, because it is the
    // reading rather than a position -- 5 µs between two cursors on a 10 ms window is a figure in
    // microseconds however the axis is labelled.
    rows.push({ label: 'Δt', value: formatSeconds(dt), title: 'The time between the two cursors.' })
    if (dt > 0) {
      rows.push({
        label: '1/Δt',
        value: formatCursorFrequency(1 / dt),
        title:
          'The reciprocal of Δt — the frequency of whatever the cursors have been put a cycle apart on. The Measurements panel’s own frequency is measured between crossings of the settled middle instead, so the two agreeing is a check and the two disagreeing is a reading.',
      })
    }
  }

  if (cursorLevelsRead(cursors) && Number.isFinite(cursors.voltAV) && Number.isFinite(cursors.voltBV)) {
    const v = (volts: number) => formatVolts(volts, voltsPerDivision)
    const tracked = cursorsTrack(cursors)
    const where = tracked
      ? 'Where the trace is at the first time cursor.'
      : 'Where the first level cursor sits.'
    rows.push({ label: 'Level A', value: v(cursors.voltAV), title: where })
    rows.push({
      label: 'Level B',
      value: v(cursors.voltBV),
      title: tracked ? 'Where the trace is at the second time cursor.' : 'Where the second level cursor sits.',
    })
    rows.push({
      label: 'ΔV',
      value: v(Math.abs(cursors.voltBV - cursors.voltAV)),
      title: 'The level difference between the two cursors.',
    })
  }

  return rows
}

/**
 * A frequency as the readout writes it. Its own formatter rather than the readings panel's, because
 * 1/Δt spans everything from a fraction of a hertz on a one-second record to the better part of a
 * hundred kilohertz between two adjacent samples.
 */
export function formatCursorFrequency(hz: number): string {
  if (!Number.isFinite(hz)) return '—'
  if (hz >= 1_000_000) return `${(hz / 1_000_000).toFixed(3)} MHz`
  if (hz >= 1_000) return `${(hz / 1000).toFixed(hz >= 100_000 ? 2 : 3)} kHz`
  if (hz >= 1) return `${hz.toFixed(hz >= 100 ? 2 : 3)} Hz`
  return `${(hz * 1000).toFixed(2)} mHz`
}
