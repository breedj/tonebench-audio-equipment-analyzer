import { describe, expect, it } from 'vitest'
import {
  HORIZONTAL_DIVISIONS,
  clampWindow,
  formatPerDivision,
  formatScopeTime,
  fromChartMs,
  isWholeRecord,
  recordWindow,
  scopeTimeScale,
  toChartMs,
} from './timeAxis'

describe('the two units the scope holds a time in', () => {
  it('round-trips seconds through the chart', () => {
    expect(toChartMs(0.02)).toBeCloseTo(20, 12)
    expect(fromChartMs(20)).toBeCloseTo(0.02, 12)
    expect(fromChartMs(toChartMs(-0.005))).toBeCloseTo(-0.005, 12)
  })
})

describe('scopeTimeScale', () => {
  it('reads a 20 ms record in milliseconds', () => {
    expect(scopeTimeScale(20).unit).toBe('ms')
  })

  it('reads a 50 us record in microseconds', () => {
    // The whole reason the unit follows the window: 20 us in milliseconds is "0.020", which is
    // arithmetic rather than a reading.
    expect(scopeTimeScale(0.05).unit).toBe('us')
  })

  it('reads a second-long record in seconds', () => {
    expect(scopeTimeScale(1000).unit).toBe('s')
  })

  it('switches at the units own boundaries, not at a judgement', () => {
    expect(scopeTimeScale(1).unit).toBe('ms')
    expect(scopeTimeScale(0.999).unit).toBe('us')
    expect(scopeTimeScale(999).unit).toBe('ms')
  })

  it('falls back to milliseconds for a window that is not one yet', () => {
    expect(scopeTimeScale(0).unit).toBe('ms')
    expect(scopeTimeScale(Number.NaN).unit).toBe('ms')
  })
})

describe('formatScopeTime', () => {
  it('writes a time in the unit its own window is read in', () => {
    expect(formatScopeTime(1.5, 20)).toBe('1.50 ms')
    expect(formatScopeTime(0.015, 0.05)).toBe('15.00 µs')
    expect(formatScopeTime(250, 1000)).toBe('0.250 s')
  })

  it('drops the unit for an axis tick, where the axis label carries it', () => {
    expect(formatScopeTime(1.5, 20, false)).toBe('1.50')
  })

  it('writes a negative zero as zero', () => {
    // -0.00 is floating point showing through at the trigger, and the trigger is the one tick
    // every reader looks at.
    expect(formatScopeTime(-0.0001, 20, false)).toBe('0.00')
  })

  it('answers a non-finite time with a dash rather than NaN', () => {
    expect(formatScopeTime(Number.NaN, 20)).toBe('—')
  })
})

describe('formatPerDivision', () => {
  it('quotes the figure a scopes status line quotes', () => {
    expect(formatPerDivision(20)).toBe('2.00 ms/div')
    expect(formatPerDivision(1000)).toBe('100.00 ms/div')
    expect(formatPerDivision(0.5)).toBe('50.00 µs/div')
  })

  it('takes its unit from the division and not from the window', () => {
    // A 5 ms window is read in ms, but half a millisecond a division reads better in us -- which is
    // the graticule a user is counting squares on.
    expect(scopeTimeScale(5).unit).toBe('ms')
    expect(formatPerDivision(5)).toContain('µs/div')
  })

  it('has ten divisions, as every bench instrument quotes it', () => {
    expect(HORIZONTAL_DIVISIONS).toBe(10)
  })
})

describe('recordWindow', () => {
  it('puts the pre-trigger before zero and the rest after it', () => {
    expect(recordWindow(0.02, 0.25)).toEqual({ fromS: -0.005, toS: 0.015 })
  })

  it('answers 100 % pre-trigger with a window entirely to the left of the trigger', () => {
    const window = recordWindow(0.02, 1)
    expect(window.fromS).toBeCloseTo(-0.02, 12)
    expect(window.toS).toBeCloseTo(0, 12)
  })

  it('answers no pre-trigger with a window entirely to the right', () => {
    const window = recordWindow(0.02, 0)
    expect(window.fromS).toBeCloseTo(0, 12)
    expect(window.toS).toBeCloseTo(0.02, 12)
  })

  it('falls back rather than returning a window with no span', () => {
    const window = recordWindow(0, Number.NaN)
    expect(window.toS).toBeGreaterThan(window.fromS)
  })
})

describe('clampWindow', () => {
  const bounds = { fromS: -0.005, toS: 0.015 }

  it('leaves a window inside the record alone', () => {
    expect(clampWindow({ fromS: 0, toS: 0.001 }, bounds)).toEqual({ fromS: 0, toS: 0.001 })
  })

  it('clamps a window reaching outside the record to it', () => {
    expect(clampWindow({ fromS: -10, toS: 10 }, bounds)).toEqual(bounds)
  })

  it('refuses a reversed pair rather than sorting it', () => {
    // Reachable by typing into the panel's boxes. Sorting it would accept a figure nobody meant;
    // the bounds are the answer that is always drawable.
    expect(clampWindow({ fromS: 0.01, toS: 0.001 }, bounds)).toEqual(bounds)
  })

  it('refuses a window with no span, which doubled is still no span', () => {
    expect(clampWindow({ fromS: 0.002, toS: 0.002 }, bounds)).toEqual(bounds)
  })

  it('refuses a non-finite window, so a cleared number box cannot blank the plot', () => {
    expect(clampWindow({ fromS: Number.NaN, toS: 0.01 }, bounds)).toEqual(bounds)
  })

  it('answers bounds that are not a window with something drawable', () => {
    const fallback = clampWindow({ fromS: 0, toS: 1 }, { fromS: 0, toS: 0 })
    expect(fallback.toS).toBeGreaterThan(fallback.fromS)
  })
})

describe('isWholeRecord', () => {
  const bounds = { fromS: -0.005, toS: 0.015 }

  it('is true for the record itself', () => {
    expect(isWholeRecord(bounds, bounds)).toBe(true)
  })

  it('is true within floating-point noise of it, so no Reset zoom is offered on an untouched chart', () => {
    expect(isWholeRecord({ fromS: -0.005 + 1e-12, toS: 0.015 }, bounds)).toBe(true)
  })

  it('is false once either end has really moved', () => {
    expect(isWholeRecord({ fromS: 0, toS: 0.015 }, bounds)).toBe(false)
    expect(isWholeRecord({ fromS: -0.005, toS: 0.001 }, bounds)).toBe(false)
  })
})
