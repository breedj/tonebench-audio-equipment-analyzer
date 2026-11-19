import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import type { ScopeSliceMeasurements } from '../api/client'
import { SCOPE_MEASUREMENT_ICONS } from './measurementIcons'
import {
  DEFAULT_SCOPE_MEASUREMENTS,
  parseScopeMeasurementIds,
  SCOPE_MEASUREMENTS,
  SCOPE_MEASUREMENT_GROUPS,
  scopeMeasurement,
  scopeMeasurementsIn,
  scopeMeasurementsNeedPair,
} from './measurementCatalogue'

describe('SCOPE_MEASUREMENTS', () => {
  it('has one entry per id', () => {
    const ids = SCOPE_MEASUREMENTS.map((d) => d.id)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('gives every entry a figure to be picked by', () => {
    // The whole value of the picker is telling +Width from −Width at a glance, which a missing
    // figure takes away without anything failing.
    for (const def of SCOPE_MEASUREMENTS) {
      expect(SCOPE_MEASUREMENT_ICONS[def.icon], `${def.id} has no icon`).toBeDefined()
    }
  })

  it('reads either a channel or the pair, never neither and never both', () => {
    for (const def of SCOPE_MEASUREMENTS) {
      const channel = typeof def.read === 'function'
      const pair = typeof def.readPair === 'function'
      expect(channel !== pair, `${def.id} reads ${channel && pair ? 'both' : 'neither'}`).toBe(true)
      // The Delay group is the pair group and nothing else is.
      expect(pair).toBe(def.group === 'Delay')
    }
  })

  it('puts every entry in a tab the picker offers', () => {
    const groups = new Set(SCOPE_MEASUREMENT_GROUPS.map((g) => g.value))
    for (const def of SCOPE_MEASUREMENTS) expect(groups.has(def.group)).toBe(true)
    // And every tab has something in it, or it is a tab that reads as broken.
    for (const group of SCOPE_MEASUREMENT_GROUPS) {
      expect(scopeMeasurementsIn(group.value).length, `${group.value} is empty`).toBeGreaterThan(0)
    }
  })

  it('names a field the wire actually carries', () => {
    // The trap this replaces a compiler check for: a reader that looks up a field the server does
    // not send answers undefined on every record, which draws a dash for ever rather than failing.
    const wire = readFileSync(resolve(__dirname, '../api/client.ts'), 'utf8')
    const block = wire.slice(
      wire.indexOf('export interface ScopeSliceMeasurements {'),
      wire.indexOf('\n}', wire.indexOf('export interface ScopeSliceMeasurements {')),
    )
    const measured = {} as ScopeSliceMeasurements
    for (const def of SCOPE_MEASUREMENTS) {
      if (!def.read) continue
      // Call it to find out which key it touches, through a proxy that records the lookup.
      let touched: string | null = null
      const probe = new Proxy(measured, {
        get(_t, key) {
          if (typeof key === 'string') touched = key
          return undefined
        },
      })
      def.read(probe)
      expect(touched, `${def.id} reads nothing`).not.toBeNull()
      expect(block, `${def.id} reads ${touched}, which the wire does not carry`).toContain(`${touched}`)
    }
  })

  it('writes every reading to a quantity, so a statistic of it is in the same units', () => {
    const quantities = new Set(['level', 'time', 'frequency', 'percent', 'ratio', 'angle', 'area'])
    for (const def of SCOPE_MEASUREMENTS) expect(quantities.has(def.quantity)).toBe(true)
  })

  it('explains every entry, because the label is four characters', () => {
    for (const def of SCOPE_MEASUREMENTS) {
      expect(def.title.length, `${def.id} has no explanation`).toBeGreaterThan(20)
    }
  })
})

describe('parseScopeMeasurementIds', () => {
  it('drops an id this build cannot read', () => {
    // A measurement from a newer build has nothing here that can take it, and a row that is always
    // blank reads as a fault rather than as a reading that is not available.
    expect(parseScopeMeasurementIds(['pkpk', 'fromTheFuture', 'freq'])).toEqual(['pkpk', 'freq'])
  })

  it('imposes the catalogue order rather than the stored one', () => {
    // So the table reads the same way whichever order the boxes were pressed in.
    expect(parseScopeMeasurementIds(['freq', 'pkpk'])).toEqual(['pkpk', 'freq'])
  })

  it('keeps an empty list empty, which is a reader who cleared it', () => {
    expect(parseScopeMeasurementIds([])).toEqual([])
  })

  it('answers null for anything that is not a list, which is a measurement saved before this', () => {
    expect(parseScopeMeasurementIds(undefined)).toBeNull()
    expect(parseScopeMeasurementIds(null)).toBeNull()
    expect(parseScopeMeasurementIds('pkpk')).toBeNull()
  })

  it('drops a duplicate rather than showing the row twice', () => {
    expect(parseScopeMeasurementIds(['pkpk', 'pkpk'])).toEqual(['pkpk'])
  })
})

describe('the default selection', () => {
  it('is readings this build knows', () => {
    for (const id of DEFAULT_SCOPE_MEASUREMENTS) expect(scopeMeasurement(id)).not.toBeNull()
  })

  it('needs no X–Y figure, so the commonest state asks for nothing extra', () => {
    expect(scopeMeasurementsNeedPair(DEFAULT_SCOPE_MEASUREMENTS)).toBe(false)
  })
})

describe('scopeMeasurementsNeedPair', () => {
  it('is true for anything in the Channel delay group and false otherwise', () => {
    expect(scopeMeasurementsNeedPair(['pkpk', 'phase'])).toBe(true)
    expect(scopeMeasurementsNeedPair(['pkpk', 'rms'])).toBe(false)
    expect(scopeMeasurementsNeedPair([])).toBe(false)
  })
})
