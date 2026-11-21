import { describe, expect, it } from 'vitest'
import { onScopeGrid, referenceInk } from './references'

describe('onScopeGrid', () => {
  it('puts a stored capture of another sample rate on the grid by time, not by index', () => {
    // The regression this module exists for: a 48 kS/s reference beside a 192 kS/s live record
    // holds a quarter as many samples over the same 1 ms window. Copied across by index it would
    // occupy the left quarter of the chart and read as a DUT four times faster than it is.
    const reference = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]
    const source = { x0S: 0, dxS: 1 / 48_000, length: reference.length }
    const grid = { x0S: 0, dxS: 1 / 192_000, count: 44 }

    const on = onScopeGrid(reference, source, grid, 'interpolate')

    // Every fourth output lands exactly on a source sample, and the stretch between them rises
    // linearly rather than being compressed.
    expect(on[0]).toBeCloseTo(0, 12)
    expect(on[4]).toBeCloseTo(1, 12)
    expect(on[40]).toBeCloseTo(10, 12)
    expect(on[2]).toBeCloseTo(0.5, 12)
    expect(on[43]).toBeCloseTo(10.75, 12)
  })

  it('leaves an identical grid exactly as it was', () => {
    // The ordinary case: one window, one column budget, one sample rate. There is no special case
    // for it in the code, so this is what says the general path does not disturb it.
    const values = [-0.25, 0.5, 0.75, -1]
    const source = { x0S: -0.005, dxS: 1 / 192_000, length: values.length }
    const grid = { x0S: -0.005, dxS: 1 / 192_000, count: values.length }

    for (const mode of ['interpolate', 'nearest'] as const) {
      expect(onScopeGrid(values, source, grid, mode)).toEqual(values)
    }
  })

  it('answers null outside the stored capture rather than zero or the nearest end', () => {
    // A reference that covers less of the window than the live trace has nothing to say there. A
    // flat line along zero, or the last sample held, would both be read as a measurement.
    const values = [1, 2, 3]
    const source = { x0S: 0, dxS: 0.001, length: values.length }
    const grid = { x0S: -0.002, dxS: 0.001, count: 7 }

    const on = onScopeGrid(values, source, grid, 'interpolate')

    expect(on).toEqual([null, null, 1, 2, 3, null, null])
  })

  it('keeps the trigger at the same place when the two records are offset', () => {
    // Both axes are labelled against the trigger, so a reference whose pre-trigger stretch is
    // longer than the live one still has its own t = 0 at t = 0.
    const values = [0, 10, 20, 30, 40]
    const source = { x0S: -0.002, dxS: 0.001, length: values.length }
    const grid = { x0S: -0.001, dxS: 0.001, count: 3 }

    expect(onScopeGrid(values, source, grid, 'nearest')).toEqual([10, 20, 30])
  })

  it('takes a column extreme rather than averaging two of them', () => {
    // A min/max column is already an extreme over a stretch, so interpolating between two of them
    // invents a level the capture never reached -- which on a noise floor reads several dB quiet.
    const maxima = [1, 0, 1, 0]
    const source = { x0S: 0, dxS: 0.002, length: maxima.length }
    const grid = { x0S: 0, dxS: 0.001, count: 7 }

    const nearest = onScopeGrid(maxima, source, grid, 'nearest')
    const smoothed = onScopeGrid(maxima, source, grid, 'interpolate')

    expect(nearest).toEqual([1, 0, 0, 1, 1, 0, 0])
    // The value halfway between a 1 and a 0 column is 0.5 under interpolation and never appears
    // under nearest, which is the whole of the difference.
    expect(smoothed[1]).toBeCloseTo(0.5, 12)
    expect(nearest.some((v) => v === 0.5)).toBe(false)
  })

  it('allows half a source step of slack at either edge', () => {
    // A grid whose first column sits a rounding error outside the source must not be drawn as a
    // gap at the very edge of the window.
    const values = [5, 6]
    const source = { x0S: 0, dxS: 0.001, length: values.length }
    const grid = { x0S: -0.0004, dxS: 0.001, count: 2 }

    const on = onScopeGrid(values, source, grid, 'nearest')

    expect(on[0]).toBe(5)
    expect(on[1]).toBe(6)
  })

  it('answers a grid of nulls for a slice with nothing in it', () => {
    const grid = { x0S: 0, dxS: 0.001, count: 3 }

    expect(onScopeGrid(null, { x0S: 0, dxS: 0.001, length: 0 }, grid, 'interpolate')).toEqual([
      null,
      null,
      null,
    ])
    expect(onScopeGrid([1, 2], { x0S: 0, dxS: 0, length: 2 }, grid, 'interpolate')).toEqual([
      null,
      null,
      null,
    ])
  })

  it('never reads past the shorter of the array and the length it was given', () => {
    // The two can disagree on a hand-built or truncated response, and reading past the array is a
    // run of nulls in the middle of a trace rather than an error.
    const values = [1, 2]
    const source = { x0S: 0, dxS: 0.001, length: 5 }
    const grid = { x0S: 0, dxS: 0.001, count: 5 }

    expect(onScopeGrid(values, source, grid, 'nearest')).toEqual([1, 2, null, null, null])
  })

  it('skips a non-finite sample rather than drawing it', () => {
    const values = [1, Number.NaN, 3]
    const source = { x0S: 0, dxS: 0.001, length: values.length }
    const grid = { x0S: 0, dxS: 0.001, count: 3 }

    expect(onScopeGrid(values, source, grid, 'nearest')).toEqual([1, null, 3])
    // Under interpolation a NaN takes both columns that would have read through it, and the last
    // column -- which has no neighbour past it -- is the sample itself.
    expect(onScopeGrid(values, source, grid, 'interpolate')).toEqual([null, null, 3])
  })
})

describe('referenceInk', () => {
  /**
   * The point of the whole thing: a stored capture is never drawn in a live channel's colour.
   * It used to be the channel's own hue pulled toward a per-run tint, which on the first run is
   * blue pulled toward blue — so the dash was carrying the entire distinction.
   */
  it('is never one of the live channels’ colours', () => {
    const live = ['#3b82f6', '#f97316']
    for (const channel of [0, 1]) {
      for (const run of [1, 2, 3]) {
        expect(live).not.toContain(referenceInk(channel, run))
      }
    }
  })

  it('tells the two channels apart', () => {
    expect(referenceInk(0, 1)).not.toBe(referenceInk(1, 1))
  })

  it('tells one run from the next', () => {
    const inks = [1, 2, 3].map((run) => referenceInk(0, run))
    expect(new Set(inks).size).toBe(3)
  })

  /** Lighter per run, and bounded, so the third is still legible on white. */
  it('lifts a later run toward white without washing it out', () => {
    const base = referenceInk(0, 1)
    const third = referenceInk(0, 3)
    expect(third).not.toBe(base)
    // Still clearly the same hue family: green stays the largest channel of the teal base.
    const [r, g, b] = [1, 3, 5].map((i) => parseInt(third.slice(i, i + 2), 16))
    expect(g).toBeGreaterThan(r)
    expect(g).toBeGreaterThan(120)
    expect(b).toBeLessThan(g)
  })

  it('reads an unknown channel as the left one', () => {
    expect(referenceInk(7, 1)).toBe(referenceInk(0, 1))
  })
})
