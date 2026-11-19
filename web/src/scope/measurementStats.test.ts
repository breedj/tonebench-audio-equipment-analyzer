import { describe, expect, it } from 'vitest'
import {
  addStatSample,
  newStatAccumulator,
  SCOPE_SERIES_CAPACITY,
  SCOPE_STAT_COLUMNS,
  seriesFirstRecord,
  seriesOf,
  statOf,
} from './measurementStats'

function fold(values: readonly (number | null)[]) {
  const acc = newStatAccumulator()
  for (const v of values) addStatSample(acc, v)
  return statOf(acc)
}

describe('addStatSample', () => {
  it('answers nothing before it has seen anything', () => {
    expect(statOf(newStatAccumulator())).toBeNull()
    expect(statOf(undefined)).toBeNull()
  })

  it('counts, averages and bounds what it is given', () => {
    const s = fold([1, 2, 3, 4])
    expect(s).not.toBeNull()
    expect(s!.count).toBe(4)
    expect(s!.mean).toBeCloseTo(2.5, 12)
    expect(s!.min).toBe(1)
    expect(s!.max).toBe(4)
    expect(s!.peakToPeak).toBe(3)
    expect(s!.last).toBe(4)
  })

  it('takes the population deviation, so one record reads zero rather than nothing', () => {
    // n − 1 would make a single reading's spread undefined, and the table would have to print a
    // dash beside a count of 1 — which reads as a failure rather than as "it has not moved yet".
    expect(fold([5])!.stdev).toBe(0)
    // Four values 1..4 about a mean of 2.5: sqrt((2.25+0.25+0.25+2.25)/4).
    expect(fold([1, 2, 3, 4])!.stdev).toBeCloseTo(Math.sqrt(1.25), 12)
  })

  it('keeps a small spread on a large value, where the textbook one-pass form loses it', () => {
    // THE reason this is Welford's. These are the Siglent screenshot's own figures: a 306.864 mV
    // peak-to-peak wandering by a few hundred microvolts. sumSq/n − mean² has the two terms
    // agreeing to six digits and their difference in the rounding, which at this ratio is most of
    // the answer; here it has to be exact to the twelfth place.
    const base = 0.306_864
    const values = [base - 0.000_757, base, base + 0.000_757, base - 0.000_3, base + 0.000_3]
    const s = fold(values)!
    const mean = values.reduce((a, b) => a + b, 0) / values.length
    const variance = values.reduce((a, b) => a + (b - mean) ** 2, 0) / values.length
    expect(s.mean).toBeCloseTo(mean, 15)
    expect(s.stdev).toBeCloseTo(Math.sqrt(variance), 15)
    expect(s.stdev).toBeGreaterThan(0)
  })

  it('holds up on a value whose offset dwarfs its spread', () => {
    // The same cancellation one step further on: a reading sitting at 1000 that moves by a
    // thousandth. The naive form is pure rounding here.
    const values = [1000.001, 1000.002, 1000.003, 1000.002, 1000.001]
    const s = fold(values)!
    const mean = values.reduce((a, b) => a + b, 0) / values.length
    const variance = values.reduce((a, b) => a + (b - mean) ** 2, 0) / values.length
    expect(s.mean).toBeCloseTo(mean, 12)
    // Eleven significant figures against the two-pass answer. The naive one-pass form here is
    // subtracting 1000000.0000036 from 1000000.000004, i.e. mostly rounding.
    expect(Math.abs(s.stdev / Math.sqrt(variance) - 1)).toBeLessThan(1e-10)
    // And it is the real spread: about 750 µV on a value of 1000.
    expect(s.stdev).toBeGreaterThan(5e-4)
  })

  it('ignores a reading the waveform did not have rather than counting it as zero', () => {
    // A record with no second crossing has no period, and one zero in a mean of microseconds is
    // not a smaller figure, it is a wrong one. The count then says how many records had it, which
    // is itself worth reading.
    const s = fold([2, null, 4, Number.NaN as unknown as number, 6])!
    expect(s.count).toBe(3)
    expect(s.mean).toBe(4)
    expect(s.min).toBe(2)
    expect(s.max).toBe(6)
  })

  it('answers nothing at all when every reading was absent', () => {
    expect(fold([null, null])).toBeNull()
  })

  it('reports the newest value, so the Value column and its statistics are of one record', () => {
    expect(fold([1, 2, 3])!.last).toBe(3)
  })
})

describe('SCOPE_STAT_COLUMNS', () => {
  it('is the set a bench instrument prints, in its order', () => {
    expect(SCOPE_STAT_COLUMNS.map((c) => c.label)).toEqual(['Mean', 'Min', 'Max', 'Pk-Pk', 'Stdev'])
  })

  it('names a field every statistic actually has', () => {
    const s = fold([1, 2, 3])!
    for (const column of SCOPE_STAT_COLUMNS) {
      expect(Number.isFinite(s[column.key])).toBe(true)
    }
  })
})

describe('the kept series', () => {
  function series(values: readonly (number | null)[]) {
    const acc = newStatAccumulator()
    for (const v of values) addStatSample(acc, v)
    return acc
  }

  it('keeps the readings in the order they were folded in', () => {
    expect(Array.from(seriesOf(series([1, 2, 3])))).toEqual([1, 2, 3])
  })

  /**
   * The rule the trend rests on: a record with no reading keeps its place as a gap, so the series
   * is against records rather than against its own non-empty subset.
   */
  it('keeps a record with no reading as a gap rather than skipping it', () => {
    const kept = Array.from(seriesOf(series([1, null, 3])))

    expect(kept).toHaveLength(3)
    expect(kept[0]).toBe(1)
    expect(Number.isNaN(kept[1])).toBe(true)
    expect(kept[2]).toBe(3)
  })

  it('does not let that gap into the statistics', () => {
    const acc = series([1, null, 3])

    expect(statOf(acc)!.count).toBe(2)
    expect(statOf(acc)!.mean).toBe(2)
  })

  it('is empty before anything has been folded in', () => {
    expect(seriesOf(newStatAccumulator())).toHaveLength(0)
    expect(seriesOf(undefined)).toHaveLength(0)
    expect(seriesFirstRecord(newStatAccumulator())).toBe(0)
  })

  it('counts from record one until it wraps', () => {
    expect(seriesFirstRecord(series([1, 2, 3]))).toBe(1)
  })

  /** The bound is what makes a long run affordable; what it costs is the start of the run. */
  it('keeps the newest readings once it is full, and says which record they start at', () => {
    const acc = newStatAccumulator()
    const total = SCOPE_SERIES_CAPACITY + 10
    for (let i = 1; i <= total; i += 1) addStatSample(acc, i)

    const kept = seriesOf(acc)
    expect(kept).toHaveLength(SCOPE_SERIES_CAPACITY)
    expect(kept[0]).toBe(11)
    expect(kept[kept.length - 1]).toBe(total)
    expect(seriesFirstRecord(acc)).toBe(11)
  })

  /** Every record counts, including the ones the ring has dropped. */
  it('goes on counting records the series no longer keeps', () => {
    const acc = newStatAccumulator()
    const total = SCOPE_SERIES_CAPACITY + 10
    for (let i = 1; i <= total; i += 1) addStatSample(acc, i)

    expect(statOf(acc)!.count).toBe(total)
    expect(statOf(acc)!.min).toBe(1)
  })

  /** A caller that held the ring itself would be reading a buffer the next record overwrites. */
  it('hands out a copy rather than the ring', () => {
    const acc = series([1, 2, 3])
    const kept = seriesOf(acc)

    addStatSample(acc, 4)

    expect(Array.from(kept)).toEqual([1, 2, 3])
  })
})
