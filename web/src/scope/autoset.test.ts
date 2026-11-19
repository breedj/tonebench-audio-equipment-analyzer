import { describe, expect, it } from 'vitest'
import { AUTOSET_PERIODS, autosetNote, scopeAutoset } from './autoset'

/** A 1 kHz sine of ±0.7 V in a 20 ms record with a quarter of it before the trigger. */
const TONE = {
  maxV: 0.7,
  minV: -0.7,
  frequencyHz: 1000,
  recordFromS: -0.005,
  recordToS: 0.015,
}

describe('scopeAutoset', () => {
  it('puts three cycles on screen', () => {
    const set = scopeAutoset(TONE)!

    expect((set.toS - set.fromS) * TONE.frequencyHz).toBeCloseTo(AUTOSET_PERIODS, 9)
  })

  it('hangs the window off the trigger, a quarter of it before', () => {
    const set = scopeAutoset(TONE)!

    expect(set.fromS).toBeCloseTo(-0.00075, 9)
    expect(set.toS).toBeCloseTo(0.00225, 9)
  })

  it('takes the trigger level to the middle of the signal', () => {
    expect(scopeAutoset({ ...TONE, maxV: 1.2, minV: 0.2 })!.triggerLevelV).toBeCloseTo(0.7, 6)
  })

  it('fits the vertical to the measured extremes', () => {
    const set = scopeAutoset(TONE)!

    // Eight divisions, so the trace has to be inside four of them either side of the centre.
    expect(set.voltsPerDivision * 4).toBeGreaterThanOrEqual(0.7)
    expect(set.offsetV).toBeCloseTo(0, 9)
  })

  /**
   * The rule that is quietly wrong by default: three periods of 20 Hz is 150 ms, which a 20 ms
   * record does not contain, and a window past the capture draws a trace that stops in the middle
   * of the plot.
   */
  it('clamps the window into a record too short for three cycles', () => {
    const set = scopeAutoset({ ...TONE, frequencyHz: 20 })!

    expect(set.fromS).toBeCloseTo(TONE.recordFromS, 9)
    expect(set.toS).toBeCloseTo(TONE.recordToS, 9)
  })

  /** Slid rather than squeezed: the number of cycles is the figure the whole thing is chosen for. */
  it('slides a window that runs past the end rather than shortening it', () => {
    const set = scopeAutoset({ ...TONE, recordFromS: 0, recordToS: 0.002 })!

    expect(set.toS).toBeCloseTo(0.002, 9)
    expect(set.fromS).toBeCloseTo(0, 9)
  })

  it('shows the whole record where no frequency was measured', () => {
    const set = scopeAutoset({ ...TONE, frequencyHz: null })!

    expect(set.fromS).toBe(TONE.recordFromS)
    expect(set.toS).toBe(TONE.recordToS)
    expect(set.periodS).toBeNull()
    // And still sets the other two, which do not need a frequency.
    expect(set.triggerLevelV).toBeCloseTo(0, 6)
    expect(set.voltsPerDivision).toBeGreaterThan(0)
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
})

describe('autosetNote', () => {
  it('says how many cycles it put on screen', () => {
    expect(autosetNote(scopeAutoset(TONE))).toMatch(/3\.0 cycles/)
  })

  it('says so when there was no frequency to work from', () => {
    expect(autosetNote(scopeAutoset({ ...TONE, frequencyHz: null }))).toMatch(/whole record/)
  })

  it('is nothing at all with nothing to report', () => {
    expect(autosetNote(null)).toBeNull()
  })
})
