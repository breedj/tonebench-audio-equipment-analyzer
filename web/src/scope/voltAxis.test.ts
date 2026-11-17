import { describe, expect, it } from 'vitest'
import {
  VERTICAL_DIVISIONS,
  VOLTS_PER_DIVISION_LADDER,
  fitVoltWindow,
  formatVolts,
  formatVoltsPerDivision,
  roundVoltsForWindow,
  snapVoltsPerDivision,
  stepVoltsPerDivision,
  voltWindow,
} from './voltAxis'

describe('the ladder', () => {
  it('is 1-2-5 per decade and strictly increasing', () => {
    for (let i = 1; i < VOLTS_PER_DIVISION_LADDER.length; i++)
      expect(VOLTS_PER_DIVISION_LADDER[i]).toBeGreaterThan(VOLTS_PER_DIVISION_LADDER[i - 1])
    for (const rung of VOLTS_PER_DIVISION_LADDER) {
      const mantissa = rung / 10 ** Math.floor(Math.log10(rung) + 1e-9)
      expect([1, 2, 5]).toContain(Number(mantissa.toPrecision(2)))
    }
  })

  it('reaches as high as the analyzer can measure and no higher', () => {
    // The QA403's widest input range is 42 dBV, about 178 V peak, which is 22 V a division over
    // eight of them -- so 50 V/div is the first rung that holds anything measurable and nothing
    // above it is reachable.
    expect(VOLTS_PER_DIVISION_LADDER.at(-1)).toBe(50)
    expect(50 * VERTICAL_DIVISIONS).toBeGreaterThan(178)
    expect(20 * VERTICAL_DIVISIONS).toBeLessThan(178)
  })

  it('has eight divisions, the near-universal convention', () => {
    expect(VERTICAL_DIVISIONS).toBe(8)
  })
})

describe('voltWindow', () => {
  it('centres the graticule on the offset', () => {
    expect(voltWindow(0.5, 0)).toEqual({ topV: 2, bottomV: -2 })
  })

  it('moves both ends with the offset', () => {
    expect(voltWindow(0.5, 1)).toEqual({ topV: 3, bottomV: -1 })
  })

  it('answers a nonsense setting with a drawable window rather than a blank plot', () => {
    expect(voltWindow(0, 0).topV).toBeGreaterThan(voltWindow(0, 0).bottomV)
    expect(voltWindow(Number.NaN, Number.NaN).topV).toBeGreaterThan(0)
  })
})

describe('snapVoltsPerDivision', () => {
  it('takes the rung at or above what was asked for', () => {
    expect(snapVoltsPerDivision(0.3)).toBe(0.5)
    expect(snapVoltsPerDivision(0.5)).toBe(0.5)
    expect(snapVoltsPerDivision(0.51)).toBe(1)
  })

  it('tolerates a rung arrived at by arithmetic rather than typed', () => {
    // 0.1 + 0.2 - 0.3 is not zero, and neither is a V/div derived from a fitted span.
    expect(snapVoltsPerDivision(0.1 + 0.2 - 0.3 + 0.2)).toBe(0.2)
  })

  it('stops at the top of the ladder rather than inventing a rung', () => {
    expect(snapVoltsPerDivision(1e6)).toBe(50)
  })

  it('answers a non-positive ask with the most magnified rung', () => {
    expect(snapVoltsPerDivision(0)).toBe(VOLTS_PER_DIVISION_LADDER[0])
    expect(snapVoltsPerDivision(Number.NaN)).toBe(VOLTS_PER_DIVISION_LADDER[0])
  })
})

describe('stepVoltsPerDivision', () => {
  it('moves one rung at a time', () => {
    expect(stepVoltsPerDivision(0.5, 1)).toBe(1)
    expect(stepVoltsPerDivision(0.5, -1)).toBe(0.2)
  })

  it('stops at either end instead of running off it', () => {
    expect(stepVoltsPerDivision(50, 1)).toBe(50)
    expect(stepVoltsPerDivision(VOLTS_PER_DIVISION_LADDER[0], -1)).toBe(VOLTS_PER_DIVISION_LADDER[0])
  })

  it('finds its way back onto the ladder from a value between rungs', () => {
    expect(stepVoltsPerDivision(0.37, 1)).toBe(1)
    expect(stepVoltsPerDivision(0.37, -1)).toBe(0.2)
  })
})

describe('fitVoltWindow', () => {
  it('frames a symmetric trace on about six of the eight divisions', () => {
    // 1 V peak: six divisions of it is 0.333 V a division, which snaps up to 0.5.
    const fit = fitVoltWindow(1, -1)
    expect(fit.voltsPerDivision).toBe(0.5)
    expect(fit.offsetV).toBe(0)

    const window = voltWindow(fit.voltsPerDivision, fit.offsetV)
    expect(window.topV).toBeGreaterThan(1)
    expect(window.bottomV).toBeLessThan(-1)
  })

  it('leaves headroom rather than filling the plot to both edges', () => {
    // Filling all eight divisions would clip the next capture's overshoot off the top with nothing
    // saying so.
    const fit = fitVoltWindow(1, -1)
    const window = voltWindow(fit.voltsPerDivision, fit.offsetV)
    expect(window.topV).toBeGreaterThanOrEqual(1 * 1.2)
  })

  it('centres on the midpoint of the extremes, not on the mean', () => {
    // A trace from 0 to 10 V -- a unipolar pulse -- is centred on 5, whatever its duty cycle.
    const fit = fitVoltWindow(10, 0)
    expect(fit.offsetV).toBeCloseTo(5, 12)
  })

  it('snaps a nearly-centred trace onto zero exactly', () => {
    // An AC signal's midpoint is zero plus whatever noise the record carried; without the snap the
    // offset box reads a figure nobody typed, and a reader cannot tell a real DC offset from it.
    expect(fitVoltWindow(1.0003, -0.9997).offsetV).toBe(0)
  })

  it('keeps a real DC offset', () => {
    const fit = fitVoltWindow(1.5, 0.5)
    expect(fit.offsetV).toBeCloseTo(1, 12)
  })

  it('gives a flat trace the most magnified rung', () => {
    expect(fitVoltWindow(0, 0).voltsPerDivision).toBe(VOLTS_PER_DIVISION_LADDER[0])
  })

  it('answers a non-finite extreme with the widest view rather than a NaN window', () => {
    const fit = fitVoltWindow(Number.NaN, -1)
    expect(fit.voltsPerDivision).toBe(50)
    expect(Number.isFinite(voltWindow(fit.voltsPerDivision, fit.offsetV).topV)).toBe(true)
  })
})

describe('formatVolts', () => {
  it('takes its unit from the division so a column of readings stays in one unit', () => {
    expect(formatVolts(1.234, 0.5)).toBe('1.234 V')
    expect(formatVolts(0.0012, 0.001)).toBe('1.20 mV')
  })

  it('writes a negative zero as zero', () => {
    expect(formatVolts(-0.00001, 0.5)).toBe('0.000 V')
  })

  it('answers a non-finite reading with a dash', () => {
    expect(formatVolts(Number.NaN, 0.5)).toBe('—')
  })
})

describe('formatVoltsPerDivision', () => {
  it('quotes the figure a scopes status line quotes', () => {
    expect(formatVoltsPerDivision(0.5)).toBe('0.5 V/div')
    expect(formatVoltsPerDivision(2)).toBe('2 V/div')
    expect(formatVoltsPerDivision(0.05)).toBe('50 mV/div')
    expect(formatVoltsPerDivision(0.0002)).toBe('0.2 mV/div')
  })

  it('answers a nonsense setting with a dash', () => {
    expect(formatVoltsPerDivision(0)).toBe('—')
  })

  it('has a reading for every rung of the ladder', () => {
    for (const rung of VOLTS_PER_DIVISION_LADDER) expect(formatVoltsPerDivision(rung)).not.toBe('—')
  })
})

describe('roundVoltsForWindow', () => {
  it('turns a drag back into a figure a box can show', () => {
    // The report: a line dragged to roughly six tenths of a volt put
    // "0.6017391304347828" in the trigger panel's Level box.
    expect(roundVoltsForWindow(0.6017391304347828, 4)).toBe(0.602)
  })

  it('rounds to a decimal place rather than to a multiple of the step', () => {
    // 144 x (4 / 1000) is 0.5760000000000001, so rounding to the nearest multiple of the step puts
    // back exactly the digits the rounding is for. This is the first attempt at it, pinned.
    expect(roundVoltsForWindow(0.5761, 4)).toBe(0.576)
    expect(String(roundVoltsForWindow(0.5761, 4))).toBe('0.576')
  })

  it('follows the window, so a millivolt screen keeps its millivolts', () => {
    // Eight divisions of 0.1 mV: a thousandth of that window is 0.8 uV, so the figure keeps seven
    // decimals where a four-volt window keeps three -- and a level of 97.6 uV survives as one
    // rather than rounding away to nothing.
    expect(roundVoltsForWindow(0.0000976123456, 0.0008)).toBe(0.0000976)
    expect(roundVoltsForWindow(0.0000976123456, 4)).toBe(0)
  })

  it('leaves a figure alone when there is no window to round against', () => {
    expect(roundVoltsForWindow(0.6017391304347828, 0)).toBe(0.6017391304347828)
    expect(roundVoltsForWindow(0.6017391304347828, Number.NaN)).toBe(0.6017391304347828)
    expect(Number.isNaN(roundVoltsForWindow(Number.NaN, 4))).toBe(true)
  })

  it('keeps a whole-volt window usable', () => {
    // A 400 V window would ask for no decimals at all if the clamp were not there; one decimal is
    // what the arithmetic gives, and it is finer than the thousandth asked for.
    expect(roundVoltsForWindow(123.456789, 400)).toBe(123.5)
  })
})
