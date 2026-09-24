/**
 * Putting a stored impulse response on the same time axis as the live one.
 *
 * <p>uPlot has one x array for every series, so a history overlay has to be resampled onto a grid it
 * shares with the measurement. Which grid that is, is the whole of the design here, and it is not
 * the union the impedance chart builds.</p>
 *
 * <p><b>Inside the measured span the live trace's own columns are the grid, untouched.</b> An impulse
 * response is a waveform decimated to min/max per column, and the chart draws that envelope as a
 * vertical per column: interpolating the live trace onto a denser grid would invent columns and draw
 * an envelope narrower than the one measured. The overlays are the curves that get approximated,
 * which is the honest ordering — the measurement is what is being read, the overlay is what it is
 * being compared against.</p>
 *
 * <p><b>Outside it the overlays' own times are added</b>, so a stored run gated wider than the live
 * one is drawn in full rather than clipped to it. The live curve is null there, which uPlot draws as
 * a gap. Gates usually match between runs of one measurement, so in the ordinary case the grid is
 * exactly the live trace's and nothing is resampled at all.</p>
 */

/**
 * The shared time axis, in milliseconds: the live trace's columns, plus any overlay column lying
 * outside the span they cover. Ascending and free of exact duplicates.
 *
 * <p>With no live trace — history checked on before a run — the grid is the overlays' own times
 * merged, so the chart still draws.</p>
 */
export function impulseTimeGrid(
  liveTimeMs: readonly number[],
  overlayTimeMs: readonly (readonly number[])[],
): number[] {
  const merged = [...liveTimeMs]

  if (liveTimeMs.length === 0) {
    for (const times of overlayTimeMs) merged.push(...times)
  } else {
    // Only what the live columns cannot represent. Anything inside the span is dropped, which is
    // what keeps the live trace exact on its own grid.
    const lo = liveTimeMs[0]
    const hi = liveTimeMs[liveTimeMs.length - 1]
    for (const times of overlayTimeMs) {
      for (const t of times) if (t < lo || t > hi) merged.push(t)
    }
  }

  merged.sort((a, b) => a - b)

  const grid: number[] = []
  for (const t of merged) if (grid.length === 0 || t !== grid[grid.length - 1]) grid.push(t)
  return grid
}

/**
 * `values` sampled at every point of `grid`, interpolated linearly in time, and **null outside the
 * source's own span** — a curve says nothing about time it never covered, and a held end value would
 * draw a flat tail that reads as a measurement.
 *
 * <p>A grid point that coincides with a source sample returns that sample exactly, which is what
 * makes the live trace lossless on the grid <see cref="impulseTimeGrid"/> builds for it.</p>
 */
export function resampleImpulse(
  timeMs: readonly number[],
  values: readonly number[],
  grid: readonly number[],
): (number | null)[] {
  const out: (number | null)[] = new Array(grid.length).fill(null)
  if (timeMs.length === 0 || values.length !== timeMs.length) return out

  const lo = timeMs[0]
  const hi = timeMs[timeMs.length - 1]

  // Both ascending, so one walk rather than a search per point.
  let i = 0
  for (let g = 0; g < grid.length; g++) {
    const t = grid[g]
    if (t < lo || t > hi) continue

    while (i + 1 < timeMs.length && timeMs[i + 1] < t) i++

    if (timeMs[i] === t) {
      out[g] = values[i]
      continue
    }

    const next = i + 1
    if (next >= timeMs.length) {
      out[g] = values[i]
      continue
    }
    if (timeMs[next] === t) {
      out[g] = values[next]
      continue
    }

    const span = timeMs[next] - timeMs[i]
    out[g] = span === 0 ? values[i] : values[i] + ((values[next] - values[i]) * (t - timeMs[i])) / span
  }

  return out
}
