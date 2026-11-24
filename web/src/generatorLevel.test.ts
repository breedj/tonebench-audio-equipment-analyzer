import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
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

/**
 * The level boxes and the unit beside them read one store, and it is the store the panel was
 * given.
 *
 * <p><b>The bug this exists for.</b> `GeneratorLevelUnitSelect` reached for the FFT screen's module
 * singleton directly, where `GeneratorChannelPanel` converts with `props.stimulus.levelUnit`. One
 * screen, one store, and nobody noticed — until the oscilloscope got a store of its own. There,
 * picking <b>mV</b> wrote the FFT store while the box went on converting in <b>dBV</b>, so a level
 * typed as 5 mV was played as 5 dBV: 1.778 V, <b>51 dB</b> out, with the box reading 5 beside a
 * dropdown reading mV. Reported from a bench 2026-11-24 as the scope "not outputting what it says",
 * with every other screen correct — which is the signature, since the FFT screen's two halves are
 * the same store by accident of being the default.</p>
 *
 * <p>An audit rather than a unit test because both halves live in <code>.vue</code> files and this
 * suite runs on <code>environment: 'node'</code> — the technique `seriesScale.test.ts` and
 * `numberField.test.ts` already use. Both directions are checked: a call site that forgets the
 * prop, and a component that picks a store for itself.</p>
 */
describe('the generator level unit belongs to the store the panel edits', () => {
  const componentDir = resolve(import.meta.dirname, 'components')
  const select = readFileSync(join(componentDir, 'GeneratorLevelUnitSelect.vue'), 'utf8')
  const panel = readFileSync(join(componentDir, 'GeneratorChannelPanel.vue'), 'utf8')

  it('is handed to the dropdown at every box', () => {
    const uses = panel.match(/<GeneratorLevelUnitSelect\b[^>]*>/g) ?? []
    expect(uses.length).toBeGreaterThan(0)
    expect(uses.filter((use) => !use.includes(':stimulus='))).toEqual([])
  })

  it('is never a store the dropdown chose for itself', () => {
    // Neither the singleton nor a default that falls back to it: a default is how this returns,
    // and it returns silently.
    expect(select).not.toMatch(/useGeneratorStimulus\s*\(/)
    expect(select).not.toMatch(/withDefaults/)
  })
})
