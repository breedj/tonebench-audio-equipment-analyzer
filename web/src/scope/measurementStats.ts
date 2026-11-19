/**
 * Statistics over a chosen measurement, accumulated across records.
 *
 * <p>What a single reading says is what this capture was; what a column of them says is whether the
 * DUT is steady. A peak-to-peak that reads 306 mV every record and one that reads 306 mV on average
 * while wandering over 3 mV are two different amplifiers, and nothing on a per-record display tells
 * them apart — which is the whole reason a bench instrument offers Mean, Min, Max, Pk-Pk, Stdev and
 * Count beside the value.</p>
 *
 * <p><b>Welford's method, and that is not a flourish.</b> The textbook one-pass form
 * <code>sumSq/n − mean²</code> is catastrophic cancellation whenever the spread is small against
 * the value — which is <em>exactly</em> the case this is built for: a 306.864 mV mean with a 757 µV
 * standard deviation has the two terms agreeing to six digits and their difference in the rounding.
 * This codebase has paid for that lesson twice already, in <code>WaveformMeasure</code>'s second
 * pass and in the band sums that once read −600 dB, and a second pass is not available here because
 * the records are seen one at a time and are not kept.</p>
 *
 * <p><b>Count is records the screen read, not records the analyzer committed.</b> A record is
 * fetched when the run's own counter moves, so every one counted here is a distinct capture — but
 * a run commits faster than a browser polls and the ones in between are never fetched. So this
 * counts what was looked at, which is the honest thing for it to count: every figure beside it is
 * of those same records.</p>
 */

/** One measurement's running statistics. All null-free: a stat with no sample is simply absent. */
export interface ScopeMeasurementStat {
  count: number
  mean: number
  min: number
  max: number
  /** Max less min — the spread, which is what a reader scans the column for. */
  peakToPeak: number
  /** The population standard deviation of the values seen. */
  stdev: number
  /** The newest value, so the table's Value column and its statistics cannot come from two records. */
  last: number
}

/** The accumulator itself. Mutated in place; one per measurement and source. */
export interface ScopeStatAccumulator {
  count: number
  mean: number
  /** Welford's running sum of squared deviations from the current mean. */
  m2: number
  min: number
  max: number
  last: number
}

export function newStatAccumulator(): ScopeStatAccumulator {
  return { count: 0, mean: 0, m2: 0, min: Number.POSITIVE_INFINITY, max: Number.NEGATIVE_INFINITY, last: 0 }
}

/**
 * Folds one reading in.
 *
 * <p>A non-finite value is <b>ignored rather than counted</b>: a measurement a waveform does not
 * have arrives as null, and one record that happened to have no period must not put a NaN into a
 * mean that every later record then inherits. The count then says how many records actually had
 * this reading, which is itself worth knowing — a frequency with a count well below its
 * neighbours' is a signal that keeps losing its second crossing.</p>
 */
export function addStatSample(acc: ScopeStatAccumulator, value: number | null | undefined): void {
  if (value === null || value === undefined || !Number.isFinite(value)) return

  acc.count += 1
  const delta = value - acc.mean
  acc.mean += delta / acc.count
  // The second factor uses the UPDATED mean, which is what makes this Welford's rather than a
  // plain running variance: the product of the two deltas is what keeps the accumulated sum from
  // ever being a difference of two large numbers.
  acc.m2 += delta * (value - acc.mean)
  if (value < acc.min) acc.min = value
  if (value > acc.max) acc.max = value
  acc.last = value
}

/** The accumulator read out, or null before it has seen anything. */
export function statOf(acc: ScopeStatAccumulator | undefined): ScopeMeasurementStat | null {
  if (!acc || acc.count === 0) return null
  return {
    count: acc.count,
    mean: acc.mean,
    min: acc.min,
    max: acc.max,
    peakToPeak: acc.max - acc.min,
    // Population rather than sample: these are every record that was looked at, not a sample drawn
    // from a larger set, and dividing by n − 1 would make a single record's deviation undefined
    // rather than zero.
    stdev: Math.sqrt(acc.m2 / acc.count),
    last: acc.last,
  }
}

/** The stat rows the table shows under a measurement, in the order a bench instrument lists them. */
export const SCOPE_STAT_COLUMNS: readonly {
  key: 'mean' | 'min' | 'max' | 'peakToPeak' | 'stdev'
  label: string
  title: string
}[] = Object.freeze([
  { key: 'mean', label: 'Mean', title: 'The average of this reading over every record counted.' },
  { key: 'min', label: 'Min', title: 'The smallest it has read.' },
  { key: 'max', label: 'Max', title: 'The largest.' },
  { key: 'peakToPeak', label: 'Pk-Pk', title: 'Max less min — how far this reading has wandered.' },
  {
    key: 'stdev',
    label: 'Stdev',
    title:
      'The standard deviation of the readings, accumulated by Welford’s method so a small spread on a large value does not disappear into rounding.',
  },
])
