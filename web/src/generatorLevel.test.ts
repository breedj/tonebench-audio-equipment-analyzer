import { describe, expect, it } from 'vitest'
import {
  generatorLevelFromDbv,
  generatorLevelToDbv,
  generatorLevelUnitLabel,
  generatorLevelUnitOf,
} from './generatorLevel'

describe('generatorLevelFromDbv', () => {
  it('hands dBV back exactly as stored', () => {
    expect(generatorLevelFromDbv(-6, 'Dbv')).toBe(-6)
    expect(generatorLevelFromDbv(-5.9998, 'Dbv')).toBe(-5.9998)
  })

  it('converts to volts and millivolts', () => {
    expect(generatorLevelFromDbv(0, 'Volt')).toBe(1)
    expect(generatorLevelFromDbv(0, 'MilliVolt')).toBe(1000)
    expect(generatorLevelFromDbv(-6, 'Volt')).toBe(0.5012)
    expect(generatorLevelFromDbv(-6, 'MilliVolt')).toBe(501.2)
    expect(generatorLevelFromDbv(-60, 'MilliVolt')).toBe(1)
  })
})

describe('generatorLevelToDbv', () => {
  it('reads a typed level back as dBV', () => {
    expect(generatorLevelToDbv(-6, 'Dbv')).toBe(-6)
    expect(generatorLevelToDbv(1, 'Volt')).toBe(0)
    expect(generatorLevelToDbv(501.2, 'MilliVolt')).toBe(-5.9998)
  })

  it('rounds a converted level to what the dBV box can show', () => {
    // 250 mV is -12.041199826559248 dBV exactly; switching the unit back must not read like that.
    expect(generatorLevelToDbv(250, 'MilliVolt')).toBe(-12.0412)
    expect(generatorLevelToDbv(0.25, 'Volt')).toBe(-12.0412)
  })

  it('leaves a level typed in dBV at every digit the user gave it', () => {
    expect(generatorLevelToDbv(-12.041199826559248, 'Dbv')).toBe(-12.041199826559248)
  })

  it('refuses a level that has no dBV rather than storing an infinity', () => {
    expect(generatorLevelToDbv(0, 'Volt')).toBeNull()
    expect(generatorLevelToDbv(-1, 'MilliVolt')).toBeNull()
    expect(generatorLevelToDbv(Number.NaN, 'Dbv')).toBeNull()
  })

  it('leaves a box the user typed into showing what they typed', () => {
    // The round trip is what keeps the input stable: a displayed 501.2 mV must not redraw as
    // 501.19999 the moment it is stored and read back.
    for (const value of [1, 12.5, 250, 501.2, 1000, 2692]) {
      const dbv = generatorLevelToDbv(value, 'MilliVolt')!
      expect(generatorLevelFromDbv(dbv, 'MilliVolt')).toBe(value)
    }
    for (const value of [0.001, 0.25, 0.5012, 1, 3.162]) {
      const dbv = generatorLevelToDbv(value, 'Volt')!
      expect(generatorLevelFromDbv(dbv, 'Volt')).toBe(value)
    }
  })
})

describe('generatorLevelUnitOf', () => {
  it('reads a stored name back', () => {
    expect(generatorLevelUnitOf('MilliVolt')).toBe('MilliVolt')
    expect(generatorLevelUnitOf('Volt')).toBe('Volt')
  })

  it('falls back to dBV for a type saved before the unit existed', () => {
    expect(generatorLevelUnitOf(null)).toBe('Dbv')
    expect(generatorLevelUnitOf(undefined)).toBe('Dbv')
    expect(generatorLevelUnitOf('Watt')).toBe('Dbv')
  })
})

describe('generatorLevelUnitLabel', () => {
  it('names each unit the way the field beside it reads', () => {
    expect(generatorLevelUnitLabel('Dbv')).toBe('dBV')
    expect(generatorLevelUnitLabel('Volt')).toBe('V')
    expect(generatorLevelUnitLabel('MilliVolt')).toBe('mV')
  })
})
