/**
 * Putting several sweeps that were measured at *different* frequencies onto one chart.
 *
 * uPlot draws a single x array for the whole plot, so ThdSweepChart builds the union of every
 * visible sweep's frequency grid (see its `frequencies`). A trace then has to supply a y value at
 * every one of those frequencies — including the ones only some *other* sweep measured. Leaving
 * those null looked reasonable and is what broke the chart: two sweeps whose grids barely
 * intersect (adaptive vs fixed resolution, 4 vs 5 points per decade) interleave, so each trace
 * ends up as value/null/value/null and uPlot draws it as a scatter of one-point segments — with
 * lines only at the few frequencies that happened to coincide, which is the "strange lines".
 *
 * The fix is to fill the in-between grid slots by interpolating each trace along its own measured
 * points, which is exactly the straight segment a line chart would have drawn between two adjacent
 * measurements anyway. The measured values themselves are never touched, nothing is invented
 * outside a sweep's own frequency range, and a genuine gap (a null at a measured frequency) still
 * breaks the line.
 */

/**
 * Fills a trace's y values across `gridFreqHz`, given the grid indices it actually has
 * measurements at.
 *
 * `anchorIdx` must be ascending indices into `gridFreqHz` — the union grid is built from these
 * very frequencies, so every measured point has an exact slot in it and no lookup is approximate.
 * `anchorValues[k]` is the value at `anchorIdx[k]`, already in axis units, `null` for "measured but
 * has no position on this axis" (ThdSweepChart's toY, e.g. a non-positive percentage).
 *
 * Interpolation is linear in log-frequency because the x axis is log-frequency, and — when `logY`
 * — linear in log-value because the percent y axis is logarithmic too. Together that makes a
 * filled slot land exactly on the straight line the chart draws between its two neighbours, so
 * densifying a trace cannot change its shape. dB (`logY: false`) is already a log quantity on a
 * linear axis, so it interpolates linearly.
 *
 * Outside `[first anchor, last anchor]` the result stays null: a sweep that stopped at 5 kHz must
 * not sprout a flat line out to 20 kHz just because something else on the chart went that far.
 */
export function interpolateOnGrid(
  gridFreqHz: readonly number[],
  anchorIdx: readonly number[],
  anchorValues: readonly (number | null)[],
  logY: boolean,
): (number | null)[] {
  const out: (number | null)[] = new Array(gridFreqHz.length).fill(null)

  for (let k = 0; k < anchorIdx.length; k++) out[anchorIdx[k]] = anchorValues[k]

  for (let k = 0; k + 1 < anchorIdx.length; k++) {
    const i0 = anchorIdx[k]
    const i1 = anchorIdx[k + 1]
    if (i1 - i0 < 2) continue // adjacent on the grid — nothing to fill between them

    const v0 = anchorValues[k]
    const v1 = anchorValues[k + 1]
    if (v0 == null || v1 == null) continue // a gap the trace really has; do not bridge it
    if (logY && (v0 <= 0 || v1 <= 0)) continue

    const x0 = Math.log(Math.max(gridFreqHz[i0], Number.EPSILON))
    const x1 = Math.log(Math.max(gridFreqHz[i1], Number.EPSILON))
    const span = x1 - x0
    const y0 = logY ? Math.log(v0) : v0
    const y1 = logY ? Math.log(v1) : v1

    for (let i = i0 + 1; i < i1; i++) {
      const frac = span > 0 ? (Math.log(Math.max(gridFreqHz[i], Number.EPSILON)) - x0) / span : 0
      const y = y0 + frac * (y1 - y0)
      out[i] = logY ? Math.exp(y) : y
    }
  }

  return out
}
