import { describe, expect, it } from 'vitest'
import { autofitRange, autofitRangeInBand } from './autofitRange'

describe('autofitRange', () => {
  it('brackets the data on the requested grid', () => {
    expect(autofitRange([-3.2, 0.4, -11.7])).toEqual({ top: 5, bottom: -15 })
  })

  it('returns null when there is nothing finite to fit', () => {
    expect(autofitRange([])).toBeNull()
    expect(autofitRange([Number.NaN, Number.POSITIVE_INFINITY])).toBeNull()
  })

  /**
   * Every dB conversion in this app floors its output (−200 dBc, and `ToDb`'s −600 for an exactly
   * zero bin). One of those in the data would drag the window down by hundreds of dB and leave the
   * trace as a flat line at the top, which is indistinguishable from a broken measurement.
   */
  it('ignores non-finite values rather than fitting to them', () => {
    expect(autofitRange([-3, -8, Number.NEGATIVE_INFINITY, -5])).toEqual({ top: 0, bottom: -15 })
  })

  /** A trace flat to within the grid would otherwise give a zero-height window. */
  it('never returns a window narrower than minSpan', () => {
    const fitted = autofitRange([-0.001, 0.001])
    expect(fitted).not.toBeNull()
    expect(fitted!.top - fitted!.bottom).toBeGreaterThanOrEqual(10)
  })

  /**
   * A gated impulse tapers to nothing at its edges, so its lowest sample sits at the dB conversion's
   * own floor rather than anywhere meaningful. Fitting to that gave a 255 dB tall plot with the
   * impulse as a spike along the very top.
   */
  it('will not fit below a floor the caller sets', () => {
    expect(autofitRange([-6, -240, -12], { minBottom: -120 })).toEqual({ top: 10, bottom: -120 })
  })

  it('leaves the fit alone when it is already above the floor', () => {
    expect(autofitRange([-6, -30], { minBottom: -120 })).toEqual({ top: 0, bottom: -35 })
  })

  it('honours a coarser grid, for the phase axis', () => {
    expect(autofitRange([-100, 130], { step: 45, minSpan: 90 })).toEqual({ top: 180, bottom: -135 })
  })

  it('always contains the data it was given', () => {
    for (const values of [[-120, -3], [0.5, 0.6], [-95.2, -12.8, -40], [12, 12]]) {
      const fitted = autofitRange(values)!
      expect(fitted.bottom).toBeLessThanOrEqual(Math.min(...values))
      expect(fitted.top).toBeGreaterThanOrEqual(Math.max(...values))
    }
  })
})

describe('autofitRangeInBand', () => {
  const x = [10, 100, 1000, 10000]
  const y = [-80, -3, -4, -90]

  /** Sizes what is on screen, not the parts of the curve scrolled off it. */
  it('fits only the values inside the band', () => {
    expect(autofitRangeInBand(x, y, 50, 5000)).toEqual({ top: 0, bottom: -10 })
  })

  /**
   * An empty band is not the same as nothing to fit. Leaving the axis untouched would look like the
   * button did nothing, so the whole curve is used instead.
   */
  it('falls back to the whole curve when the band is empty', () => {
    expect(autofitRangeInBand(x, y, 1e6, 2e6)).toEqual(autofitRange(y))
  })
})
