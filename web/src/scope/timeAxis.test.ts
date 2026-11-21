import { describe, expect, it } from 'vitest'
import {
  HORIZONTAL_DIVISIONS,
  clampWindow,
  followRecordWindow,
  formatPerDivision,
  formatScopeTime,
  fromChartMs,
  isWholeRecord,
  preTriggerFromPointer,
  recordWindow,
  recordSecondsFor,
  SCOPE_TIME_PER_DIVISION_LADDER,
  snapTimePerDivision,
  timePerDivisionFor,
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

describe('followRecordWindow', () => {
  const was = recordWindow(0.02, 0.25)

  it('follows the record when the window was the record', () => {
    const now = recordWindow(0.004, 0.25)
    expect(followRecordWindow(was, was, now)).toEqual(now)
  })

  it('leaves a deliberate zoom alone where it still fits', () => {
    const zoom = { fromS: 0.001, toS: 0.002 }
    const now = recordWindow(0.004, 0.25)
    expect(followRecordWindow(zoom, was, now)).toEqual(zoom)
  })

  it('clamps a zoom the shortened record no longer holds', () => {
    const zoom = { fromS: 0.012, toS: 0.014 }
    const now = recordWindow(0.004, 0.25)
    const result = followRecordWindow(zoom, was, now)
    expect(result.fromS).toBeGreaterThanOrEqual(now.fromS)
    expect(result.toS).toBeLessThanOrEqual(now.toS)
    expect(result.toS).toBeGreaterThan(result.fromS)
  })

  it('follows a pre-trigger change, which moves the record without resizing it', () => {
    const now = recordWindow(0.02, 0.75)
    expect(followRecordWindow(was, was, now)).toEqual(now)
    expect(now.fromS).not.toBeCloseTo(was.fromS, 9)
  })
})

describe('preTriggerFromPointer', () => {
  it('reads the record’s own start as none of it and its end as all of it', () => {
    // A 20 ms record a quarter pre-trigger runs -5 ms to +15 ms.
    expect(preTriggerFromPointer(-0.005, 0.02, 0.25)).toBeCloseTo(0, 12)
    expect(preTriggerFromPointer(0.015, 0.02, 0.25)).toBeCloseTo(1, 12)
    expect(preTriggerFromPointer(0.005, 0.02, 0.25)).toBeCloseTo(0.5, 12)
  })

  it('reads the trigger’s own position back as the fraction it already was', () => {
    for (const fraction of [0, 0.1, 0.25, 0.5, 0.9, 1]) {
      expect(preTriggerFromPointer(0, 0.02, fraction)).toBeCloseTo(fraction, 12)
    }
  })

  it('clamps a pointer dragged off either end rather than refusing it', () => {
    expect(preTriggerFromPointer(-5, 0.02, 0.25)).toBe(0)
    expect(preTriggerFromPointer(5, 0.02, 0.25)).toBe(1)
  })

  it('keeps the fraction it had when there is no record to measure against', () => {
    expect(preTriggerFromPointer(0.001, 0, 0.4)).toBe(0.4)
    expect(preTriggerFromPointer(Number.NaN, 0.02, 0.4)).toBe(0.4)
    expect(preTriggerFromPointer(0.001, 0.02, Number.NaN)).toBeCloseTo(0.3, 12)
  })
})

describe('the timebase ladder', () => {
  it('runs 1-2-5 across its whole span', () => {
    for (const rung of SCOPE_TIME_PER_DIVISION_LADDER) {
      const mantissa = rung / 10 ** Math.floor(Math.log10(rung) + 1e-9)
      expect([1, 2, 5].some((m) => Math.abs(mantissa - m) < 1e-6), `${rung}`).toBe(true)
    }
  })

  it('climbs, and never repeats a rung', () => {
    for (let i = 1; i < SCOPE_TIME_PER_DIVISION_LADDER.length; i += 1) {
      expect(SCOPE_TIME_PER_DIVISION_LADDER[i]).toBeGreaterThan(SCOPE_TIME_PER_DIVISION_LADDER[i - 1])
    }
  })

  it('ends exactly where the server would start refusing', () => {
    // MinRecordSeconds = 20 us and MaxRecordSeconds = 1 s, over ten divisions. Asserted as the
    // record rather than as the rung, because that is the figure the refusal is about.
    const first = recordSecondsFor(SCOPE_TIME_PER_DIVISION_LADDER[0])
    const last = recordSecondsFor(SCOPE_TIME_PER_DIVISION_LADDER[SCOPE_TIME_PER_DIVISION_LADDER.length - 1])
    expect(first).toBeCloseTo(20e-6, 12)
    expect(last).toBeCloseTo(1, 12)
  })
})

describe('recordSecondsFor and timePerDivisionFor', () => {
  it('are ten divisions apart, and are exact inverses', () => {
    for (const rung of SCOPE_TIME_PER_DIVISION_LADDER) {
      expect(recordSecondsFor(rung)).toBeCloseTo(rung * HORIZONTAL_DIVISIONS, 15)
      expect(timePerDivisionFor(recordSecondsFor(rung))).toBeCloseTo(rung, 15)
    }
  })

  it('reads the default 20 ms record as 2 ms a division', () => {
    expect(timePerDivisionFor(0.02)).toBeCloseTo(2e-3, 15)
  })
})

describe('snapTimePerDivision', () => {
  it('leaves a rung alone', () => {
    for (const rung of SCOPE_TIME_PER_DIVISION_LADDER) {
      expect(snapTimePerDivision(rung)).toBe(rung)
    }
  })

  it('is nearest in the ratio, not in seconds', () => {
    // 3 us sits between 2 and 5. Linearly 2 is nearer; by ratio it is 1.5x against 1.67x, so 2 wins
    // either way -- but 3.2 us is 1.6x and 1.5625x, where the two answers differ and the ratio's is
    // the one a reader of a 1-2-5 ladder means.
    expect(snapTimePerDivision(3.2e-6)).toBeCloseTo(5e-6, 15)
    expect(snapTimePerDivision(3e-6)).toBeCloseTo(2e-6, 15)
  })

  it('pins anything past either end to that end', () => {
    expect(snapTimePerDivision(1e-9)).toBeCloseTo(2e-6, 15)
    expect(snapTimePerDivision(10)).toBeCloseTo(0.1, 15)
  })

  it('answers a rung for a figure that is no time at all', () => {
    for (const bad of [0, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(SCOPE_TIME_PER_DIVISION_LADDER).toContain(snapTimePerDivision(bad))
    }
  })
})


describe('preTriggerFromPointer rounding', () => {
  it('lands on whole percentages, which is the unit the setting is in', () => {
    // The box types a percentage and the marker's tooltip quotes one; a drag that left 23.37 %
    // behind would make the two disagree about the same setting.
    // A 10 ms record whose trigger is a quarter in starts at -2.5 ms, so a pointer 2.337 ms along
    // it is asking for 23.37 %.
    const fraction = preTriggerFromPointer(-0.000163, 0.01, 0.25)
    expect(fraction).toBeCloseTo(0.23, 12)
    expect(Math.round(fraction * 10000) % 100).toBe(0)
  })

  it('still reaches both ends exactly', () => {
    expect(preTriggerFromPointer(-1, 0.01, 0.25)).toBe(0)
    expect(preTriggerFromPointer(1, 0.01, 0.25)).toBe(1)
  })

  it('is a fixed point: dragging back to where it started lands there', () => {
    const start = -0.01 * 0.25
    expect(preTriggerFromPointer(start + 0.01 * 0.25, 0.01, 0.25)).toBe(0.25)
  })
})
