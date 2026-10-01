import { describe, expect, it } from 'vitest'
import { fromDisplay, formatGain, toDisplay, MAX_ABS_DB } from './gainUnits'

describe('gainUnits', () => {
  it('toDisplay_InDb_IsTheStoredValue', () => {
    expect(toDisplay(-20, 'Db')).toBe(-20)
    expect(toDisplay(0, 'Db')).toBe(0)
  })

  // The case the unit selector exists for: a 10:1 divider reads as 0.1, not as -20.
  it('toDisplay_AsARatio_ReadsADividerTheWayABenchDoes', () => {
    expect(toDisplay(-20, 'Times')).toBeCloseTo(0.1, 10)
    expect(toDisplay(0, 'Times')).toBeCloseTo(1, 10)
    expect(toDisplay(20, 'Times')).toBeCloseTo(10, 10)
  })

  it('fromDisplay_RoundTripsThroughBothUnits', () => {
    for (const db of [-40, -20, -6, 0, 6, 20]) {
      expect(fromDisplay(toDisplay(db, 'Db'), 'Db')).toBeCloseTo(db, 10)
      expect(fromDisplay(toDisplay(db, 'Times'), 'Times')).toBeCloseTo(db, 10)
    }
  })

  it('fromDisplay_ARatioOfOne_IsNoGain', () => {
    expect(fromDisplay(1, 'Times')).toBeCloseTo(0, 10)
  })

  // log10(0) is -Infinity, which would reach the server as an invalid document and come back as a
  // 400 the user cannot act on. Zero and negative ratios mean "nothing declared" instead.
  it('fromDisplay_ARatioOfZero_IsNoGainRatherThanMinusInfinity', () => {
    expect(fromDisplay(0, 'Times')).toBe(0)
    expect(Number.isFinite(fromDisplay(0, 'Times'))).toBe(true)
  })

  // A negative ratio is an inversion, which is a phase statement rather than a gain.
  it('fromDisplay_ANegativeRatio_IsNoGain', () => {
    expect(fromDisplay(-0.1, 'Times')).toBe(0)
  })

  it('fromDisplay_NotANumber_IsNoGain', () => {
    expect(fromDisplay(Number.NaN, 'Db')).toBe(0)
    expect(fromDisplay(Number.POSITIVE_INFINITY, 'Times')).toBe(0)
  })

  it('fromDisplay_BeyondTheServersBound_IsClamped', () => {
    expect(fromDisplay(500, 'Db')).toBe(MAX_ABS_DB)
    expect(fromDisplay(-500, 'Db')).toBe(-MAX_ABS_DB)
    // 1e6 is +120 dB, past the bound.
    expect(fromDisplay(1e6, 'Times')).toBe(MAX_ABS_DB)
  })

  it('formatGain_SignsTheDbValue_AndMarksTheRatio', () => {
    expect(formatGain(-20, 'Db')).toBe('-20 dB')
    expect(formatGain(6, 'Db')).toBe('+6 dB')
    expect(formatGain(-20, 'Times')).toBe('×0.1')
  })

  // 0.1 must not read as 0.1000 just because a ratio needs four decimals at the small end.
  it('formatGain_DropsTrailingZeros', () => {
    expect(formatGain(0, 'Times')).toBe('×1')
    // Zero carries no sign: the badge is grey at 0 to say nothing is declared, and "+0" reads as
    // a figure that was chosen.
    expect(formatGain(0, 'Db')).toBe('0 dB')
  })
})
