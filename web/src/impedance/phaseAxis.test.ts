import { describe, expect, it } from 'vitest'
import {
  MAX_PHASE_TICKS,
  MIN_PHASE_SPAN_DEG,
  phaseAxisSplits,
  phaseInWindow,
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

  it('keeps a window inside one quadrant in the top half, ticks and all', () => {
    // Asked for 2026-10-22: the two boxes used to be held 90 deg apart, so the default +90/-90
    // window was also the narrowest one reachable -- a capacitor near -90 or a coil near +90 could
    // not be framed. The panels clamp to MIN_PHASE_SPAN_DEG now, so the geometry has to hold for a
    // window far narrower than the default.
    expect(phaseScaleRange(45, 90)).toEqual([0, 90])
    expect(phaseScaleRange(-90, -45)).toEqual([-135, -45])

    const splits = phaseAxisSplits(45, 90)
    expect(splits.length).toBeGreaterThan(1)
    for (const value of splits) {
      expect(value).toBeGreaterThanOrEqual(45)
      expect(value).toBeLessThanOrEqual(90)
    }
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

describe('phaseInWindow', () => {
  it('blanks the samples that would be drawn in the empty lower half', () => {
    // The report (Joost, 2026-10-22), from the output-impedance chart at 30/10: the phase swept up
    // from near zero, and everything below 10 deg was drawn through the |Z| half of the plot at a
    // height the degrees axis prints no label for.
    expect(phaseInWindow([2, 8, 12, 25, 31], 10, 30)).toEqual([null, null, 12, 25, null])
  })

  it('keeps a sample sitting exactly on either boundary', () => {
    // Autofit rounds the curve's extremes outward onto whole ticks, so this is the ordinary case
    // rather than an edge one: blanked here, the fitted window would open with a gap at each end.
    expect(phaseInWindow([10, 30], 10, 30)).toEqual([10, 30])
  })

  it('leaves a curve that fits inside the window untouched', () => {
    const values = [-89.5, -45, 0, 12.25, 90]
    expect(phaseInWindow(values, -90, 90)).toEqual(values)
  })

  it('carries a gap through as a gap, and never writes a zero', () => {
    // A null is "not measured here" and must stay one: drawn as 0 it would be a phase the DUT never
    // had, sitting in the middle of the window.
    expect(phaseInWindow([null, undefined, Number.NaN, 5], -90, 90)).toEqual([null, null, null, 5])
  })

  it('reads the window through the same guard the scale and the ticks do', () => {
    // A window with no height is widened to MIN_PHASE_SPAN_DEG for the scale, so it has to be
    // widened here too -- otherwise the axis would show a band the curve is blanked out of.
    const [bottom, top] = phaseWindow(90, 90)
    expect(top - bottom).toBe(MIN_PHASE_SPAN_DEG)
    expect(phaseInWindow([bottom, top, bottom - 1, top + 1], 90, 90)).toEqual([bottom, top, null, null])

    // And a cleared box falls back to +/-90 here as well, rather than blanking the whole curve.
    expect(phaseInWindow([-90, 0, 90], Number.NaN, Number.NaN)).toEqual([-90, 0, 90])
  })

  it('answers an array of the length it was given', () => {
    // uPlot pairs series with x by position, so a filtered-down array would shift every sample onto
    // a lower frequency than it was measured at.
    expect(phaseInWindow(new Array(7).fill(180), -90, 90)).toHaveLength(7)
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
