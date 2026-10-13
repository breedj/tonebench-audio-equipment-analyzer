import { describe, expect, it } from 'vitest'
import { genericSettingsSection, groupSettingsRows } from './generic'
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
