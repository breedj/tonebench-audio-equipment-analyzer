/**
 * The zoom bar's arithmetic — where a time sits on the overview strip, what a press on it grabs,
 * and what a drag of that grab does to the window.
 *
 * <p>A module rather than part of the component for the usual reason: nothing in a <code>.vue</code>
 * is testable under the node environment vitest runs in, and every rule here is the kind that is
 * quietly wrong rather than obviously broken — a box that cannot be made narrower than a sample, an
 * edge dragged past the other one, a window nudged off the end of the record.</p>
 */

import type { ScopeTimeWindow } from './timeAxis'

/** The strip's plot area inside its canvas, in CSS pixels. */
export interface StripGeometry {
  left: number
  width: number
}

/**
 * How close to an edge a press has to be to grab it rather than the body, in CSS pixels.
 *
 * <p>Generous, and deliberately wider than the edge is drawn: a two-pixel target is one nobody can
 * hit, and the cost of grabbing an edge when the body was meant is one undo-able drag.</p>
 */
export const STRIP_HANDLE_PX = 7

/**
 * The narrowest box a drag may leave, in pixels.
 *
 * <p>In pixels rather than seconds because it is about the <em>grip</em>: a box narrower than this
 * has no body left to drag, so the only way out of it would be to click elsewhere on the strip. The
 * window may still be made narrower than this by typing or by a plot drag-zoom — this bounds the
 * gesture, not the setting.</p>
 */
export const MIN_STRIP_BOX_PX = 6

/** Where a time relative to the trigger sits on the strip. */
export function stripX(seconds: number, bounds: ScopeTimeWindow, geo: StripGeometry): number {
  const span = bounds.toS - bounds.fromS
  if (!(span > 0) || !Number.isFinite(seconds)) return geo.left
  return geo.left + ((seconds - bounds.fromS) / span) * geo.width
}

/** And back: the time a pixel on the strip stands for. */
export function stripSeconds(x: number, bounds: ScopeTimeWindow, geo: StripGeometry): number {
  const span = bounds.toS - bounds.fromS
  if (!(span > 0) || geo.width <= 0) return bounds.fromS
  return bounds.fromS + ((x - geo.left) / geo.width) * span
}

/** The box a window draws as, in pixels, or null when the strip has no span to draw it on. */
export function stripBox(
  window: ScopeTimeWindow,
  bounds: ScopeTimeWindow,
  geo: StripGeometry,
): { fromX: number; toX: number } | null {
  if (!(bounds.toS > bounds.fromS) || geo.width <= 0) return null
  // Clamped to the strip rather than hidden: a window reaching past the record — which the display
  // panel's boxes allow, since they are clamped to the record and a drag-zoom is not — still has to
  // show which part of it is on the record.
  const fromX = Math.max(geo.left, Math.min(geo.left + geo.width, stripX(window.fromS, bounds, geo)))
  const toX = Math.max(geo.left, Math.min(geo.left + geo.width, stripX(window.toS, bounds, geo)))
  return { fromX: Math.min(fromX, toX), toX: Math.max(fromX, toX) }
}

/** What a press at a pixel takes hold of. */
export type StripGrab = 'from' | 'to' | 'body' | 'outside'

/**
 * Which part of the box a press grabs.
 *
 * <p><b>Edges first, and the nearer edge wins</b> — on a box narrower than two handles both tests
 * pass, and silently preferring one end would make a narrow box resizable from one side only.</p>
 */
export function stripGrabAt(
  x: number,
  box: { fromX: number; toX: number } | null,
  handlePx = STRIP_HANDLE_PX,
): StripGrab {
  if (!box) return 'outside'

  const dFrom = Math.abs(x - box.fromX)
  const dTo = Math.abs(x - box.toX)
  if (dFrom <= handlePx || dTo <= handlePx) return dFrom <= dTo ? 'from' : 'to'
  if (x > box.fromX && x < box.toX) return 'body'
  return 'outside'
}

/**
 * The window a drag leaves, in seconds.
 *
 * <p>Three behaviours, and each is what the corresponding grip means on a bench instrument. The
 * <b>body</b> slides the window whole, keeping its span, and stops at either end of the record
 * rather than shortening against it — a window that silently narrowed as it was pushed off the end
 * would be a timebase changed by a gesture that never asked to change one. An <b>edge</b> moves
 * alone, bounded by the other edge less the minimum, so a drag through the far edge stops rather
 * than turning the box inside out.</p>
 *
 * @param minSpanS the narrowest window a gesture may leave — the caller's, since only it knows the rate
 */
export function stripDrag(
  grab: StripGrab,
  start: ScopeTimeWindow,
  deltaS: number,
  bounds: ScopeTimeWindow,
  minSpanS: number,
): ScopeTimeWindow {
  if (!Number.isFinite(deltaS) || grab === 'outside') return start

  const min = Math.max(minSpanS, 0)

  if (grab === 'body') {
    const span = start.toS - start.fromS
    // Clamped as a pair: the span is what a body drag keeps.
    let fromS = start.fromS + deltaS
    if (fromS < bounds.fromS) fromS = bounds.fromS
    if (fromS + span > bounds.toS) fromS = bounds.toS - span
    return { fromS, toS: fromS + span }
  }

  if (grab === 'from') {
    const fromS = Math.min(Math.max(start.fromS + deltaS, bounds.fromS), start.toS - min)
    return { fromS, toS: start.toS }
  }

  const toS = Math.max(Math.min(start.toS + deltaS, bounds.toS), start.fromS + min)
  return { fromS: start.fromS, toS }
}

/**
 * The window a click on the bare strip asks for: the same span, centred where the pointer is.
 *
 * <p>What every bench scope does with a click on its zoom bar, and the one gesture that moves a
 * window a long way in one go. Clamped to the record like a body drag, and for the same reason.</p>
 */
export function stripCentreOn(
  seconds: number,
  span: number,
  bounds: ScopeTimeWindow,
): ScopeTimeWindow {
  const width = Math.min(Math.max(span, 0), bounds.toS - bounds.fromS)
  let fromS = seconds - width / 2
  if (fromS < bounds.fromS) fromS = bounds.fromS
  if (fromS + width > bounds.toS) fromS = bounds.toS - width
  return { fromS, toS: fromS + width }
}

/** True when the window is the whole record — the state the strip's box fills it, and a reset lands on. */
export function stripCoversAll(window: ScopeTimeWindow, bounds: ScopeTimeWindow): boolean {
  return window.fromS <= bounds.fromS && window.toS >= bounds.toS
}
