import { describe, expect, it } from 'vitest'
import {
  MAX_PHASE_TICKS,
  MIN_PHASE_SPAN_DEG,
  phaseAxisSplits,
  phaseScaleRange,
  phaseWindow,
} from './phaseAxis'

describe('phaseScaleRange', () => {
  it('puts the window in the top half of the plot', () => {
    // The ask (2026-10-22): the axis starts at the top and ends half way down. So the scale spans
    // twice the window, with the window's own top as the scale's top -- the lower half is empty.
    expect(phaseScaleRange(-90, 90)).toEqual([-270, 90])

    const [min, max] = phaseScaleRange(-90, 90)
    const middle = (min + max) / 2
    expect(middle).toBe(-90) // the window's bottom lands exactly half way down
  })

  it('works for a window that does not straddle zero', () => {
    expect(phaseScaleRange(0, 90)).toEqual([-90, 90])
    expect(phaseScaleRange(-90, -30)).toEqual([-150, -30])
  })

  it('guards a window with no height rather than blanking the plot', () => {
    // Top at or below Bottom is reachable by typing, and a zero-height window doubled is still zero:
    // uPlot would have nothing to map onto and the whole chart would come back empty.
    const [min, max] = phaseScaleRange(90, 90)
    expect(max - min).toBe(2 * MIN_PHASE_SPAN_DEG)
    expect(max).toBe(90)

    const [reversedMin, reversedMax] = phaseScaleRange(90, -90)
    expect(reversedMax - reversedMin).toBe(2 * MIN_PHASE_SPAN_DEG)
  })

  it('survives a non-finite bound', () => {
    // A cleared number box reads NaN through v-model.number, and NaN in a uPlot scale blanks it.
    expect(phaseScaleRange(Number.NaN, Number.NaN)).toEqual([-270, 90])
  })
})

describe('phaseAxisSplits', () => {
  it('labels the default window at quarters of a right angle', () => {
    expect(phaseAxisSplits(-90, 90)).toEqual([-90, -45, 0, 45, 90])
  })

  it('never labels the empty lower half of the scale', () => {
    // The whole reason the splits are generated here: uPlot's own would spread ticks across the
    // doubled scale, i.e. over phases nothing can be plotted at.
    const [scaleMin] = phaseScaleRange(-90, 90)
    for (const value of phaseAxisSplits(-90, 90)) {
      expect(value).toBeGreaterThanOrEqual(-90)
      expect(value).toBeGreaterThan(scaleMin)
      expect(value).toBeLessThanOrEqual(90)
    }
  })

  it('keeps the label count readable at any window', () => {
    for (const [bottom, top] of [
      [-90, 90],
      [-180, 180],
      [0, 90],
      [-45, 45],
      [-10, 10],
      [-5, 25],
      [-360, 360],
    ] as const) {
      const splits = phaseAxisSplits(bottom, top)
      expect(splits.length).toBeGreaterThan(0)
      expect(splits.length).toBeLessThanOrEqual(MAX_PHASE_TICKS)
    }
  })

  it('prints round numbers rather than floating-point residue', () => {
    // The step is accumulated, so without the rounding a 7.5 degree step reads 22.500000000000004.
    for (const value of phaseAxisSplits(-15, 15)) {
      expect(String(value)).not.toContain('0000')
    }
  })

  it('labels something even for a window narrower than the finest step', () => {
    const splits = phaseAxisSplits(0.2, 0.4)
    expect(splits.length).toBeGreaterThan(0)
  })
})

describe('phaseWindow', () => {
  it('is the pair the chart honours, which the scale and the ticks both read', () => {
    // One source for both, so a guarded window cannot have its ticks drawn against a different
    // window than its scale.
    expect(phaseWindow(-90, 90)).toEqual([-90, 90])

    const [bottom, top] = phaseWindow(90, 90)
    const splits = phaseAxisSplits(90, 90)
    for (const value of splits) {
      expect(value).toBeGreaterThanOrEqual(bottom)
      expect(value).toBeLessThanOrEqual(top)
    }
  })
})
