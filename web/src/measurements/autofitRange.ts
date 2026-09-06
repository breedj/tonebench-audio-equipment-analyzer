/**
 * The axis window that fits a set of plotted values, snapped to a readable grid.
 *
 * Pure and in its own module for the reason `reorder.ts` is: nothing in a `.vue` file is testable
 * under the node environment vitest runs in, and an autofit that silently returns a degenerate
 * window looks like a broken chart rather than a broken calculation.
 */
export interface AutofitOptions {
  /** Grid the result is snapped to — 5 dB for a level axis, 45° for phase. */
  step?: number
  /** Never return a window narrower than this, so a flat trace does not fill the plot with noise. */
  minSpan?: number
  /** Headroom either side, as a fraction of the data's own span. */
  pad?: number
  /**
   * A floor the fit will not go below.
   *
   * For data that decays toward zero the minimum is not informative: a gated impulse response is
   * tapered to nothing at its edges, so its lowest sample sits at whatever the dB conversion's own
   * floor happens to be — fitting to that produced a 255 dB tall plot with the impulse as a spike
   * along the top. Where the useful range is bounded by what the measurement can say rather than by
   * the numbers in the array, the caller says so here.
   */
  minBottom?: number
}

export interface AxisRange {
  top: number
  bottom: number
}

/**
 * @returns The window, or null when there is nothing finite to fit — the caller should then leave
 * the axis alone rather than snapping it to an arbitrary default.
 */
export function autofitRange(values: Iterable<number>, options: AutofitOptions = {}): AxisRange | null {
  const { step = 5, minSpan = 10, pad = 0.05, minBottom = Number.NEGATIVE_INFINITY } = options

  let lo = Number.POSITIVE_INFINITY
  let hi = Number.NEGATIVE_INFINITY
  for (const value of values) {
    // Guards the floors every dB conversion in this app applies (-200, -1e-30 → -600 dB and so on):
    // one of those in the data would drag the window down by hundreds of dB and hide the trace.
    if (!Number.isFinite(value)) continue
    if (value < lo) lo = value
    if (value > hi) hi = value
  }

  if (lo > hi) return null

  const span = hi - lo
  const margin = Math.max(span * pad, step / 2)

  let bottom = Math.max(Math.floor((lo - margin) / step) * step, minBottom)
  let top = Math.ceil((hi + margin) / step) * step

  // A trace that is flat to within the grid would otherwise produce a zero-height window.
  if (top - bottom < minSpan) {
    const centre = (top + bottom) / 2
    bottom = Math.floor((centre - minSpan / 2) / step) * step
    top = bottom + minSpan
  }

  // Adding zero turns -0 into +0. Math.ceil of a small negative yields -0, which would put "-0" on
  // the axis and compares unequal to 0 anywhere the window is checked.
  return { top: top + 0, bottom: bottom + 0 }
}

/**
 * The same fit, restricted to values inside a band of a parallel x array — so autofit sizes what is
 * actually on screen rather than the parts of a curve scrolled off it.
 */
export function autofitRangeInBand(
  x: ArrayLike<number>,
  values: ArrayLike<number>,
  lowX: number,
  highX: number,
  options: AutofitOptions = {},
): AxisRange | null {
  const inBand: number[] = []
  const count = Math.min(x.length, values.length)
  for (let i = 0; i < count; i++) {
    if (x[i] >= lowX && x[i] <= highX) inBand.push(values[i])
  }

  // Nothing in the band is not the same as nothing to fit: fall back to the whole curve rather than
  // leaving the axis unchanged, which would look like the button did nothing.
  return autofitRange(inBand.length > 0 ? inBand : Array.from({ length: count }, (_, i) => values[i]), options)
}
