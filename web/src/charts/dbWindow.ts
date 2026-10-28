/**
 * The arithmetic behind a display rail's **Autofit** on a dB axis: the lowest and highest sample a
 * chart is drawing in, a Top/Bottom pair out.
 *
 * <p>A module rather than a function per panel, and the reason is the bug it was extracted for
 * rather than tidiness. Both panels used to settle the two bounds by assignment —
 * `yTopDb.value = …` and then `Math.min(bottom, yTopDb.value - minSpan)` on the next line — and
 * `yTopDb` is a `defineModel` with a listener on it, so its setter emits `update:yTopDb` and leaves
 * the value to arrive back as a prop on the parent's *next* render. The read-back therefore hands
 * out the window being replaced, and the second clamp is taken against it. Measured 2026-10-28 on
 * the crosstalk chart with Top at 0 dB and a curve at +50 to +67: Top landed on its correct 70 and
 * Bottom on `min(45, 0 − 10)` = −10, a window five times too tall whose arithmetic reads perfectly
 * sensible on screen. Computing the pair as one value is what makes that unavailable, and it is
 * what this function exists to be.</p>
 */

/** The ladder and the limits a chart's dB axis is fitted on. */
export interface DbWindowBounds {
  /** Round each bound outward to a whole multiple of this. */
  step: number
  /** Clearance left beyond the data before rounding, in dB. */
  pad: number
  /** The narrowest window the axis may end up with. */
  minSpan: number
  /** The floor and ceiling the two boxes themselves accept. */
  min: number
  max: number
}

/** A fitted dB axis window. */
export interface DbWindow {
  top: number
  bottom: number
}

/**
 * The window that frames `lo`…`hi`: padded, rounded outward to whole steps, widened to `minSpan`
 * and then held inside `[min, max]`. Null when the caller found nothing to fit, so an Autofit over
 * an empty chart leaves the axis exactly where it was.
 *
 * <p>The ceiling binds the top first and the span is then taken *from the bound top*, so a curve
 * that runs off the top of the allowed range still gets a legal window rather than one whose two
 * ends disagree about how tall it is.</p>
 */
export function fitDbWindow(lo: number, hi: number, bounds: DbWindowBounds): DbWindow | null {
  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return null

  const { step, pad, minSpan, min, max } = bounds
  const fittedTop = Math.ceil((hi + pad) / step) * step
  const fittedBottom = Math.floor((lo - pad) / step) * step

  // The floor is applied *before* the top is widened to `minSpan`, or a curve lying against the
  // bottom of the allowed range widens against a bottom the clamp is about to raise and the window
  // comes out shorter than the minimum span. Caught by its own test rather than on a bench: it
  // needs a curve within one span of ±200 dB, which nothing measures.
  const floored = Math.max(min, Math.min(fittedBottom, max - minSpan))
  const top = Math.min(max, Math.max(fittedTop, floored + minSpan))
  const bottom = Math.max(min, Math.min(floored, top - minSpan))
  return { top, bottom }
}
