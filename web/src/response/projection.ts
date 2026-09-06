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
  for (let k = first; k <= last; k++) out.push(-180 + 360 * k)
  return out
}
