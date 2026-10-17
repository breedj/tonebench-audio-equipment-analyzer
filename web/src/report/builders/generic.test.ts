import { describe, expect, it } from 'vitest'
import { genericSettingsRows, genericSettingsSection, groupSettingsRows, humanizeKey } from './generic'
import type { ReportRow } from '../../api/client'

const rows = (...labels: string[]): ReportRow[] => labels.map((label) => ({ label, value: label.toLowerCase() }))

describe('grouping the as-run settings', () => {
  it('puts every row in exactly one group', () => {
    // The property that matters. A settings list that quietly drops a row is a report that lies
    // about what ran, and the keyword table can only ever be a guess at where a row belongs.
    const input = rows(
      'Sample rate',
      'FFT size',
      'Band',
      'Harmonics',
      'Input range',
      'Generator unit',
      'Scope periods',
      'Something nobody has named',
    )
    const grouped = groupSettingsRows(input)

    const seen = grouped.flatMap((g) => g.rows.map((r) => r.label))
    expect(seen.slice().sort()).toEqual(input.map((r) => r.label).sort())
    expect(new Set(seen).size).toBe(input.length)
  })

  it('keeps the unmatched rows rather than dropping them, in a group of their own', () => {
    const grouped = groupSettingsRows(rows('Wibble', 'Sample rate'))
    const other = grouped.find((g) => g.name === 'Other settings')
    expect(other?.rows.map((r) => r.label)).toEqual(['Wibble'])
  })

  it('names the groups in their own order, and only the ones that have rows', () => {
    expect(groupSettingsRows(rows('Band', 'Sample rate')).map((g) => g.name)).toEqual(['Acquisition', 'Analysis'])
    expect(groupSettingsRows([])).toEqual([])
  })
})

describe('spelling a key nobody has labelled', () => {
  // The three the 2026-10-17 report named, each a row off the FFT screen's own params_json.
  it('spells the units and the acronyms the way the screen does', () => {
    expect(humanizeKey('dutGainDb')).toBe('DUT gain dB')
    expect(humanizeKey('sampleRateHz')).toBe('Sample rate Hz')
    expect(humanizeKey('inputRangeDbv')).toBe('Input range dBV')
    expect(humanizeKey('fftSize')).toBe('FFT size')
    expect(humanizeKey('captureDelayMs')).toBe('Capture delay ms')
  })

  it('leaves a word nobody listed as the naive spelling had it', () => {
    expect(humanizeKey('frequencySpanHz')).toBe('Frequency span Hz')
    expect(humanizeKey('wibble')).toBe('Wibble')
    expect(humanizeKey('notch_octaves')).toBe('Notch octaves')
  })

  it('moves an ohms suffix off the label and onto the value', () => {
    expect(genericSettingsRows(JSON.stringify({ loadOhms: 8, programmableLoadOhms: 4 }))).toEqual([
      { label: 'Load', value: '8 Ω' },
      { label: 'Programmable load', value: '4 Ω' },
    ])
  })

  it('quotes a unit setting the way its own dropdown spells it', () => {
    expect(genericSettingsRows(JSON.stringify({ generatorLevelUnit: 'Dbv', levelUnit: 'MilliWatt' }))).toEqual([
      { label: 'Generator level unit', value: 'dBV' },
      { label: 'Level unit', value: 'mW' },
    ])
  })

  it('hands back a unit name it does not know rather than dropping the row', () => {
    expect(genericSettingsRows(JSON.stringify({ someUnit: 'Furlong' }))).toEqual([{ label: 'Some unit', value: 'Furlong' }])
  })
})

describe('the generic settings section', () => {
  const params = (keys: string[]) => JSON.stringify(Object.fromEntries(keys.map((k, i) => [k, i])))

  it('stays one list while it is short enough to read down a column', () => {
    const section = genericSettingsSection(params(['sampleRateHz', 'fftSize', 'window']))
    expect(section.kind).toBe('keyValues')
    expect(section.rows).toHaveLength(3)
  })

  it('becomes boxes once it is long', () => {
    const section = genericSettingsSection(params(['sampleRateHz', 'fftSize', 'window', 'band', 'harmonics', 'inputRange', 'scopePeriods']))
    expect(section.kind).toBe('groups')
    expect(section.groups!.flatMap((g) => g.rows)).toHaveLength(7)
  })

  it('says so when a measurement recorded nothing', () => {
    expect(genericSettingsSection('{}').kind).toBe('paragraph')
    expect(genericSettingsSection('not json').kind).toBe('paragraph')
  })
})
