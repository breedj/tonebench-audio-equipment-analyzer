/**
 * Snapping a cursor position on the spectrum to the peak it is obviously aimed at.
 *
 * <p>Lifted out of SpectrumChart's `setCursor` hook so the <b>cursor readout</b> and
 * <b>click-to-annotate</b> share one rule instead of two copies that drift. They must agree: an
 * annotation placed on a harmonic has to land on the column whose level the readout was showing
 * when the user clicked, or the label says one thing and the chart shows another.</p>
 */

/**
 * How far, in CSS pixels, the search looks either side of the cursor.
 *
 * <p>Reading a harmonic's or the fundamental's true level off the chart would otherwise need
 * pixel-perfect mouse placement on that exact column — easy to miss by a column or two, especially
 * at higher frequencies where many raw bins collapse into one narrow on-screen column. The radius
 * bounds how far this can jump, so it never latches onto some distant, unrelated peak.</p>
 */
export const PEAK_SNAP_RADIUS_PX = 15

/**
 * The index of the local maximum within the search radius of the cursor, or `startIdx` when there
 * is nothing better — which includes every candidate being null (a hidden channel's null-filled
 * series) and a tie, since a tie means the column under the cursor is already as high as anything
 * beside it.
 *
 * @param posAt The column's position in the same CSS pixels as `cursorPx`.
 * @param valueAt The column's plotted value, or null where the series has none.
 */
export function peakSnapIndex(args: {
  count: number
  posAt: (index: number) => number
  valueAt: (index: number) => number | null
  cursorPx: number
  startIdx: number
  radiusPx?: number
}): number {
  const { count, posAt, valueAt, cursorPx, startIdx } = args
  const radius = args.radiusPx ?? PEAK_SNAP_RADIUS_PX

  let bestIdx = startIdx
  let bestValue = valueAt(startIdx)
  if (!(cursorPx >= 0)) return bestIdx

  for (let i = 0; i < count; i++) {
    const value = valueAt(i)
    if (value === null) continue
    if (Math.abs(posAt(i) - cursorPx) > radius) continue
    if (bestValue === null || value > bestValue) {
      bestValue = value
      bestIdx = i
    }
  }
  return bestIdx
}
