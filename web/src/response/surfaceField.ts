import type { Viewport } from '../charts/planeTransform'
import { evaluateModel, type MeasuredBand, type RationalModel } from './rationalFit'
import { toHz, type SPlaneMap } from './splaneScale'

/**
 * |H(s)| over the complex s-plane, from a fitted rational model — the field the |H(s)| surface draws.
 *
 * **This is the model speaking, not the instrument.** A measurement only ever gives H on the jω
 * axis; there is no such thing as a measured value at σ ≠ 0. What makes the picture legitimate
 * rather than invented is that it is a rendering of the *same* rational model the pole–zero table
 * reports, and that its jω axis slice is exactly the Bode magnitude — which is the one place model
 * and measurement coincide, and therefore the check the tests here are built around.
 *
 * Nothing in this module knows about canvases: it returns levels on a grid and contour segments in
 * grid coordinates, so all of it is testable under vitest's node environment.
 */

export interface SurfaceField {
  cols: number
  rows: number
  /**
   * 20·log10|H|, row-major, **row 0 is the top** (largest ω) to match screen order rather than
   * complex-plane order. `+Infinity` at a pole.
   */
  levelDb: Float32Array
  /** The mapped-coordinate window the cells sample, cell centres inset by half a cell. */
  viewport: Viewport
}

/**
 * |P(s)|² for a real-coefficient polynomial at a complex argument, by a Horner that allocates nothing.
 *
 * **Deliberately a duplicate of `polyEval`, and worth 1.9× — which is less than it looks like it
 * should be.** `polyEval` returns a fresh `Cx` per step, so the obvious expectation is that an
 * order-8/8 model over a grid of 87 500 cells allocates some 1.6 million short-lived objects per
 * redraw and pays dearly for them. Measured, it does not: the whole field costs **17.6 ms** through
 * `polyEval` against **9.5 ms** here, because V8's escape analysis removes most of those allocations
 * on a hot loop that never lets one outlive its iteration. So the honest justification is a factor
 * of two on a ten-millisecond redraw, not an order of magnitude — kept because it is fifteen lines
 * pinned by a test against `polyEval`, and because 9.5 ms of field plus 2.6 ms of contours fits
 * inside one frame where 17.6 + 2.6 does not.
 *
 * It returns the *squared* magnitude rather than the complex value for a related reason: a
 * `{re, im}` return would put two allocations per cell back, and it is all the caller wants — it
 * saves a square root, since `10·log10` of a power ratio is `20·log10` of an amplitude ratio.
 *
 * Exported only so a test can pin it against `polyEval`, which is what stops the copy drifting.
 */
export function polyAbs2(coeffs: readonly number[], sr: number, si: number): number {
  let hr = 0
  let hi = 0
  for (let k = coeffs.length - 1; k >= 0; k--) {
    const nextRe = hr * sr - hi * si + coeffs[k]!
    hi = hr * si + hi * sr
    hr = nextRe
  }
  return hr * hr + hi * hi
}

/**
 * 20·log10|H(σ+jω)|, with σ and ω given as *frequencies* in Hz — i.e. σ/2π and ω/2π, the same units
 * the pole–zero map's axes carry.
 *
 * On the jω axis (`sigmaHz = 0`) this is `20log10|evaluateModel(model, omegaHz)|` by construction:
 * both build `s = 2πf/omegaScale` and divide the same two polynomials. That equality is asserted
 * rather than assumed, because a missing or doubled 2π here would produce a picture that looks
 * entirely plausible and is a factor of 6.28 wrong in frequency.
 */
export function modelLevelDb(model: RationalModel, sigmaHz: number, omegaHz: number): number {
  const scale = (2 * Math.PI) / model.omegaScale
  const sr = sigmaHz * scale
  const si = omegaHz * scale
  const numAbs2 = polyAbs2(model.num, sr, si)
  const denAbs2 = polyAbs2(model.den, sr, si)
  if (denAbs2 === 0) return numAbs2 === 0 ? Number.NaN : Number.POSITIVE_INFINITY
  // One log per cell rather than two: 10·log10 of a power ratio is 20·log10 of an amplitude ratio.
  return 10 * Math.log10(numAbs2 / denAbs2)
}

/** How many frequencies `passbandReferenceDb` samples. */
const REFERENCE_SAMPLES = 64

/**
 * The surface's 0 dB reference: the **median** of |H(jω)| in dB across the measured band.
 *
 * Not `FitResult.dcGainDb`, for two reasons. It is magnitude-only, so it cannot describe an
 * inverting stage; and — the substantive one — `dcGainExtrapolation` exists precisely because H(0)
 * is frequently an extrapolation through a corner the sweep never reached. On an EL84 fitted at
 * order 6 it read 18.43 dB against a measured passband of 26.58 dB, so a surface referenced to it
 * would sit 8 dB out on every contour.
 *
 * Median rather than peak or mean, for the reason the bandwidth reference already uses one: a mean
 * in dB is dragged down by any null and a mean of magnitudes up by any resonance, while a peak is
 * the resonance itself. A median over a band the DUT is flat in is the level a datasheet would quote.
 */
export function passbandReferenceDb(
  model: RationalModel,
  band: MeasuredBand | null,
  samples = REFERENCE_SAMPLES,
): number {
  const low = band && band.lowHz > 0 ? band.lowHz : 20
  const high = band && band.highHz > low ? band.highHz : Math.max(low * 10, 20000)

  const levels: number[] = []
  const ratio = Math.log(high / low)
  for (let i = 0; i < samples; i++) {
    const f = low * Math.exp((ratio * i) / Math.max(1, samples - 1))
    const h = evaluateModel(model, f)
    const level = 20 * Math.log10(Math.hypot(h.re, h.im))
    if (Number.isFinite(level)) levels.push(level)
  }
  if (levels.length === 0) return 0

  levels.sort((a, b) => a - b)
  const mid = levels.length >> 1
  return levels.length % 2 === 1 ? levels[mid]! : (levels[mid - 1]! + levels[mid]!) / 2
}

export interface SurfaceWindow {
  referenceDb: number
  topDb: number
  bottomDb: number
}

/**
 * The colour window, from offsets relative to the reference.
 *
 * Offsets rather than absolute levels so the window means the same thing on a 0 dB filter and a
 * +26 dB amplifier — the alternative is a setting the user has to re-enter per DUT. Headroom *above*
 * the reference is what makes a pole legible: it runs to +∞, so the top of the scale is where it
 * saturates rather than a level anything actually reaches.
 */
export function surfaceWindow(referenceDb: number, topOffsetDb: number, bottomOffsetDb: number): SurfaceWindow {
  const top = referenceDb + Math.max(topOffsetDb, bottomOffsetDb)
  const bottom = referenceDb + Math.min(topOffsetDb, bottomOffsetDb)
  // A collapsed window would make levelIndex divide by zero and paint one flat colour.
  return { referenceDb, topDb: top, bottomDb: bottom === top ? top - 1 : bottom }
}

/** The dB steps a contour set may use — the 1/2/5 family plus the thirds a decibel scale wants. */
const CONTOUR_STEPS = [1, 2, 3, 6, 10, 12, 20, 30] as const

/** At most this many contour lines across the window, past which they read as a solid wash. */
const MAX_CONTOUR_LINES = 12

/**
 * Iso-level contour values in **absolute** dB, on a step chosen so at most `maxLines` cross the
 * window, and always including the reference itself and 3 dB below it.
 *
 * The step is chosen from a *relative* family because the absolute level is arbitrary — a filter is
 * 0 dB, an amplifier +26, a loop gain anything at all — so "contours every 6 dB from the passband"
 * transfers between DUTs where "contours at −12, −6, 0" does not. The reference line is what the
 * chart draws heavier, and −3 dB is included because it is the number the bandwidth reading uses.
 */
export function contourLevels(w: SurfaceWindow, maxLines = MAX_CONTOUR_LINES): number[] {
  const span = w.topDb - w.bottomDb
  if (!(span > 0) || !Number.isFinite(span)) return []

  const step = CONTOUR_STEPS.find((candidate) => span / candidate <= maxLines) ?? 50

  const levels = new Set<number>()
  // Walk outward from the reference so the grid is anchored on it rather than on the window's edge.
  for (let k = Math.ceil((w.bottomDb - w.referenceDb) / step); ; k++) {
    const level = w.referenceDb + k * step
    if (level > w.topDb + 1e-9) break
    levels.add(Number(level.toPrecision(12)))
  }
  if (w.referenceDb - 3 >= w.bottomDb && w.referenceDb - 3 <= w.topDb) {
    levels.add(Number((w.referenceDb - 3).toPrecision(12)))
  }
  return [...levels].sort((a, b) => a - b)
}

/**
 * How close to the reference a level has to be to *be* the reference.
 *
 * <p>`contourLevels` rounds every level to 12 significant digits, so the reference's own level comes
 * back a whisker away from `referenceDb` rather than equal to it — 1e-13 or so for an amplifier's
 * +26 dB passband. Both readers of that list need the same tolerance, and they must have the same
 * one: the heavy contour line and the "0" on the colour key have to name one level.</p>
 */
export const REFERENCE_LEVEL_EPSILON_DB = 1e-9

/**
 * A contour level as the colour key labels it: signed dB relative to the reference, with the
 * reference itself reading `0`.
 *
 * <p>Without the snap the residue above *is* the whole value at the reference, and three significant
 * digits of 1e-13 is the string `-1.07e-13` — nine characters where the other labels are three, so
 * it also pushed the key's rotated caption off the right edge of the canvas. Reported with a
 * screenshot, 2026-09-27.</p>
 */
export function relativeLevelLabel(levelDb: number, referenceDb: number): string {
  const raw = levelDb - referenceDb
  const relative = Math.abs(raw) < REFERENCE_LEVEL_EPSILON_DB ? 0 : raw
  return `${relative > 0 ? '+' : ''}${Number(relative.toPrecision(3))}`
}

/**
 * Evaluate the model over a grid of cell centres spanning `viewport` in **mapped** coordinates.
 *
 * Sampling is per *screen* cell, and each cell is mapped back to a real s through `toHz`. That is
 * what makes the contours correct under the log-modulus warp for nothing: a grid laid out in the
 * plane and then warped would need its contours warped too, and a marching-squares polyline through
 * a warped grid is not the warp of the polyline through the plain one.
 */
export function evaluateField(
  model: RationalModel,
  map: SPlaneMap,
  viewport: Viewport,
  cols: number,
  rows: number,
): SurfaceField {
  const width = Math.max(1, Math.floor(cols))
  const height = Math.max(1, Math.floor(rows))
  const levelDb = new Float32Array(width * height)

  const spanX = viewport.xMax - viewport.xMin
  const spanY = viewport.yMax - viewport.yMin

  for (let row = 0; row < height; row++) {
    // Row 0 is the top, so y runs down from yMax — screen order, not complex-plane order.
    const v = viewport.yMax - (spanY * (row + 0.5)) / height
    const base = row * width
    for (let col = 0; col < width; col++) {
      const u = viewport.xMin + (spanX * (col + 0.5)) / width
      const s = toHz(map, u, v)
      levelDb[base + col] = modelLevelDb(model, s.reHz, s.imHz)
    }
  }

  return { cols: width, rows: height, levelDb, viewport }
}

/** The level at a cell, or NaN outside the grid — for the hover readout. */
export function levelAtCell(field: SurfaceField, col: number, row: number): number {
  if (col < 0 || row < 0 || col >= field.cols || row >= field.rows) return Number.NaN
  return field.levelDb[row * field.cols + col]!
}

/**
 * Marching squares at one level, returning **unconnected** segments as a flat
 * `[x0, y0, x1, y1, …]` in cell coordinates (cell centres at integer positions).
 *
 * Unconnected on purpose: every segment goes into one `beginPath`/`stroke`, which canvas draws as
 * fast as a stitched polyline would, and stitching only buys inline labels — which this view does
 * not have, because the colour key carries the numbers. Should labels arrive, chain the segments
 * then, not now.
 *
 * The two ambiguous cases (5 and 10 — diagonal corners above the level) are resolved by the average
 * of the four corners, which is the standard saddle test and the only one that cannot produce
 * crossed segments.
 */
export function marchingSquares(field: SurfaceField, levelDb: number, stride = 1): number[] {
  const out: number[] = []
  const step = Math.max(1, Math.floor(stride))
  const { cols, rows, levelDb: z } = field

  // A non-finite level (a pole) counts as above: the contour then closes around the pole rather than
  // leaving a gap in the ring where the cell overflowed.
  const above = (value: number) => (Number.isNaN(value) ? false : value >= levelDb)
  // Linear interpolation along an edge; a non-finite end has no finite crossing, so fall to the mid.
  const cross = (a: number, b: number) => {
    if (!Number.isFinite(a) || !Number.isFinite(b)) return 0.5
    return a === b ? 0.5 : (levelDb - a) / (b - a)
  }

  for (let row = 0; row + step < rows; row += step) {
    for (let col = 0; col + step < cols; col += step) {
      const x0 = col
      const x1 = col + step
      const y0 = row
      const y1 = row + step

      // Corners, named by compass position on screen (y grows downward through the rows).
      const nw = z[y0 * cols + x0]!
      const ne = z[y0 * cols + x1]!
      const se = z[y1 * cols + x1]!
      const sw = z[y1 * cols + x0]!

      let code = 0
      if (above(nw)) code |= 8
      if (above(ne)) code |= 4
      if (above(se)) code |= 2
      if (above(sw)) code |= 1
      if (code === 0 || code === 15) continue

      // Edge crossings: top, right, bottom, left.
      const top = { x: x0 + step * cross(nw, ne), y: y0 }
      const right = { x: x1, y: y0 + step * cross(ne, se) }
      const bottom = { x: x0 + step * cross(sw, se), y: y1 }
      const left = { x: x0, y: y0 + step * cross(nw, sw) }

      const push = (a: { x: number; y: number }, b: { x: number; y: number }) => out.push(a.x, a.y, b.x, b.y)

      switch (code) {
        case 1:
        case 14:
          push(left, bottom)
          break
        case 2:
        case 13:
          push(bottom, right)
          break
        case 3:
        case 12:
          push(left, right)
          break
        case 4:
        case 11:
          push(top, right)
          break
        case 6:
        case 9:
          push(top, bottom)
          break
        case 7:
        case 8:
          push(left, top)
          break
        case 5:
        case 10: {
          // The saddle. The centre's own value decides which pair of corners is connected; getting
          // this wrong produces two crossed segments, which read as an X in the middle of a contour.
          const centreAbove = above((nw + ne + se + sw) / 4)
          if ((code === 5) === centreAbove) {
            push(left, top)
            push(bottom, right)
          } else {
            push(left, bottom)
            push(top, right)
          }
          break
        }
      }
    }
  }

  return out
}
