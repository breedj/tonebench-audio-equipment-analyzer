import { describe, expect, it } from 'vitest'
import {
  DEFAULT_SCOPE_MATH_AXIS,
  MATH_DIVISIONS,
  MAX_MATH_PER_DIVISION,
  MIN_MATH_PER_DIVISION,
  fitMathAxis,
  formatMathPerDivision,
  mathAxisNeedsFit,
  mathWindow,
  snapMathPerDivision,
  stepMathPerDivision,
} from './mathAxis'
import { MAX_FILL_FRACTION, VERTICAL_DIVISIONS } from './voltAxis'

describe('the math per-division ladder', () => {
  it('snaps to 1-2-5 at any magnitude, which is what a table could not do', () => {
    // The five units span some twenty decades between them -- a V·s of microvolts over
    // microseconds against a V/s of a volt at the top of the band -- so the rungs are arithmetic.
    expect(snapMathPerDivision(1)).toBeCloseTo(1, 12)
    expect(snapMathPerDivision(1.5)).toBeCloseTo(2, 12)
    expect(snapMathPerDivision(3)).toBeCloseTo(5, 12)
    expect(snapMathPerDivision(6)).toBeCloseTo(10, 12)
    expect(snapMathPerDivision(1.3e5)).toBeCloseTo(2e5, 7)
    expect(snapMathPerDivision(3.7e-13)).toBeCloseTo(5e-13, 20)
  })

  it('leaves a figure that is already a rung exactly where it is', () => {
    // The round decades are the cases a bare Math.log10 floors into the decade below, with a
    // mantissa of ten -- which would walk every one of them up a rung on the way in.
    for (const rung of [1e-6, 1e-3, 0.001, 0.01, 0.1, 1, 10, 1000, 1e6]) {
      expect(snapMathPerDivision(rung), `${rung}`).toBeCloseTo(rung, 20)
    }
    for (const rung of [2e-6, 5e-6, 0.002, 0.05, 2, 5, 200, 5e5]) {
      expect(snapMathPerDivision(rung), `${rung}`).toBeCloseTo(rung, 20)
    }
  })

  it('walks the decades in both directions', () => {
    expect(stepMathPerDivision(1, 1)).toBeCloseTo(2, 12)
    expect(stepMathPerDivision(2, 1)).toBeCloseTo(5, 12)
    expect(stepMathPerDivision(5, 1)).toBeCloseTo(10, 12)
    expect(stepMathPerDivision(1, -1)).toBeCloseTo(0.5, 12)
    expect(stepMathPerDivision(2, -1)).toBeCloseTo(1, 12)
    expect(stepMathPerDivision(5, -1)).toBeCloseTo(2, 12)
  })

  it('returns to where it started after a step out and back', () => {
    // The property a hand-written pair of branches gets wrong first: halving a decade is where the
    // arithmetic can leave a figure a rounding error off its own rung, and then the next press
    // reads it as a different one.
    for (const rung of [1e-9, 2e-6, 0.005, 0.01, 0.2, 1, 50, 2e5]) {
      expect(stepMathPerDivision(stepMathPerDivision(rung, 1), -1), `${rung} up then down`).toBeCloseTo(
        rung,
        20,
      )
      expect(stepMathPerDivision(stepMathPerDivision(rung, -1), 1), `${rung} down then up`).toBeCloseTo(
        rung,
        20,
      )
    }
  })

  it('stops at either end rather than running on', () => {
    expect(stepMathPerDivision(MIN_MATH_PER_DIVISION, -1)).toBeCloseTo(MIN_MATH_PER_DIVISION, 20)
    expect(stepMathPerDivision(MAX_MATH_PER_DIVISION, 1)).toBeCloseTo(MAX_MATH_PER_DIVISION, 0)
  })

  it('answers the bottom rung for a figure that is not one', () => {
    expect(snapMathPerDivision(0)).toBe(MIN_MATH_PER_DIVISION)
    expect(snapMathPerDivision(-1)).toBe(MIN_MATH_PER_DIVISION)
    expect(snapMathPerDivision(Number.NaN)).toBe(MIN_MATH_PER_DIVISION)
  })
})

describe('the math window', () => {
  it('is the graticule the waveforms are counted against, and not one of its own', () => {
    // It has to be: the math trace is on the same plot, so two divisions of it and two divisions
    // of the waveform are the same height on screen or neither figure means anything.
    expect(MATH_DIVISIONS).toBe(VERTICAL_DIVISIONS)
  })

  it('puts the ends half a graticule either side of the centre', () => {
    const w = mathWindow({ perDivision: 0.5, centre: 2, autoscale: false })
    expect(w.top).toBeCloseTo(2 + 2, 12)
    expect(w.bottom).toBeCloseTo(2 - 2, 12)
  })

  it('draws something rather than nothing for a pair that cannot be one', () => {
    const w = mathWindow({ perDivision: 0, centre: Number.NaN, autoscale: false })
    expect(w.top).toBeGreaterThan(w.bottom)
    expect(Number.isFinite(w.top) && Number.isFinite(w.bottom)).toBe(true)
  })
})

describe('fitting a math axis', () => {
  it('frames the trace inside the fraction of the height a fit leaves', () => {
    const fit = fitMathAxis(0.31, -0.31)
    const fill = 0.62 / (fit.perDivision * MATH_DIVISIONS)
    expect(fill).toBeLessThanOrEqual(MAX_FILL_FRACTION)
    expect(fill).toBeGreaterThan(MAX_FILL_FRACTION / 2.5)
  })

  it('centres on the midpoint of the extremes and never snaps it to zero', () => {
    // The one place this departs from the volts fit, and it is the whole reason it is its own
    // function: zero is not a special value on a derived axis. A ratio sits at one, and a product
    // of two tones on its own DC term -- pulling either onto zero takes the trace off the picture
    // the fit was framing.
    const fit = fitMathAxis(1.05, 0.95)
    expect(fit.centre).toBeCloseTo(1, 6)
    expect(mathWindow({ ...fit, autoscale: true }).bottom).toBeGreaterThan(0)
  })

  it('writes a centre somebody could have typed', () => {
    // A midpoint of two measured extremes carries every digit the arithmetic produced, and the
    // Centre box then reads nine significant figures of nothing.
    const fit = fitMathAxis(0.6017391304347828, -0.1)
    expect(String(fit.centre).length).toBeLessThanOrEqual(8)
  })

  it('answers a drawable pair for extremes that are not numbers', () => {
    const fit = fitMathAxis(Number.NaN, Number.NaN)
    expect(fit.perDivision).toBeGreaterThan(0)
    expect(Number.isFinite(fit.centre)).toBe(true)
  })
})

describe('the math autoscale', () => {
  it('leaves an axis the fit has just chosen alone', () => {
    // The property the dead band exists for, and the reported fault if it is missing: applying the
    // fit must not produce a picture the band then asks to fit again, or the graticule is redrawn
    // under the trace on every record.
    for (const span of [1e-9, 3.1e-6, 0.031, 1, 7.4, 1.3e5]) {
      const fit = fitMathAxis(span / 2, -span / 2)
      expect(mathAxisNeedsFit({ ...fit, autoscale: true }, span / 2, -span / 2), `${span}`).toBe(
        false,
      )
    }
  })

  it('asks for a fit once the trace grows past the margin, and not before', () => {
    const fit = { ...fitMathAxis(0.15, -0.15), autoscale: true }
    const reach = fit.perDivision * (MATH_DIVISIONS / 2) * MAX_FILL_FRACTION
    expect(mathAxisNeedsFit(fit, reach * 1.02, -reach * 1.02)).toBe(true)
    expect(mathAxisNeedsFit(fit, reach * 0.98, -reach * 0.98)).toBe(false)
  })

  it('holds a trace that is merely off centre, until it leans on an edge', () => {
    const fit = { ...fitMathAxis(1.05, 0.95), autoscale: true }
    // Still well inside the window it was fitted into.
    expect(mathAxisNeedsFit(fit, 1.06, 0.94)).toBe(false)
    // And out of it.
    expect(mathAxisNeedsFit(fit, 2, 0.95)).toBe(true)
  })

  it('is on by default, which is what this axis has always done', () => {
    // Off by default would hand every measurement already saved a fixed scale nobody chose, on an
    // axis whose magnitude nobody knows in advance.
    expect(DEFAULT_SCOPE_MATH_AXIS.autoscale).toBe(true)
  })
})

describe('what the tile reads', () => {
  it('writes the division in the operation’s own unit', () => {
    expect(formatMathPerDivision(0.5, 'Volt')).toMatch(/V$/)
    expect(formatMathPerDivision(0.5, 'VoltSquared')).toMatch(/V²$/)
    expect(formatMathPerDivision(0.5, 'VoltSecond')).toMatch(/V·s$/)
    // A bare ratio has no unit at all, which is the honest answer for A ÷ B.
    expect(formatMathPerDivision(0.5, 'Ratio')).not.toMatch(/V/)
  })

  it('scales a rate’s denominator rather than its numerator', () => {
    // 2.4 MV/s is the same figure as 2.400 V/µs and the second is the one every amplifier
    // datasheet quotes — the rule formatMathValue already carries, inherited rather than repeated.
    expect(formatMathPerDivision(2.4e6, 'VoltPerSecond')).toContain('V/µs')
    expect(formatMathPerDivision(2.4e6, 'VoltPerSecond')).not.toContain('MV')
  })
})
