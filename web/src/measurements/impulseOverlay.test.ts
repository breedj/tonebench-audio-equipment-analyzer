import { describe, expect, it } from 'vitest'
import { impulseTimeGrid, resampleImpulse } from './impulseOverlay'

describe('impulseTimeGrid', () => {
  // The ordinary case: two runs of one measurement share a gate, so there is nothing to merge and
  // nothing is resampled at all.
  it('is the live grid untouched when an overlay covers the same span', () => {
    const live = [-1, 0, 1, 2]

    expect(impulseTimeGrid(live, [[-1, 0, 1, 2]])).toEqual(live)
    expect(impulseTimeGrid(live, [[-0.5, 0.5, 1.5]])).toEqual(live)
  })

  it('adds only the overlay columns the live span cannot reach', () => {
    expect(impulseTimeGrid([-1, 0, 1], [[-3, -2, 0.5, 2, 3]])).toEqual([-3, -2, -1, 0, 1, 2, 3])
  })

  it('merges the overlays themselves when there is no live trace', () => {
    expect(impulseTimeGrid([], [[0, 2], [1, 2, 3]])).toEqual([0, 1, 2, 3])
  })

  it('has no duplicates and is ascending', () => {
    const grid = impulseTimeGrid([0, 1], [[-1, -1, 2], [-1, 2]])

    expect(grid).toEqual([-1, 0, 1, 2])
  })

  it('is empty when there is nothing to draw', () => {
    expect(impulseTimeGrid([], [])).toEqual([])
  })
})

describe('resampleImpulse', () => {
  // The property the grid rule exists to guarantee: on its own columns a trace is not approximated.
  it('returns a curve unchanged on its own grid', () => {
    const timeMs = [-1, 0, 1, 2]
    const values = [0.1, 1, -0.4, 0.05]

    expect(resampleImpulse(timeMs, values, timeMs)).toEqual(values)
  })

  it('interpolates linearly between samples', () => {
    expect(resampleImpulse([0, 2], [0, 10], [0, 0.5, 1, 1.5, 2])).toEqual([0, 2.5, 5, 7.5, 10])
  })

  it('is null outside the span it measured, rather than holding its ends', () => {
    expect(resampleImpulse([0, 1], [4, 8], [-1, 0, 1, 2])).toEqual([null, 4, 8, null])
  })

  it('is all null for a curve with no samples', () => {
    expect(resampleImpulse([], [], [0, 1])).toEqual([null, null])
  })

  it('is all null when the arrays disagree, rather than reading past one of them', () => {
    expect(resampleImpulse([0, 1, 2], [1, 2], [0, 1])).toEqual([null, null])
  })

  // A live trace and a wider overlay, end to end through both functions: the live values survive
  // exactly, the overlay is sampled onto them, and neither invents data outside its own span.
  it('puts a wider overlay and the live trace on one axis without distorting the live one', () => {
    const liveTimeMs = [0, 1, 2]
    const liveValues = [1, 0.5, 0.25]
    const overlayTimeMs = [-1, 0.5, 3]
    const overlayValues = [10, 20, 30]

    const grid = impulseTimeGrid(liveTimeMs, [overlayTimeMs])

    expect(grid).toEqual([-1, 0, 1, 2, 3])
    expect(resampleImpulse(liveTimeMs, liveValues, grid)).toEqual([null, 1, 0.5, 0.25, null])

    const overlay = resampleImpulse(overlayTimeMs, overlayValues, grid)
    expect(overlay[0]).toBe(10)
    expect(overlay[4]).toBe(30)
    // t = 0 sits two thirds of the way from -1 to 0.5, so 10 + 20/3.
    expect(overlay[1]).toBeCloseTo(50 / 3, 9)
  })
})
