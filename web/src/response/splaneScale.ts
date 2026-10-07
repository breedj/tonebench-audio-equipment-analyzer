import type { Viewport } from '../charts/planeTransform'
import { MIN_ROOT_EXTENT_HZ, type RootPoint } from './poleZeroMap'
import type { MeasuredBand } from './rationalFit'

/**
 * How the s-plane is laid out on screen, for the pole–zero map and the |H(s)| surface.
 *
 * The plane is drawn in hertz (σ/2π along x, jω/2π along y), which `poleZeroMap` established and
 * this module does not change. What it adds is a choice about *radius*.
 *
 * **Linear is the textbook plane and it fails on an amplifier.** The window is fitted to the roots,
 * so a DUT whose roots span decades — a 12 Hz coupling pole and a 46 kHz transformer pole is the
 * ordinary case, not a pathological one — puts the low root 0.02 % of the way out from the origin,
 * i.e. inside the origin marker. `poleZeroMap.visibleRoots` exists to rescue the map from the
 * extreme version of this by dropping out-of-band roots, but that cannot rescue a *surface*: the
 * field is continuous and the window is the whole picture, so there is nothing to hide.
 *
 * `LogModulus` scales |s| logarithmically and leaves arg(s) exactly alone:
 *
 *     ρ = log10(1 + r/refHz),  drawn at (ρ·x/r, ρ·y/r)
 *
 * Four properties make this the right warp rather than merely a working one, and all four are
 * pinned by tests:
 *
 * - **The angle is preserved exactly.** ζ and Q read as an angle from the origin, which is the
 *   affordance the manual's pole–zero page is built on. A warp that moved the angle would make the
 *   picture say something false about damping.
 * - **It is smooth through the origin.** ρ → r/(refHz·ln10) as r → 0, so there is no pole at the
 *   centre of the plane, no hole to special-case, and a root approaching DC does not run off to
 *   minus infinity the way a bare log10(r/ref) does.
 * - **It is exactly invertible**, which the surface needs: the raster evaluates H per *screen* cell,
 *   so every cell has to be mapped back to a real s.
 * - **Every axis stays an axis.** θ = ±90° maps to ±90°, so the jω axis is still the vertical line
 *   and the measured band is still a segment on it; θ = 180° stays the negative real axis. And
 *   circles r = const stay circles, which is what makes the surface's band arcs cheap.
 *
 * Linear remains the default. It is what a reader expects of an s-plane, and switching the scale
 * must never change a number — only where it is drawn.
 */

export type SPlaneScale = 'Linear' | 'LogModulus'

export interface SPlaneMap {
  scale: SPlaneScale
  /** Hz at which `LogModulus` turns over from roughly linear to logarithmic. Unused when `Linear`. */
  refHz: number
}

/** The smallest reference frequency, Hz: below this a decade of the picture is spent on nothing. */
export const MIN_REFERENCE_HZ = 0.1

/**
 * The reference frequency for a band: the largest power of ten at or below its low end.
 *
 * A *rounded decade* rather than `band.lowHz` itself, so the mapping does not shift under every
 * small change of the plotted window — a warp that moves as the user drags the From box would make
 * the roots appear to drift when nothing about them had changed. It still depends on the band, so
 * anything caching a warped position has to invalidate when the band can move.
 */
export function referenceHz(band: MeasuredBand | null): number {
  if (!band || !(band.lowHz > 0) || !Number.isFinite(band.lowHz)) return 1
  const decade = Math.pow(10, Math.floor(Math.log10(band.lowHz)))
  return Math.max(MIN_REFERENCE_HZ, decade)
}

export function sPlaneMap(scale: SPlaneScale, band: MeasuredBand | null): SPlaneMap {
  return { scale, refHz: referenceHz(band) }
}

/**
 * The warped radius of a radius in hertz. Monotone, zero at zero.
 *
 * `log1p`/`LN10` rather than `log10(1 + x)`, and `expm1` rather than `pow(10, ρ) − 1` in the
 * inverse: both forms are the same function in exact arithmetic and not in floating point. For a
 * root far below the reference — 1 Hz against a 10 Hz reference is `x = 0.1`, and a root near DC is
 * far smaller — `1 + x` rounds away the low bits of `x` before the logarithm ever sees them, and
 * the round trip then loses five or six digits. `log1p` is defined precisely to avoid that.
 */
export function mappedRadius(m: SPlaneMap, rHz: number): number {
  if (m.scale === 'Linear') return rHz
  return Math.log1p(rHz / m.refHz) / Math.LN10
}

/** The radius in hertz of a warped radius — the inverse of `mappedRadius`. */
export function plainRadius(m: SPlaneMap, rho: number): number {
  if (m.scale === 'Linear') return rho
  return m.refHz * Math.expm1(rho * Math.LN10)
}

/** Hz plane → drawing coordinates. */
export function toMapped(m: SPlaneMap, reHz: number, imHz: number): { x: number; y: number } {
  if (m.scale === 'Linear') return { x: reHz, y: imHz }
  const r = Math.hypot(reHz, imHz)
  // The origin has no direction to preserve, and r = 0 would divide by zero.
  if (r === 0) return { x: 0, y: 0 }
  const scale = mappedRadius(m, r) / r
  return { x: reHz * scale, y: imHz * scale }
}

/** Drawing coordinates → Hz plane. */
export function toHz(m: SPlaneMap, x: number, y: number): { reHz: number; imHz: number } {
  if (m.scale === 'Linear') return { reHz: x, imHz: y }
  const rho = Math.hypot(x, y)
  if (rho === 0) return { reHz: 0, imHz: 0 }
  const scale = plainRadius(m, rho) / rho
  return { reHz: x * scale, imHz: y * scale }
}

/**
 * The window the surface opens on: square, origin-centred, fitted to the roots **and to the top of
 * the measured band**.
 *
 * That is the one deliberate difference from `poleZeroViewport`, which fits the roots alone and lets
 * the band segment clip. On the surface the jω axis is the link back to the Bode plot — it is where
 * the model and the measurement are the same thing — so a picture with the measured band off-screen
 * has nothing to check itself against.
 */
export function surfaceViewport(m: SPlaneMap, roots: readonly RootPoint[], band: MeasuredBand | null): Viewport {
  let extent = mappedRadius(m, MIN_ROOT_EXTENT_HZ)
  for (const r of roots) {
    const p = toMapped(m, r.re, r.im)
    extent = Math.max(extent, Math.abs(p.x), Math.abs(p.y))
  }
  if (band && band.highHz > 0 && Number.isFinite(band.highHz)) {
    extent = Math.max(extent, mappedRadius(m, band.highHz))
  }
  const e = extent * 1.25
  return { xMin: -e, xMax: e, yMin: -e, yMax: e }
}

/** At most this many ticks per axis direction, so a log axis over six decades stays readable. */
const MAX_TICKS_PER_SIDE = 12

/**
 * The hertz values to label on either axis, inside a window of half-width `mappedExtent`.
 *
 * `niceTicks` is the right answer under `Linear` and the wrong one under `LogModulus`, where equal
 * screen steps are not equal frequency steps — so this returns *frequencies* (positive only; the
 * caller mirrors them), drawn wherever `mappedRadius` puts them. The 1/2/5 family matches
 * `niceTicks`' own so the two scales read alike.
 */
export function axisTickHz(m: SPlaneMap, mappedExtent: number): number[] {
  if (!(mappedExtent > 0) || !Number.isFinite(mappedExtent)) return []
  const maxHz = plainRadius(m, mappedExtent)
  if (!(maxHz > 0) || !Number.isFinite(maxHz)) return []

  const out: number[] = []
  const startDecade = m.scale === 'LogModulus' ? Math.floor(Math.log10(m.refHz)) : Math.floor(Math.log10(maxHz)) - 2
  for (let decade = startDecade; out.length < MAX_TICKS_PER_SIDE; decade++) {
    const base = Math.pow(10, decade)
    if (base > maxHz) break
    for (const multiple of [1, 2, 5]) {
      const hz = multiple * base
      if (hz <= maxHz && out.length < MAX_TICKS_PER_SIDE) out.push(Number(hz.toPrecision(12)))
    }
  }
  return out
}

/**
 * A pole–zero map window as the panel's four boxes hold it: hertz on both axes, σ/2π along x and
 * jω/2π up y, signed — a stable pole has a negative σ, and both halves of the jω axis are drawn.
 */
export interface PoleZeroWindowHz {
  xMinHz: number
  xMaxHz: number
  yMinHz: number
  yMaxHz: number
}

/** The narrowest window either axis will be drawn with, in hertz. */
export const MIN_WINDOW_SPAN_HZ = 1e-3

/**
 * A radius in hertz through the warp, keeping its sign.
 *
 * `mappedRadius` takes a radius, which is unsigned by definition; an axis bound is a *coordinate* and
 * the plane is drawn with the origin inside it, so each bound is warped by its own magnitude and the
 * sign put back. That is exactly the restriction of `toMapped` to each axis (θ = 0, ±90°, 180° all
 * map to themselves — see this module's remarks), so a window's edges land where a root on that axis
 * lands, which is the only property this has to have.
 */
export function signedMappedRadius(m: SPlaneMap, hz: number): number {
  if (!Number.isFinite(hz)) return 0
  return Math.sign(hz) * mappedRadius(m, Math.abs(hz))
}

/** The inverse of `signedMappedRadius`. */
export function signedPlainRadius(m: SPlaneMap, coordinate: number): number {
  if (!Number.isFinite(coordinate)) return 0
  return Math.sign(coordinate) * plainRadius(m, Math.abs(coordinate))
}

/**
 * A hand-set window in hertz → the drawing viewport.
 *
 * Each edge is warped on its own, so the numbers in the boxes are the numbers on the axis under
 * either radius scale. The bounds are ordered and separated here rather than trusted: two fields a
 * user types into arrive in either order and can arrive equal, and a degenerate viewport divides by
 * zero in `fitTransform` and paints nothing at all.
 *
 * **Note the aspect.** The map is drawn with equal aspect while the window is fitted, because ζ and Q
 * read as an angle from the origin there. A window asked for by hand is honoured exactly instead —
 * see `ResponsePolarChart.equalAspect` — so an x range and a y range of different spans tilt those
 * angles. That is the trade for controls that mean what they say, and the panel's tooltip says so.
 */
export function mappedWindow(m: SPlaneMap, window: PoleZeroWindowHz): Viewport {
  const x = orderedSpan(window.xMinHz, window.xMaxHz)
  const y = orderedSpan(window.yMinHz, window.yMaxHz)
  return {
    xMin: signedMappedRadius(m, x.lo),
    xMax: signedMappedRadius(m, x.hi),
    yMin: signedMappedRadius(m, y.lo),
    yMax: signedMappedRadius(m, y.hi),
  }
}

/** A drawing viewport back to hertz — what seeds the four boxes from the fitted window. */
export function windowHz(m: SPlaneMap, viewport: Viewport): PoleZeroWindowHz {
  return {
    xMinHz: signedPlainRadius(m, viewport.xMin),
    xMaxHz: signedPlainRadius(m, viewport.xMax),
    yMinHz: signedPlainRadius(m, viewport.yMin),
    yMaxHz: signedPlainRadius(m, viewport.yMax),
  }
}

function orderedSpan(a: number, b: number): { lo: number; hi: number } {
  const lo = Math.min(Number.isFinite(a) ? a : 0, Number.isFinite(b) ? b : 0)
  const hi = Math.max(Number.isFinite(a) ? a : 0, Number.isFinite(b) ? b : 0)
  if (hi - lo >= MIN_WINDOW_SPAN_HZ) return { lo, hi }

  const centre = (lo + hi) / 2
  return { lo: centre - MIN_WINDOW_SPAN_HZ / 2, hi: centre + MIN_WINDOW_SPAN_HZ / 2 }
}
