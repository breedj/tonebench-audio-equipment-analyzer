import { describe, expect, it } from 'vitest'
import type { ScopeChannelSlice } from '../api/client'
import { recordExtremes, sliceExtremes } from './measurements'

function columns(min: number[], max: number[]): ScopeChannelSlice {
  return { channel: 0, x0: -0.005, dx: 1e-5, raw: false, samples: null, min, max }
}

function raw(samples: number[]): ScopeChannelSlice {
  return { channel: 0, x0: 0, dx: 1e-5, raw: true, samples, min: null, max: null }
}

describe('sliceExtremes', () => {
  it('reads both ends of a decimated slice', () => {
    expect(sliceExtremes(columns([-1, -0.5, -0.9], [1, 0.4, 0.95]))).toEqual({ maxV: 1, minV: -1, ppV: 2 })
  })

  it('reads the minima too, not just the maxima', () => {
    // Maxima alone read several dB quiet at a noise floor -- the rule the spectrum's decimator
    // carries -- and would frame a chart on half the trace.
    const extremes = sliceExtremes(columns([-3, -2], [0.5, 0.4]))
    expect(extremes!.minV).toBe(-3)
  })

  it('reads a raw slice sample for sample', () => {
    expect(sliceExtremes(raw([0, 0.75, -0.25]))).toEqual({ maxV: 0.75, minV: -0.25, ppV: 1 })
  })

  it('survives a hole in a column array', () => {
    const extremes = sliceExtremes(columns([-1, Number.NaN], [1, Number.NaN]))
    expect(extremes).toEqual({ maxV: 1, minV: -1, ppV: 2 })
  })

  it('is null for a channel that carries nothing, rather than reading zero', () => {
    // A zero would frame the chart on a trace that is not there and hand the 50 % button a level
    // as though it had measured one.
    expect(sliceExtremes(null)).toBeNull()
    expect(sliceExtremes(columns([], []))).toBeNull()
    expect(sliceExtremes(raw([]))).toBeNull()
    expect(sliceExtremes(raw([Number.NaN]))).toBeNull()
  })

  it('is null for a raw slice with no samples attached', () => {
    expect(sliceExtremes({ ...raw([]), samples: null })).toBeNull()
  })
})

describe('recordExtremes', () => {
  it('spans every channel, since one vertical setting serves the whole plot', () => {
    // Fitting to the left alone would push a louder right channel off the top of a window that
    // claims to show both.
    const left = columns([-1], [1])
    const right = { ...columns([-4], [2]), channel: 1 }
    expect(recordExtremes([left, right])).toEqual({ maxV: 2, minV: -4, ppV: 6 })
  })

  it('ignores a channel that carries nothing instead of being poisoned by it', () => {
    const left = columns([-1], [1])
    const right = { ...columns([], []), channel: 1 }
    expect(recordExtremes([left, right])).toEqual({ maxV: 1, minV: -1, ppV: 2 })
  })

  it('is null with no record at all', () => {
    expect(recordExtremes(null)).toBeNull()
    expect(recordExtremes([])).toBeNull()
  })
})
