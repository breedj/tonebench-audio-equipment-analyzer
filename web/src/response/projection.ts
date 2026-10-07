import type { ComplexPoint } from './complexPlane'
import { dbToRatio } from './complexPlane'

/** How a Nyquist plot maps magnitude onto radius. */
export type NyquistRadiusMode = 'Linear' | 'Db'

export interface PlanePoint {
  x: number
  y: number
  /** The dB radius map ran into its floor here, so this point's distance from the origin is a lower bound. */
  clamped: boolean
}

/**
 * The smallest radius the dB map will emit.
 *
 * Not zero, deliberately. Collapsing everything below the floor onto the origin changes how many
 * times the contour winds around −1, and the encirclement count is the one thing a Nyquist plot
 * exists to show — a compression that alters it is not a compression, it is a different plot.
 */
export const MIN_DB_RADIUS = 0.02

/**
 * The smallest linear window a Nyquist plot will open on, ±2 about the origin.
 *
 * A *floor*, not a fixed window. It was fixed at first, on the reasoning that the interesting part
 * of a loop-gain Nyquist plot is always the neighbourhood of −1 and that fitting to a +60 dB
 * low-frequency gain would put the unit circle inside one pixel. That reasoning is sound and the
 * conclusion was still wrong: the *ordinary* case is an amplifier measured input-to-output, where
 * |G| is simply its gain — 21.8 for a 26.8 dB stage — so a ±2 window clipped the whole contour away
 * and drew an empty pair of axes. Showing nothing is strictly worse than showing a small −1.
 *
 * So the window fits the data and never shrinks below this, which keeps the unit circle and the −1
 * marker on screen for a curve that lives near them. Zooming onto crossover is a drag-box away, and
 * the dB radius exists for the genuinely wide-range case.
 */
export const MIN_LINEAR_EXTENT = 2

/**
 * The half-width of a square window about the origin containing every point, never below
 * `minExtent`.
 *
 * Symmetric because a Nyquist plot is read against the origin and the unit circle; an asymmetric fit
 * would push the origin off-centre for no gain. Takes plane points rather than complex ones so it
 * measures whatever the radius map actually produced — under the dB map the extent stays small even
 * when |G| is enormous, which is the whole point of that map.
 */
export function symmetricExtent(points: PlanePoint[], minExtent: number): number {
  let extent = minExtent
  for (const point of points) {
    if (!Number.isFinite(point.x) || !Number.isFinite(point.y)) continue
    extent = Math.max(extent, Math.abs(point.x), Math.abs(point.y))
  }
  // A little air so the outermost point is not drawn onto the frame.
  return extent > minExtent ? extent * 1.05 : extent
}

/**
 * Magnitude to radius.
 *
 * `Linear` is the textbook plot: `r = |G|`, so distances mean what they look like and the vector
 * margin can be read off the picture. `Db` maps the floor to the origin and 0 dB to the unit circle,
 * keeping 60 dB of range on screen at the cost of the geometry — it is monotone, so encirclements
 * and crossings survive, but distance to −1 does not, which is why any distance-based readout is
 * suppressed under it.
 */
export function nyquistRadius(magDb: number, mode: NyquistRadiusMode, floorDb: number): { r: number; clamped: boolean } {
  if (mode === 'Linear') return { r: dbToRatio(magDb), clamped: false }

  // A floor at or above 0 dB has no room to map anything into; fall back rather than divide by a
  // non-positive span and emit an inverted scale.
  const span = -floorDb
  if (!(span > 0)) return { r: dbToRatio(magDb), clamped: false }

  const r = (magDb - floorDb) / span
  return r <= MIN_DB_RADIUS ? { r: MIN_DB_RADIUS, clamped: true } : { r, clamped: false }
}

/** A measured point on the complex plane, under whichever radius map is in force. */
export function nyquistPoint(point: ComplexPoint, mode: NyquistRadiusMode, floorDb: number): PlanePoint {
  const { r, clamped } = nyquistRadius(point.magDb, mode, floorDb)
  const rad = (point.phaseDeg * Math.PI) / 180
  return { x: r * Math.cos(rad), y: r * Math.sin(rad), clamped }
}

/** A measured point on a Nichols chart: phase along x, gain in dB up y. */
export function nicholsPoint(point: ComplexPoint): PlanePoint {
  return { x: point.phaseDeg, y: point.magDb, clamped: false }
}

/**
 * Every critical phase inside a visible x window — `−180° + 360k`, not just `−180°`.
 *
 * The phase axis carries *unwrapped* degrees, so a DUT with three poles is already past −270° and
 * one with real excess phase runs to −900°. A single marker at −180 would sit outside the window
 * the curve actually occupies, which reads as "this loop never approaches the critical point".
 */
export function criticalPhases(xMinDeg: number, xMaxDeg: number): number[] {
  const out: number[] = []
  const first = Math.ceil((xMinDeg + 180) / 360)
  const last = Math.floor((xMaxDeg + 180) / 360)
  // A window spanning hundreds of turns (the DSP processor with its 12 ms delay restored is 240
  // by 20 kHz) would draw a marker every few pixels and read as a solid band; past this many the
  // markers say nothing a reader can use, so none are drawn and the legend drops the entry.
  if (last - first + 1 > MAX_CRITICAL_MARKERS) return out
  for (let k = first; k <= last; k++) out.push(-180 + 360 * k)
  return out
}

/** Above this many −180° turns in view, `criticalPhases` draws none rather than a band. */
export const MAX_CRITICAL_MARKERS = 24

/** The Nichols gain axis' window when it is set by hand rather than fitted — see `nicholsViewport`. */
export interface NicholsGainWindow {
  topDb: number
  bottomDb: number
}

/** The narrowest gain window the Nichols chart will draw, so a reversed or equal pair still plots. */
export const NICHOLS_GAIN_MIN_SPAN_DB = 2

/**
 * The window a Nichols chart opens on: phase along x, gain in dB up y.
 *
 * The phase axis is always fitted. It carries *unwrapped* degrees, so its span is a property of the
 * DUT rather than a preference — a three-pole loop is already past −270° and the DSP processor with
 * its delay restored runs to tens of thousands — and a hand-set phase window would be wrong again on
 * the next DUT. The plotted frequency range is the control that narrows it.
 *
 * The gain axis is fitted too when `gain` is null, and otherwise exactly what `gain` says. Fitted is
 * the default and stays it: both axes are already logarithmic in the physical quantity, so a fit
 * needs no floor of its own the way `MIN_LINEAR_EXTENT` does on Nyquist. A hand-set window is for
 * reading crossover — ±6 dB about 0 is where the gain margin is — which a fit over a DUT with 60 dB
 * of range cannot show.
 *
 * Note the critical point is forced into view in the fitted case and deliberately **not** in the
 * hand-set one: a window asked for is a window drawn, and growing it to reach −180° would be the
 * "the dropdowns appear inert" failure the chart docs describe.
 */
export function nicholsViewport(
  points: readonly { phaseDeg: number; magDb: number }[],
  gain: NicholsGainWindow | null,
): { xMin: number; xMax: number; yMin: number; yMax: number } {
  const y = gain ? orderedGainWindow(gain) : null

  if (points.length === 0) {
    return { xMin: -360, xMax: 0, yMin: y?.bottomDb ?? -40, yMax: y?.topDb ?? 20 }
  }

  let xMin = Infinity
  let xMax = -Infinity
  let yMin = Infinity
  let yMax = -Infinity
  for (const p of points) {
    if (!Number.isFinite(p.phaseDeg) || !Number.isFinite(p.magDb)) continue
    xMin = Math.min(xMin, p.phaseDeg)
    xMax = Math.max(xMax, p.phaseDeg)
    yMin = Math.min(yMin, p.magDb)
    yMax = Math.max(yMax, p.magDb)
  }
  if (!Number.isFinite(xMin)) return { xMin: -360, xMax: 0, yMin: y?.bottomDb ?? -40, yMax: y?.topDb ?? 20 }

  // The critical point must always be in view — a Nichols chart that fitted tightly to a
  // comfortable loop would crop the one marker the reader is looking for.
  xMin = Math.min(xMin, -190)
  xMax = Math.max(xMax, -170)
  if (!y) {
    yMin = Math.min(yMin, -6)
    yMax = Math.max(yMax, 6)
  }

  const padX = Math.max(15, (xMax - xMin) * 0.05)
  if (y) return { xMin: xMin - padX, xMax: xMax + padX, yMin: y.bottomDb, yMax: y.topDb }

  const padY = Math.max(3, (yMax - yMin) * 0.08)
  return { xMin: xMin - padX, xMax: xMax + padX, yMin: yMin - padY, yMax: yMax + padY }
}

/**
 * A hand-set gain window as the chart can use it: the larger value on top, and never narrower than
 * `NICHOLS_GAIN_MIN_SPAN_DB`.
 *
 * Two fields a user types into can arrive in either order and can arrive equal — the panel clamps
 * them, but a stored pair from an older build or a report rendering a type does not pass through the
 * panel at all, and a zero-height viewport divides by zero in `fitTransform` and paints nothing.
 */
export function orderedGainWindow(gain: NicholsGainWindow): NicholsGainWindow {
  const top = Number.isFinite(gain.topDb) ? gain.topDb : 20
  const bottom = Number.isFinite(gain.bottomDb) ? gain.bottomDb : -40
  const hi = Math.max(top, bottom)
  const lo = Math.min(top, bottom)
  if (hi - lo >= NICHOLS_GAIN_MIN_SPAN_DB) return { topDb: hi, bottomDb: lo }

  const centre = (hi + lo) / 2
  return { topDb: centre + NICHOLS_GAIN_MIN_SPAN_DB / 2, bottomDb: centre - NICHOLS_GAIN_MIN_SPAN_DB / 2 }
}
