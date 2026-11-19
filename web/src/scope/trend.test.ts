import { describe, expect, it } from 'vitest'
import { layOutTrend, trendAxis, trendTickLabel } from './trend'

const BOX = { left: 10, top: 20, width: 100, height: 50 }

describe('layOutTrend', () => {
  it('spreads one point per reading across the box', () => {
    const layout = layOutTrend([1, 2, 3], 1, BOX)

    expect(layout.segments).toHaveLength(1)
    const xs = layout.segments[0].points.map((p) => p.x)
    expect(xs).toEqual([10, 60, 110])
  })

  it('puts a lone reading in the middle rather than against the left edge', () => {
    const layout = layOutTrend([0.5], 1, BOX)

    expect(layout.segments[0].points[0].x).toBe(60)
  })

  /**
   * The rule the whole file exists for: a record with no reading is a break in the line. Joining
   * across it would draw a frequency that keeps losing its second crossing as a clean trace.
   */
  it('breaks the line at a record with no reading rather than joining across it', () => {
    const layout = layOutTrend([1, 2, Number.NaN, 4, 5], 1, BOX)

    expect(layout.segments).toHaveLength(2)
    expect(layout.segments[0].points).toHaveLength(2)
    expect(layout.segments[1].points).toHaveLength(2)
    expect(layout.gaps).toBe(1)
  })

  it('counts a leading and a trailing gap without emitting an empty segment', () => {
    const layout = layOutTrend([Number.NaN, 1, 2, Number.NaN], 1, BOX)

    expect(layout.segments).toHaveLength(1)
    expect(layout.gaps).toBe(2)
  })

  it('reads nothing at all as no segments and no mean', () => {
    const layout = layOutTrend([Number.NaN, Number.NaN], 7, BOX)

    expect(layout.segments).toHaveLength(0)
    expect(layout.meanY).toBeNull()
    expect(layout.band).toBeNull()
    expect(layout.gaps).toBe(2)
    expect(layout.firstRecord).toBe(7)
    expect(layout.lastRecord).toBe(8)
  })

  it('names the records at the two ends, so a bounded series says which thousand it is', () => {
    const layout = layOutTrend([1, 2, 3, 4], 8001, BOX)

    expect(layout.firstRecord).toBe(8001)
    expect(layout.lastRecord).toBe(8004)
  })

  /**
   * The y axis is fitted to the readings, so a reading that moved by a thousandth of itself fills
   * the plot. Fitted to zero it would be a flat line, which is the whole thing this plot is for.
   */
  it('fits the value axis to the readings rather than to zero', () => {
    const layout = layOutTrend([0.306864, 0.307621, 0.306107], 1, BOX)

    expect(layout.axis.min).toBeGreaterThan(0.3)
    expect(layout.axis.max).toBeLessThan(0.31)
    // Both extremes inside the box rather than on its frame.
    const ys = layout.segments[0].points.map((p) => p.y)
    expect(Math.min(...ys)).toBeGreaterThan(BOX.top)
    expect(Math.max(...ys)).toBeLessThan(BOX.top + BOX.height)
  })

  it('puts the mean where the mean is and the band one deviation either side', () => {
    // Two readings: mean half way, deviation half the gap, so the band's edges land on them.
    const layout = layOutTrend([1, 3], 1, BOX)

    const ys = layout.segments[0].points.map((p) => p.y)
    expect(layout.meanY).toBeCloseTo((ys[0] + ys[1]) / 2, 9)
    expect(layout.band!.top).toBeCloseTo(Math.min(...ys), 9)
    expect(layout.band!.bottom).toBeCloseTo(Math.max(...ys), 9)
  })

  /** The band is of the readings drawn, never of records the series no longer keeps. */
  it('takes the mean and the band over the readings it was given', () => {
    const layout = layOutTrend([2, 2, 2, Number.NaN], 1, BOX)

    const flat = layout.segments[0].points[0].y
    expect(layout.meanY).toBeCloseTo(flat, 9)
    expect(layout.band!.top).toBeCloseTo(flat, 9)
    expect(layout.band!.bottom).toBeCloseTo(flat, 9)
  })
})

describe('trendAxis', () => {
  it('opens a flat reading out by a few per cent rather than leaving no window at all', () => {
    const axis = trendAxis(2, 2)

    expect(axis.max).toBeGreaterThan(axis.min)
    expect(axis.min).toBeCloseTo(2 - 0.1, 9)
    expect(axis.max).toBeCloseTo(2 + 0.1, 9)
  })

  /** A reading of exactly zero every record has nothing to take a fraction of. */
  it('still has a window for a reading of exactly zero', () => {
    const axis = trendAxis(0, 0)

    expect(axis.max).toBeGreaterThan(axis.min)
    expect(Number.isFinite(axis.min)).toBe(true)
  })

  it('labels the bottom, the middle and the top', () => {
    const axis = trendAxis(0, 10)

    expect(axis.ticks).toHaveLength(3)
    expect(axis.ticks[0]).toBeCloseTo(axis.min, 9)
    expect(axis.ticks[1]).toBeCloseTo((axis.min + axis.max) / 2, 9)
    expect(axis.ticks[2]).toBeCloseTo(axis.max, 9)
  })
})

describe('trendTickLabel', () => {
  const volts = (v: number) => (Math.abs(v) < 1 ? `${(v * 1000).toFixed(3)} mV` : `${v.toFixed(3)} V`)

  /**
   * The reason the labels are offsets at all: four significant figures of a 1.419 V reading cannot
   * show the millivolt it wandered by, so all three ticks would print the same thing over a plot
   * whose whole subject is that spread.
   */
  it('writes the middle in full and the others against it', () => {
    expect(trendTickLabel(1.419, 1.419, volts)).toBe('1.419 V')
    expect(trendTickLabel(1.4202, 1.419, volts)).toBe('+1.200 mV')
    expect(trendTickLabel(1.4178, 1.419, volts)).toBe('−1.200 mV')
  })

  it('uses a true minus sign rather than a hyphen', () => {
    expect(trendTickLabel(0, 1, volts).startsWith('−')).toBe(true)
  })

  it('writes an offset of exactly nothing as the middle itself', () => {
    expect(trendTickLabel(2, 2, volts)).toBe('2.000 V')
  })
})
