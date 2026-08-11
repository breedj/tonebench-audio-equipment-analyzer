// Pure, Vue-free resampling for overlaying a stored trace onto the live chart's frequency grid.
// uPlot draws one x array per plot (see SpectrumChart.vue), so a stored trace —
// its own FFT size, sample rate, possibly its own zoom state — has to be re-expressed on
// whichever x axis is currently on screen before it can share a series with the live data.

export interface Trace {
  freqHz: ArrayLike<number>
  value: ArrayLike<number>
}

/**
 * Resamples `source` onto `targetFreqHz` by linear interpolation in log-frequency — the natural
 * choice since the chart's x axis is itself log-frequency, so this is what draws as a straight
 * line between two known points on screen. `source.freqHz` must be sorted ascending (true of
 * both the live chart's decimated columns and a stored trace's bins).
 *
 * Points outside source's range clamp to the nearest edge value (flat extrapolation) rather than
 * projecting a slope, which would invent detail the trace never measured.
 */
export function resample(source: Trace, targetFreqHz: ArrayLike<number>): Float64Array {
  const n = source.freqHz.length
  const result = new Float64Array(targetFreqHz.length)
  if (n === 0) return result

  if (n === 1) {
    result.fill(source.value[0])
    return result
  }

  const logFreq = new Float64Array(n)
  for (let i = 0; i < n; i++) logFreq[i] = Math.log(Math.max(source.freqHz[i], Number.EPSILON))

  for (let t = 0; t < targetFreqHz.length; t++) {
    const targetLog = Math.log(Math.max(targetFreqHz[t], Number.EPSILON))

    if (targetLog <= logFreq[0]) {
      result[t] = source.value[0]
      continue
    }
    if (targetLog >= logFreq[n - 1]) {
      result[t] = source.value[n - 1]
      continue
    }

    // Binary search for the bracketing pair.
    let lo = 0
    let hi = n - 1
    while (hi - lo > 1) {
      const mid = (lo + hi) >> 1
      if (logFreq[mid] <= targetLog) lo = mid
      else hi = mid
    }

    const span = logFreq[hi] - logFreq[lo]
    const frac = span > 0 ? (targetLog - logFreq[lo]) / span : 0
    result[t] = source.value[lo] + frac * (source.value[hi] - source.value[lo])
  }

  return result
}
