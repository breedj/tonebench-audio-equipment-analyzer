import { describe, expect, it } from 'vitest'
import { AUTOSET_PERIODS, scopeAutoset, timebaseForCycles } from './autoset'
import { SCOPE_TIME_PER_DIVISION_LADDER, recordSecondsFor, snapTimePerDivision } from './timeAxis'

/** A 1 kHz sine of ±0.7 V. */
const TONE = {
  maxV: 0.7,
  minV: -0.7,
  frequencyHz: 1000,
}

describe('scopeAutoset', () => {
  it('takes the trigger level to the middle of the signal', () => {
    expect(scopeAutoset({ ...TONE, maxV: 1.2, minV: 0.2 })!.triggerLevelV).toBeCloseTo(0.7, 6)
  })

  /**
   * Rounded against the span, as the 50 % button's own write is: the midpoint of two measured
   * extremes carries every digit the arithmetic produced, and a box full of them is a reading of
   * the noise floor rather than a level anybody set.
   */
  it('rounds the trigger level the way a typed one is rounded', () => {
    const level = scopeAutoset({ ...TONE, maxV: 0.70528375, minV: -0.69471625 })!.triggerLevelV

    // The midpoint is 0.0052837500000000315; what the box gets is a level, not the arithmetic.
    expect(level).toBe(0.005)
  })

  it('fits the vertical to the measured extremes', () => {
    const set = scopeAutoset(TONE)!

    // Eight divisions, so the trace has to be inside four of them either side of the centre.
    expect(set.voltsPerDivision * 4).toBeGreaterThanOrEqual(0.7)
    expect(set.offsetV).toBeCloseTo(0, 9)
  })

  it('treats a nonsense frequency as none at all', () => {
    for (const hz of [0, -5, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(scopeAutoset({ ...TONE, frequencyHz: hz })!.periodS).toBeNull()
    }
  })

  it('answers nothing for a record that measured nothing', () => {
    expect(scopeAutoset({ ...TONE, maxV: Number.NaN })).toBeNull()
  })

  /**
   * The 50 % button's own rule through the same function, flat record included: a level that
   * crosses nothing is the honest answer, not a reason to leave the control where it was.
   */
  it('takes a flat record own level as the trigger level', () => {
    expect(scopeAutoset({ ...TONE, maxV: 0.1, minV: 0.1 })!.triggerLevelV).toBeCloseTo(0.1, 9)
  })

  /**
   * It chooses a record and not a window (asked for 2026-11-21): the caller shows all of whatever
   * record the timebase gives, so a window computed here would be a second answer to one question.
   */
  it('answers with no window at all', () => {
    const set = scopeAutoset(TONE)! as unknown as Record<string, unknown>

    expect(set.fromS).toBeUndefined()
    expect(set.toS).toBeUndefined()
  })
})

describe('the timebase Autoset sets', () => {
  const steady = { maxV: 1, minV: -1 }

  it('is five periods over the ten divisions, on a rung', () => {
    // 1 kHz: five periods is 5 ms, which is 0.5 ms a division -- itself a rung.
    const result = scopeAutoset({ ...steady, frequencyHz: 1000 })
    expect(result?.timePerDivisionS).toBeCloseTo(snapTimePerDivision(5e-3 / 10), 15)
    expect(SCOPE_TIME_PER_DIVISION_LADDER).toContain(result?.timePerDivisionS)
  })

  /** The one function both horizontal automatics ask, so they cannot answer differently. */
  it('is the same arithmetic the Time/div tile’s Autoscale uses', () => {
    for (const hz of [20, 100, 1000, 12_345]) {
      expect(scopeAutoset({ ...steady, frequencyHz: hz })!.timePerDivisionS).toBe(
        timebaseForCycles(1 / hz),
      )
    }
  })

  /**
   * The whole point of it being a horizontal control: three periods of 20 Hz is 150 ms, which no
   * ordinary record holds, and before the timebase this could only clamp a window into one that
   * did not.
   */
  it('asks for a record long enough for a signal slower than any sensible capture', () => {
    const result = scopeAutoset({ ...steady, frequencyHz: 20 })

    expect(result?.timePerDivisionS).not.toBeNull()
    expect(recordSecondsFor(result!.timePerDivisionS!)).toBeGreaterThan(0.02)
  })

  /** Within a rung of what was asked for, which is what makes the note's cycle count honest. */
  it('lands within one detent of the periods it asks for', () => {
    for (const hz of [20, 100, 1000, 12_345]) {
      const result = scopeAutoset({ ...steady, frequencyHz: hz })!
      const cycles = recordSecondsFor(result.timePerDivisionS!) * hz

      expect(cycles, `${hz} Hz`).toBeGreaterThan(AUTOSET_PERIODS / 3)
      expect(cycles, `${hz} Hz`).toBeLessThan(AUTOSET_PERIODS * 3)
    }
  })

  it('leaves the record alone with no frequency to size one from', () => {
    expect(scopeAutoset({ ...steady, frequencyHz: null })?.timePerDivisionS).toBeNull()
  })
})

describe('timebaseForCycles', () => {
  it('refuses a period that is not one', () => {
    for (const bad of [0, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(timebaseForCycles(bad)).toBeNull()
    }
    expect(timebaseForCycles(1e-3, 0)).toBeNull()
  })

  it('asks for more record the more cycles it is asked for', () => {
    const three = timebaseForCycles(1e-3, 3)!
    const five = timebaseForCycles(1e-3, 5)!
    const ten = timebaseForCycles(1e-3, 10)!

    expect(five).toBeGreaterThan(three)
    expect(ten).toBeGreaterThan(five)
  })

  /** Five, since 2026-11-21: enough to see what a waveform does between repeats. */
  it('defaults to the five a press asks for', () => {
    expect(AUTOSET_PERIODS).toBe(5)
    expect(timebaseForCycles(1e-3)).toBe(timebaseForCycles(1e-3, AUTOSET_PERIODS))
  })

  it('lands on a rung of the ladder', () => {
    for (const hz of [20, 60, 1000, 20_000]) {
      expect(SCOPE_TIME_PER_DIVISION_LADDER).toContain(timebaseForCycles(1 / hz))
    }
  })
})
