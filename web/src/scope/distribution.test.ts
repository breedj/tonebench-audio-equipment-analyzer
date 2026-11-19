import { describe, expect, it } from 'vitest'
import {
  distributionBinCount,
  distributionOf,
  MAX_DISTRIBUTION_BINS,
  MIN_DISTRIBUTION_BINS,
} from './distribution'

describe('distributionBinCount', () => {
  it('is the square root of the count, clamped at both ends', () => {
    expect(distributionBinCount(1)).toBe(1)
    expect(distributionBinCount(4)).toBe(MIN_DISTRIBUTION_BINS)
    expect(distributionBinCount(100)).toBe(10)
    expect(distributionBinCount(1024)).toBe(32)
    expect(distributionBinCount(1_000_000)).toBe(MAX_DISTRIBUTION_BINS)
  })

  it('never decreases as more readings arrive', () => {
    let previous = 0
    for (const n of [1, 2, 8, 20, 64, 200, 1024]) {
      const bins = distributionBinCount(n)
      expect(bins).toBeGreaterThanOrEqual(previous)
      previous = bins
    }
  })
})

describe('distributionOf', () => {
  it('counts every reading exactly once', () => {
    const d = distributionOf([1, 2, 3, 4, 5, 6, 7, 8, 9, 10])!

    expect(d.total).toBe(10)
    expect(d.bins.reduce((sum, b) => sum + b.count, 0)).toBe(10)
  })

  /**
   * The largest reading falls one past the end of the last bin by the arithmetic, so without the
   * fold the maximum is the one value a histogram of the data does not contain.
   */
  it('puts the largest reading in the last bin rather than past the end', () => {
    const d = distributionOf([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10])!

    expect(d.bins[d.bins.length - 1].count).toBeGreaterThan(0)
    expect(d.bins.reduce((sum, b) => sum + b.count, 0)).toBe(11)
  })

  it('spans exactly the readings', () => {
    const d = distributionOf([2, 5, 11])!

    expect(d.min).toBe(2)
    expect(d.max).toBe(11)
    expect(d.bins[0].from).toBe(2)
    expect(d.bins[d.bins.length - 1].to).toBeCloseTo(11, 12)
  })

  /** The opposite of the trend's rule, and for the opposite reason: a value axis has no place for an absent reading. */
  it('leaves a record with no reading out rather than binning it', () => {
    const d = distributionOf([1, Number.NaN, 2, Number.NaN, 3])!

    expect(d.total).toBe(3)
    expect(d.bins.reduce((sum, b) => sum + b.count, 0)).toBe(3)
  })

  it('reads nothing finite as no distribution at all', () => {
    expect(distributionOf([])).toBeNull()
    expect(distributionOf([Number.NaN, Number.NaN])).toBeNull()
  })

  it('calls a reading that never moved flat rather than binning a window of no width', () => {
    const d = distributionOf([0.25, 0.25, 0.25])!

    expect(d.flat).toBe(true)
    expect(d.bins).toHaveLength(1)
    expect(d.bins[0].count).toBe(3)
    expect(d.mean).toBe(0.25)
  })

  /**
   * The shape this plot exists to show: two clusters whose mean no record ever read. The statistics
   * column reports that mean and a standard deviation that describes the gap between them.
   */
  it('shows two clusters as two groups of bins with nothing between them', () => {
    const series = [...Array(40).fill(1.0), ...Array(40).fill(2.0)]

    const d = distributionOf(series)!

    expect(d.flat).toBe(false)
    expect(d.bins[0].count).toBe(40)
    expect(d.bins[d.bins.length - 1].count).toBe(40)
    const middle = d.bins.slice(1, -1).reduce((sum, b) => sum + b.count, 0)
    expect(middle).toBe(0)
    // And the figure a statistics column would quote sits in the empty middle.
    expect(d.mean).toBeCloseTo(1.5, 12)
  })

  it('reports the tallest bin, which is what the bars are drawn against', () => {
    const d = distributionOf([...Array(30).fill(1.0), 2.0])!

    expect(d.tallest).toBe(30)
  })
})
