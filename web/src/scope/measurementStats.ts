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
 *
 * <p><b>The readings are kept as well as summarised</b>, in a bounded ring per row, because the two
 * questions a column of figures cannot answer are the two a reader most wants: <em>when</em> did it
 * move, and <em>how</em> is it distributed. A mean and a standard deviation are the same for a
 * reading that drifted steadily across a run and for one that sat still and jumped once, and they
 * are the same again for a bell and for two clusters a long way apart. Both of those are one glance
 * at a plot. See <code>scope/trend.ts</code> and <code>scope/distribution.ts</code>, which draw
 * them.</p>
 */

/**
 * How many of a row's readings are kept for the trend and the distribution.
 *
 * <p>A bound rather than the whole run, because this is per row and a stereo capture with a dozen
 * measurements ticked is sixty-odd of them: at eight bytes a reading that is half a megabyte, which
 * is affordable, and unbounded it is a leak that grows for as long as a run is left going. The
 * statistics are <b>not</b> bounded by it — Count, Mean and the rest are of every record read,
 * which is why a plot drawn from fewer says so rather than pretending otherwise.</p>
 */
export const SCOPE_SERIES_CAPACITY = 1024

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
  /**
   * Every reading folded in, newest last, as a ring of <code>SCOPE_SERIES_CAPACITY</code>.
   *
   * <p><b>A record with no reading is written as NaN rather than skipped</b>, which is what makes
   * this a series against <em>records</em> instead of against its own non-empty subset. Skipping
   * would slide every later reading one place earlier, so a dip a reader is trying to line up with
   * something else in the run would be in the wrong place — and a frequency that keeps losing its
   * second crossing would draw as a clean line rather than as the gappy one it is.</p>
   */
  values: Float64Array
  /** How many readings have ever been folded in, so the ring's oldest slot is derivable. */
  written: number
}

export function newStatAccumulator(): ScopeStatAccumulator {
  return {
    count: 0,
    mean: 0,
    m2: 0,
    min: Number.POSITIVE_INFINITY,
    max: Number.NEGATIVE_INFINITY,
    last: 0,
    values: new Float64Array(SCOPE_SERIES_CAPACITY),
    written: 0,
  }
}

/**
 * Folds one reading in.
 *
 * <p>A non-finite value is <b>not counted, and still recorded</b>: a measurement a waveform does
 * not have arrives as null, and one record that happened to have no period must not put a NaN into
 * a mean that every later record then inherits — but it is a record all the same, so the series
 * keeps its place as a gap. The count then says how many records actually had this reading, which
 * is itself worth knowing: a frequency with a count well below its neighbours' is a signal that
 * keeps losing its second crossing.</p>
 */
export function addStatSample(acc: ScopeStatAccumulator, value: number | null | undefined): void {
  const finite = value !== null && value !== undefined && Number.isFinite(value)

  acc.values[acc.written % acc.values.length] = finite ? (value as number) : Number.NaN
  acc.written += 1

  if (!finite) return

  const v = value as number
  acc.count += 1
  const delta = v - acc.mean
  acc.mean += delta / acc.count
  // The second factor uses the UPDATED mean, which is what makes this Welford's rather than a
  // plain running variance: the product of the two deltas is what keeps the accumulated sum from
  // ever being a difference of two large numbers.
  acc.m2 += delta * (v - acc.mean)
  if (v < acc.min) acc.min = v
  if (v > acc.max) acc.max = v
  acc.last = v
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

/** One row's readings in the order they were taken, oldest first. NaN where a record had none. */
export function seriesOf(acc: ScopeStatAccumulator | undefined): Float64Array {
  if (!acc || acc.written === 0) return new Float64Array(0)

  const capacity = acc.values.length
  const kept = Math.min(acc.written, capacity)
  const out = new Float64Array(kept)

  // The oldest kept reading, which is the write head once the ring has wrapped and slot 0 before
  // it. Copied out rather than returned as a view, because a caller that held the ring itself
  // would be reading a buffer the next record overwrites under it.
  const oldest = (acc.written - kept) % capacity
  for (let i = 0; i < kept; i += 1) out[i] = acc.values[(oldest + i) % capacity]

  return out
}

/**
 * Which record the first kept reading was, counting from one.
 *
 * <p>So a plot of the last thousand of nine thousand records says <em>which</em> thousand. Without
 * it the x axis would read 1…1024 on a run that is nowhere near its start, which is the one way a
 * bounded series can mislead rather than merely omit.</p>
 */
export function seriesFirstRecord(acc: ScopeStatAccumulator | undefined): number {
  if (!acc || acc.written === 0) return 0
  return acc.written - Math.min(acc.written, acc.values.length) + 1
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
