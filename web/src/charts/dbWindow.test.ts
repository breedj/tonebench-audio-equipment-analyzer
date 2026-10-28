import { describe, expect, it } from 'vitest'
import { fitDbWindow, type DbWindowBounds } from './dbWindow'

/** The crosstalk rail's own ladder: 5 dB steps, 2 dB of clearance, ±200 dB of travel. */
const CROSSTALK: DbWindowBounds = { step: 5, pad: 2, minSpan: 10, min: -200, max: 200 }

/** The Bode rail's: 2 dB steps, 1 dB of clearance. */
const RESPONSE: DbWindowBounds = { step: 2, pad: 1, minSpan: 4, min: -200, max: 200 }

describe('fitDbWindow', () => {
  it('frames a positive crosstalk curve, which is the window the old ceiling could not reach', () => {
    // The 2026-10-28 report, measured against the simulator with 60 dB of claimed external gain on
    // each input: a sweep reading +49.88 to +66.85 dB. A 40 dB ceiling clamped the top and nothing
    // on the chart was in view.
    expect(fitDbWindow(49.88, 66.85, CROSSTALK)).toEqual({ top: 70, bottom: 45 })
  })

  it('takes both bounds from the data, never from the window being replaced', () => {
    // The whole point of the function: the caller cannot reach a half-applied window, so a Top box
    // sitting anywhere at all gives the same answer.
    const fitted = fitDbWindow(49.88, 66.85, CROSSTALK)
    expect(fitted).toEqual({ top: 70, bottom: 45 })
    expect(fitDbWindow(-120, -64, CROSSTALK)).toEqual({ top: -60, bottom: -125 })
  })

  it('rounds outward, so no sample lands on an edge', () => {
    expect(fitDbWindow(-100, 0, CROSSTALK)).toEqual({ top: 5, bottom: -105 })
    expect(fitDbWindow(-100, 0, RESPONSE)).toEqual({ top: 2, bottom: -102 })
  })

  it('widens a window narrower than the minimum span', () => {
    const fitted = fitDbWindow(0.1, 0.2, RESPONSE)!
    expect(fitted.top - fitted.bottom).toBeGreaterThanOrEqual(RESPONSE.minSpan)
  })

  it('keeps the span legal when the ceiling binds', () => {
    const fitted = fitDbWindow(199.5, 400, CROSSTALK)!
    expect(fitted.top).toBe(CROSSTALK.max)
    expect(fitted.top - fitted.bottom).toBeGreaterThanOrEqual(CROSSTALK.minSpan)
  })

  it('keeps the span legal when the floor binds', () => {
    const fitted = fitDbWindow(-400, -199.5, CROSSTALK)!
    expect(fitted.bottom).toBe(CROSSTALK.min)
    expect(fitted.top - fitted.bottom).toBeGreaterThanOrEqual(CROSSTALK.minSpan)
  })

  it('leaves the axis alone when there was nothing to fit', () => {
    expect(fitDbWindow(Infinity, -Infinity, CROSSTALK)).toBeNull()
    expect(fitDbWindow(Number.NaN, 0, CROSSTALK)).toBeNull()
  })
})
