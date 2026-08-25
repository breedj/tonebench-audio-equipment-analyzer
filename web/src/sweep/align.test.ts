import { describe, expect, it } from 'vitest'
import { interpolateOnGrid } from './align'

describe('interpolateOnGrid', () => {
  it('interpolateOnGrid_OwnGrid_IsIdentity', () => {
    const grid = [100, 200, 400]

    const result = interpolateOnGrid(grid, [0, 1, 2], [1, 2, 3], false)

    expect(result).toEqual([1, 2, 3])
  })

  it('interpolateOnGrid_InterleavedGrids_LeavesNoGapsBetweenMeasuredPoints', () => {
    // The reported bug: two sweeps whose frequencies never coincide. The union grid alternates
    // between them, so an exact-lookup fill left every series as value/null/value/null and uPlot
    // drew disconnected stubs instead of a line.
    const grid = [100, 141, 200, 283, 400]

    const result = interpolateOnGrid(grid, [0, 2, 4], [10, 20, 30], false)

    expect(result.every((v) => v != null)).toBe(true)
  })

  it('interpolateOnGrid_FilledSlots_SitOnTheLineBetweenNeighbours', () => {
    // A filled slot must land where the chart would have drawn the segment anyway, so densifying a
    // trace never changes its shape: log in x (the axis is log-frequency), and log in y when the
    // percent axis is in use.
    const grid = [100, Math.sqrt(100 * 10000), 10000]

    const linear = interpolateOnGrid(grid, [0, 2], [-40, -20], false)
    const logY = interpolateOnGrid(grid, [0, 2], [0.001, 0.1], true)

    expect(linear[1]).toBeCloseTo(-30, 9)
    expect(logY[1] as number).toBeCloseTo(0.01, 9)
  })

  it('interpolateOnGrid_OutsideTheMeasuredRange_StaysNull', () => {
    // A sweep that stopped at 5 kHz must not sprout a flat line out to 20 kHz just because an
    // overlay went that far.
    const grid = [20, 100, 5000, 20000]

    const result = interpolateOnGrid(grid, [1, 2], [1, 2], false)

    expect(result[0]).toBeNull()
    expect(result[3]).toBeNull()
  })

  it('interpolateOnGrid_GapAtAMeasuredFrequency_IsNotBridged', () => {
    // A null anchor is a real "this value has no position on the axis" (toY's non-positive
    // percentage), which has to keep breaking the line rather than being interpolated across.
    const grid = [100, 141, 200, 283, 400]

    const result = interpolateOnGrid(grid, [0, 2, 4], [10, null, 30], false)

    expect(result).toEqual([10, null, null, null, 30])
  })

  it('interpolateOnGrid_NoAnchors_IsAllNull', () => {
    const result = interpolateOnGrid([100, 200], [], [], false)

    expect(result).toEqual([null, null])
  })
})
