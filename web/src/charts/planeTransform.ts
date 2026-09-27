/**
 * The world-to-pixel mapping for the hand-drawn plane charts (Nyquist, Nichols).
 *
 * Separated from the component because it is the arithmetic most likely to be quietly wrong, and
 * nothing inside a `.vue` file is testable here — vitest runs in the `node` environment with no DOM
 * (`web/vitest.config.ts`).
 */

export interface Viewport {
  xMin: number
  xMax: number
  yMin: number
  yMax: number
}

export interface PlaneTransform {
  /** Pixels per world unit along x, and along y. Equal when the transform was asked for equal aspect. */
  scaleX: number
  scaleY: number
  toScreenX(x: number): number
  toScreenY(y: number): number
  toWorldX(px: number): number
  toWorldY(py: number): number
  /** The viewport actually in force — wider than the one asked for when equal aspect had to letterbox it. */
  viewport: Viewport
}

export interface PlaneInsets {
  left: number
  right: number
  top: number
  bottom: number
}

/**
 * Map `viewport` onto the plot area of a `widthPx` x `heightPx` canvas.
 *
 * **Equal aspect is not cosmetic on a Nyquist plot.** The whole content of the picture is how close
 * the contour passes to −1, and unequal pixels-per-unit misstate that distance in a way no axis
 * label reveals. So when `equalAspect` is set, both axes take the *smaller* scale and the viewport
 * is widened on the roomier axis — the requested window is always fully visible, never cropped.
 *
 * y grows upward, as a complex plane and a dB axis both do, and opposite to the canvas.
 */
export function fitTransform(
  viewport: Viewport,
  widthPx: number,
  heightPx: number,
  insets: PlaneInsets,
  equalAspect: boolean,
): PlaneTransform {
  const plotWidth = Math.max(1, widthPx - insets.left - insets.right)
  const plotHeight = Math.max(1, heightPx - insets.top - insets.bottom)

  // A zero-width window would divide by zero and paint nothing; a degenerate viewport is a symptom
  // of an empty curve, not of a bug worth throwing over.
  const spanX = viewport.xMax - viewport.xMin || 1
  const spanY = viewport.yMax - viewport.yMin || 1

  let scaleX = plotWidth / spanX
  let scaleY = plotHeight / spanY

  let effective = viewport
  if (equalAspect) {
    const scale = Math.min(scaleX, scaleY)
    scaleX = scale
    scaleY = scale

    // Re-centre on the requested window, widened to whatever the smaller scale now admits.
    const midX = (viewport.xMin + viewport.xMax) / 2
    const midY = (viewport.yMin + viewport.yMax) / 2
    const halfX = plotWidth / (2 * scale)
    const halfY = plotHeight / (2 * scale)
    effective = { xMin: midX - halfX, xMax: midX + halfX, yMin: midY - halfY, yMax: midY + halfY }
  }

  const originPx = insets.left - effective.xMin * scaleX
  const originPy = insets.top + effective.yMax * scaleY

  return {
    scaleX,
    scaleY,
    viewport: effective,
    toScreenX: (x) => originPx + x * scaleX,
    toScreenY: (y) => originPy - y * scaleY,
    toWorldX: (px) => (px - originPx) / scaleX,
    toWorldY: (py) => (originPy - py) / scaleY,
  }
}

/**
 * Drop the entries that would overlap one already kept, in the order given.
 *
 * For an axis whose ticks are not evenly spaced on screen — a log-modulus s-plane, where a decade
 * near the origin occupies a few pixels by design — so the labels that have room are drawn and the
 * ones that would collide are silently lost. Order is the priority: pass zero, or whichever label
 * matters most, first, and it is the one that survives a crowd.
 *
 * Returns the kept entries rather than mutating, so the caller can use it for both axes.
 */
export function thinPositions<T extends { at: number }>(entries: readonly T[], minGap: number): T[] {
  const kept: T[] = []
  for (const entry of entries) {
    if (!Number.isFinite(entry.at)) continue
    if (kept.some((other) => Math.abs(other.at - entry.at) < minGap)) continue
    kept.push(entry)
  }
  return kept
}

/**
 * "Nice" tick values covering a span — 1/2/5 x a power of ten, the same family uPlot's linear axes
 * use, so the hand-drawn charts read like the rest of the app.
 */
export function niceTicks(min: number, max: number, target: number): number[] {
  const span = max - min
  if (!(span > 0) || !Number.isFinite(span) || target < 1) return []

  const rough = span / target
  const magnitude = Math.pow(10, Math.floor(Math.log10(rough)))
  const normalised = rough / magnitude
  const step = (normalised > 5 ? 10 : normalised > 2 ? 5 : normalised > 1 ? 2 : 1) * magnitude

  const out: number[] = []
  // A hard cap rather than trust in the arithmetic: a pathological span once produced enough ticks
  // to lock the paint loop, and a chart that hangs is worse than one with coarse gridlines.
  for (let v = Math.ceil(min / step) * step, guard = 0; v <= max && guard < 1000; v += step, guard++) {
    // Snap away the float dust `0.1 + 0.2` leaves, so labels read "0.3" and not "0.30000000000000004".
    out.push(Math.abs(v) < step / 1e6 ? 0 : Number(v.toPrecision(12)))
  }
  return out
}
