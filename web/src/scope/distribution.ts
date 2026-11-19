/**
 * One measurement's readings as a distribution — how often each value came up.
 *
 * <p><b>What it answers that neither a column of statistics nor a trend can.</b> A mean and a
 * standard deviation describe a bell, and say the same thing about a reading that is <em>not</em>
 * one: two clusters a long way apart have a mean between them that no record ever read, and a
 * standard deviation that describes the gap rather than the spread. That is the signature of
 * something switching — a relay, a ranging step, a trigger finding two different edges — and on a
 * histogram it is unmistakable. A long tail on one side is the other shape worth recognising: a
 * reading that is usually fine and occasionally much worse is not a noisy reading.</p>
 *
 * <p><b>Here rather than in the component</b> for this project's usual reason — nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in — and because the bin
 * rule below is exactly the kind of choice that is wrong without being visibly wrong.</p>
 */

export interface DistributionBin {
  /** The half-open interval this bin counts, [from, to) — except the last, which includes its top. */
  from: number
  to: number
  count: number
}

export interface Distribution {
  bins: readonly DistributionBin[]
  /** The readings counted. Fewer than the series where records had no reading. */
  total: number
  /** The largest bin's count, which is what the bars are drawn against. */
  tallest: number
  min: number
  max: number
  mean: number
  /** True where every reading was the same figure, which has no spread to bin. */
  flat: boolean
}

/** The fewest bins a distribution is ever drawn in, and the most. */
export const MIN_DISTRIBUTION_BINS = 8
export const MAX_DISTRIBUTION_BINS = 48

/**
 * How many bins for a given number of readings.
 *
 * <p><b>The square-root rule, clamped</b>, rather than Freedman–Diaconis. FD is the better rule for
 * a well-behaved sample and is the wrong one here: its width is <code>2·IQR/∛n</code>, and the
 * readings this plot is most often of are a <em>steady</em> measurement, where the interquartile
 * range is a few counts of the last digit or exactly zero — which asks for a bin width of nothing
 * and therefore for an unbounded number of bins. Clamping FD's answer would leave the bin count
 * pinned at the clamp for precisely the commonest case, where it says nothing about the data at
 * all. The square root of n is monotone in n, never degenerate, and at the sizes available here —
 * one record to a thousand — gives 8 bins at the bottom and 32 at the top, which is the range a
 * histogram is legible in.</p>
 */
export function distributionBinCount(total: number): number {
  if (total <= 1) return 1
  return Math.min(MAX_DISTRIBUTION_BINS, Math.max(MIN_DISTRIBUTION_BINS, Math.ceil(Math.sqrt(total))))
}

/**
 * Bins a series.
 *
 * <p>A reading a record did not have (NaN) is <b>left out rather than binned</b>, which is the
 * opposite of the trend's rule and right for the same reason: a trend is against records, so an
 * absent reading is a place on its axis, where a distribution is against <em>value</em> and an
 * absent reading has none.</p>
 *
 * <p>Every reading the same figure answers <code>flat</code> rather than one bin of zero width:
 * a bar drawn in a window that is a single value has nowhere to be, and the honest thing to say is
 * that the measurement did not move. The one bin is still returned, so a caller that wants to draw
 * something has it.</p>
 */
export function distributionOf(series: ArrayLike<number>): Distribution | null {
  let min = Number.POSITIVE_INFINITY
  let max = Number.NEGATIVE_INFINITY
  let sum = 0
  let total = 0

  for (let i = 0; i < series.length; i += 1) {
    const v = series[i]
    if (!Number.isFinite(v)) continue
    total += 1
    sum += v
    if (v < min) min = v
    if (v > max) max = v
  }

  if (total === 0) return null

  const mean = sum / total

  if (!(max > min)) {
    return {
      bins: [{ from: min, to: max, count: total }],
      total,
      tallest: total,
      min,
      max,
      mean,
      flat: true,
    }
  }

  const count = distributionBinCount(total)
  const width = (max - min) / count
  const bins: DistributionBin[] = []
  for (let i = 0; i < count; i += 1) bins.push({ from: min + width * i, to: min + width * (i + 1), count: 0 })

  for (let i = 0; i < series.length; i += 1) {
    const v = series[i]
    if (!Number.isFinite(v)) continue
    // Floored into a bin, with the top of the range folded into the last one: without that, the
    // single largest reading falls one past the end and the maximum is the one value a histogram
    // of the data does not contain.
    let index = Math.floor((v - min) / width)
    if (index < 0) index = 0
    if (index >= count) index = count - 1
    bins[index].count += 1
  }

  let tallest = 0
  for (const bin of bins) if (bin.count > tallest) tallest = bin.count

  return { bins, total, tallest, min, max, mean, flat: false }
}
