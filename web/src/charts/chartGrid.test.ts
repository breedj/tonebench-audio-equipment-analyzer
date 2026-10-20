import { describe, expect, it } from 'vitest'
import { measuredColumns, mergeGrids } from './chartGrid'

/** A log-spaced grid, the shape both a decimated curve and a stepped sweep produce. */
function logGrid(lowHz: number, highHz: number, count: number): number[] {
  const step = Math.log10(highHz / lowHz) / (count - 1)
  return Array.from({ length: count }, (_, i) => lowHz * Math.pow(10, i * step))
}

describe('mergeGrids', () => {
  it('keeps an overlay at its own resolution inside the measured span', () => {
    // The report: a 50-point stepped sweep part way through, with a 1600-column chirp checked on.
    // Resampling the stored curve onto the live grid alone point-sampled it at the sweep's own
    // frequencies, so the half of it below the sweep's reach was drawn smooth and the half above it
    // ragged -- the same curve, two resolutions, with the boundary walking up the chart.
    const stepped = logGrid(20, 400, 21)
    const chirp = logGrid(13, 22800, 1600)

    const grid = mergeGrids(stepped, [chirp])

    const inside = [...grid].filter((f) => f >= 20 && f <= 400)
    const storedInside = chirp.filter((f) => f >= 20 && f <= 400)
    expect(inside.length).toBeGreaterThanOrEqual(storedInside.length)
  })

  it('reaches past the measured span at both ends', () => {
    const grid = mergeGrids([100, 200, 300], [[10, 150, 5000]])
    expect(grid[0]).toBe(10)
    expect(grid[grid.length - 1]).toBe(5000)
  })

  it('is sorted and free of repeats', () => {
    const grid = mergeGrids([100, 200, 300], [[300, 200, 100], [50, 200, 400]])
    expect([...grid]).toEqual([50, 100, 200, 300, 400])
  })

  it('leaves the measured grid alone when nothing is overlaid', () => {
    const measured = [20, 40, 80]
    expect([...mergeGrids(measured, [])]).toEqual(measured)
    expect([...mergeGrids(measured, [[], []])]).toEqual(measured)
  })

  it('falls back to an overlay when nothing has been measured', () => {
    // Checking a stored result on before running anything is a normal thing to do, and without this
    // the chart has no x axis to resample onto and silently draws nothing.
    expect([...mergeGrids([], [[], [10, 100, 1000]])]).toEqual([10, 100, 1000])
    expect([...mergeGrids([], [])]).toEqual([])
  })
})

describe('measuredColumns', () => {
  it('finds every measured frequency among the overlay columns', () => {
    const measured = [20, 200, 2000]
    const grid = mergeGrids(measured, [[10, 50, 200, 900, 9000]])

    const columns = measuredColumns(grid, measured)

    expect(columns).toHaveLength(measured.length)
    expect(columns.map((i) => grid[i])).toEqual(measured)
  })

  it('is every column when the grid is the measured grid', () => {
    const measured = [20, 40, 80]
    expect(measuredColumns(mergeGrids(measured, []), measured)).toEqual([0, 1, 2])
  })

  it('is empty with nothing measured', () => {
    expect(measuredColumns([10, 100, 1000], [])).toEqual([])
  })

  it('skips a frequency the grid does not hold rather than guessing at a column', () => {
    expect(measuredColumns([10, 100, 1000], [10, 500, 1000])).toEqual([0])
  })
})
