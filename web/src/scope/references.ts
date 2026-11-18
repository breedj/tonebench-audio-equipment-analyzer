/**
 * Putting a stored capture on the live trace's own x array — the arithmetic behind a reference
 * trace.
 *
 * <p>uPlot plots every series against <b>one</b> x array, and a reference is a different capture:
 * cut to the same window by the analyzer, but taken at that measurement's own sample rate. A
 * reference of a 48 kS/s capture beside a 192 kS/s live record holds a quarter as many samples
 * over the same stretch of time, so copying it across by <i>index</i> would compress it into the
 * left quarter of the window — a trace drawn at a quarter of its real timebase, which reads as a
 * DUT four times faster than it is. The mapping has to be by <b>time</b>.</p>
 *
 * <p>It lives here rather than in the chart for the usual reason: nothing in a <code>.vue</code> is
 * testable under the node environment vitest runs in, and every rule below is quietly wrong rather
 * than visibly broken.</p>
 */

/** The x array a slice is being put onto, in seconds — the live record's wherever there is one. */
export interface ScopeGrid {
  /** Time of the first column, seconds from the trigger. Negative over the pre-trigger stretch. */
  x0S: number
  /** Seconds per column. */
  dxS: number
  /** How many columns the array holds. */
  count: number
}

/** Where a stored capture's own samples sit, in the same units. */
export interface ScopeSourceGrid {
  x0S: number
  dxS: number
  length: number
}

/**
 * How a value between two of the source's own samples is answered.
 *
 * <p><b>interpolate</b> for a raw waveform, which is what the chart draws between two samples
 * anyway. <b>nearest</b> for a min/max column, where a value is already an <i>extreme</i> over a
 * stretch: averaging two extremes invents a level the capture never reached, and on a noise floor
 * that reads several dB quiet — the same bias that makes plotting the max alone wrong.</p>
 */
export type ScopeGridMode = 'interpolate' | 'nearest'

/**
 * Resamples one stored column array onto the grid, by time.
 *
 * <p>A target time outside the source's own span answers <b>null</b> — a gap, never a zero and
 * never the nearest end: a reference that covers less of the window than the live trace has
 * nothing to say there, and a flat line along zero would be read as a measurement.</p>
 *
 * <p>Where the two grids are the same, which is the ordinary case (one window, one column budget,
 * one sample rate), every target lands exactly on a source sample and the values come back
 * unchanged — so there is no special case for it and none to get wrong.</p>
 */
export function onScopeGrid(
  values: readonly number[] | null | undefined,
  source: ScopeSourceGrid,
  grid: ScopeGrid,
  mode: ScopeGridMode,
): (number | null)[] {
  const out = new Array<number | null>(Math.max(0, grid.count)).fill(null)
  if (!values || source.length === 0 || !Number.isFinite(source.dxS) || source.dxS <= 0) return out

  const usable = Math.min(values.length, source.length)
  if (usable === 0) return out

  // Where the grid's column i falls in the source's own sample index, written as one offset plus
  // one step rather than as (t - x0) / dx per column. Algebraically the same thing, and exact where
  // the two grids are the same: the offset is then 0 and the step 1, so a reference taken at the
  // live record's own rate comes back sample for sample instead of nudged a rounding error toward
  // its neighbour at every column.
  const offset = (grid.x0S - source.x0S) / source.dxS
  const step = grid.dxS / source.dxS

  for (let i = 0; i < out.length; i++) {
    const position = offset + i * step

    // Half a source step of slack at either end, so a grid whose first or last column sits a
    // rounding error outside the source is not drawn as a gap at the very edge of the window.
    if (position < -0.5 || position > usable - 0.5) continue

    if (mode === 'nearest') {
      const index = Math.min(usable - 1, Math.max(0, Math.round(position)))
      const value = values[index]
      out[i] = Number.isFinite(value) ? value : null
      continue
    }

    const lower = Math.min(usable - 1, Math.max(0, Math.floor(position)))
    const upper = Math.min(usable - 1, lower + 1)
    const a = values[lower]
    const b = values[upper]
    if (!Number.isFinite(a) || !Number.isFinite(b)) continue

    const fraction = Math.min(1, Math.max(0, position - lower))
    out[i] = lower === upper ? a : a + (b - a) * fraction
  }

  return out
}
