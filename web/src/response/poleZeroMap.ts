import type { Viewport } from '../charts/planeTransform'
import type { FitResult, Singularity } from './rationalFit'

/**
 * The pole–zero map: the fitted model's roots in the s-plane, drawn in hertz (σ/2π along x,
 * jω/2π along y) so a pole at 2 kHz sits 2 kHz from the origin and the axis reads like the Bode
 * plot's. Pure, so it is testable; `ResponsePolarChart` only draws what this returns.
 *
 * This is a *different plane* from the Nyquist plot's. Nyquist draws G(jω) — the response — while
 * this draws where G's poles and zeros lie in s. They cannot share axes: a point on the Nyquist
 * contour is a complex gain at one frequency, a point here is a frequency (and a damping) and no
 * gain at all. So the roots are a projection of their own rather than markers on the contour.
 */

export interface RootPoint {
  kind: 'pole' | 'zero'
  /** σ/2π, Hz — negative in the left half-plane. */
  re: number
  /** ω/2π, Hz — one point per conjugate, so a pair yields ±. */
  im: number
  singularity: Singularity
}

/**
 * The s-plane roots a singularity stands for, in Hz. `Singularity` reports a conjugate pair once,
 * as (f, ζ): the pair is |s| = 2πf at −ζ|s| ± j|s|√(1−ζ²), and a real root is at −ζ|s| with
 * ζ = ±1 (its `zeta` carries the sign, so a right-half-plane root comes back on the right).
 */
export function rootPoints(singularity: Singularity, kind: 'pole' | 'zero'): RootPoint[] {
  const magnitudeHz = singularity.freqHz
  const sigmaHz = -singularity.zeta * magnitudeHz
  if (!singularity.isPair) return [{ kind, re: sigmaHz, im: 0, singularity }]

  const omegaHz = Math.sqrt(Math.max(0, magnitudeHz * magnitudeHz - sigmaHz * sigmaHz))
  return [
    { kind, re: sigmaHz, im: omegaHz, singularity },
    { kind, re: sigmaHz, im: -omegaHz, singularity },
  ]
}

/** Every root of a fit, poles and zeros, conjugates included. */
export function fitRootPoints(fit: FitResult): RootPoint[] {
  return [...fit.poles.flatMap((p) => rootPoints(p, 'pole')), ...fit.zeros.flatMap((z) => rootPoints(z, 'zero'))]
}

/**
 * The measured band a fit can speak for: the plotted curve's endpoints, or null when there is no
 * curve. The same band `ResponsePoleZeroPanel` flags its rows against, so the map's filter and the
 * table's "out of band" mark can never disagree about which roots those are.
 */
export function measuredBand(curve: readonly { freqHz: number }[]): { lowHz: number; highHz: number } | null {
  if (curve.length === 0) return null
  return { lowHz: curve[0]!.freqHz, highHz: curve[curve.length - 1]!.freqHz }
}

/**
 * A root's natural frequency lies inside the measured band. Judged on |s|/2π — the frequency the
 * table lists — not on the point's own coordinates, so a resonant pair is in or out as a pair
 * rather than one conjugate at a time.
 */
export function rootInBand(root: RootPoint, band: { lowHz: number; highHz: number } | null): boolean {
  if (!band) return true
  const f = root.singularity.freqHz
  return f >= band.lowHz && f <= band.highHz
}

/**
 * The roots to draw. Out-of-band roots are the fit's extrapolation — the data constrains the
 * response between the sweep's endpoints and says nothing beyond them — and on a wide sweep of a
 * narrow DUT they are also what wrecks the picture: the window is fitted to the roots, so one root
 * at 60 MHz (measured, a 24 kHz sweep of the 2 kHz Butterworth at order 1) squeezes every real one
 * into a pixel at the origin. Dropping them is a *display* choice and changes no fit: the model,
 * the residual and the table are all untouched, and the table goes on listing them with its own
 * flag, which is what keeps the option from hiding the fact that the fit needed them.
 */
export function visibleRoots(roots: readonly RootPoint[], band: { lowHz: number; highHz: number } | null, hideOutOfBand: boolean): RootPoint[] {
  return hideOutOfBand ? roots.filter((r) => rootInBand(r, band)) : [...roots]
}

/** The floor of the map's half-extent, Hz: with no roots at all there is still an origin to show. */
export const MIN_ROOT_EXTENT_HZ = 100

/**
 * The window the map opens on: square, centred on the origin, fitted to the roots with a margin.
 * Fitted to the roots and not to the measured band on purpose — a 24 kHz sweep of a 2 kHz filter
 * would otherwise put every root in a corner an eighth of the way out — so the band segment on the
 * jω axis is usually clipped, and that is what it is for: showing which part of the axis the data
 * spoke for.
 */
export function poleZeroViewport(roots: RootPoint[]): Viewport {
  let extent = MIN_ROOT_EXTENT_HZ
  for (const r of roots) extent = Math.max(extent, Math.abs(r.re), Math.abs(r.im))
  const e = extent * 1.25
  return { xMin: -e, xMax: e, yMin: -e, yMax: e }
}

/** Nearest root to a screen position, given a projector; null beyond `maxDistancePx`. */
export function nearestRoot(
  roots: RootPoint[],
  px: number,
  py: number,
  toScreen: (r: RootPoint) => { x: number; y: number },
  maxDistancePx = 30,
): RootPoint | null {
  let best: RootPoint | null = null
  let bestDistance = maxDistancePx * maxDistancePx
  for (const root of roots) {
    const s = toScreen(root)
    const d = (s.x - px) ** 2 + (s.y - py) ** 2
    if (d <= bestDistance) {
      bestDistance = d
      best = root
    }
  }
  return best
}
