// The impedance chart's shared x grid.
//
// uPlot's AlignedData gives every series one x array, so a curve is drawn only where that array
// reaches and only as finely as it is spaced there. With a live measurement and a handful of checked
// history items on one chart, those are three or four curves decimated independently — a different
// span, resolution or stimulus puts their columns in different places — so the array has to be the
// union of all of them, and each curve is then interpolated onto it (ImpedanceChart.vue).
//
// It lives here rather than in the chart because nothing in a `.vue` is testable under the node
// environment vitest runs in, and the two faults this fixes were both invisible by eye: a stored
// curve silently clipped to the live sweep's reach, and a stored curve silently *subsampled* to the
// live sweep's resolution.

/**
 * Every frequency any curve on the chart has a sample at, sorted and de-duplicated.
 *
 * <p>`base` is the live measurement's grid — or a standard's, when there is nothing measured — and
 * `extras` are the checked history items'. An empty `base` falls back to the first non-empty extra,
 * so a stored result checked on before anything has been run still has an axis to be drawn against.
 * </p>
 *
 * <p>De-duplicated because two overlays of the same measurement contribute the same frequencies, and
 * a repeated x is a zero-width segment uPlot has to draw for nothing.</p>
 */
export function mergeGrids(base: ArrayLike<number>, extras: ArrayLike<number>[]): Float64Array {
  if (base.length === 0) {
    const first = extras.find((e) => e.length > 0)
    return first ? Float64Array.from(first) : new Float64Array()
  }

  if (extras.every((e) => e.length === 0)) return Float64Array.from(base)

  const merged = new Set<number>(Array.from(base))
  for (const extra of extras) {
    for (let i = 0; i < extra.length; i++) merged.add(extra[i])
  }

  return Float64Array.from([...merged].sort((a, b) => a - b))
}

/**
 * Which columns of `grid` are a measured frequency of `measuredHz` — the only ones a Data points
 * marker may sit on, now that the grid also carries every overlay's columns.
 *
 * <p>Both are sorted ascending and the measured frequencies are a subset of the grid by construction
 * (the grid is built from them), so one walk places them all. A frequency the grid does not hold is
 * skipped rather than guessed at, which is what keeps a caller that merged a different set of curves
 * from putting a marker on somebody else's column.</p>
 */
export function measuredColumns(grid: ArrayLike<number>, measuredHz: ArrayLike<number>): number[] {
  const out: number[] = []
  let j = 0

  for (let i = 0; i < grid.length && j < measuredHz.length; i++) {
    if (grid[i] === measuredHz[j]) {
      out.push(i)
      j++
    }
  }

  return out
}
