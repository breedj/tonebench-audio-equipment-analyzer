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
  scopeMeasurementSection,
  scopeMeasurementSections,
  SCOPE_MEASUREMENT_SECTIONS,
  scopeMeasurementsIn,
  scopeMeasurementsNeedPair,
  removeScopeSelectionRow,
  scopeSelectionEntry,
  scopeSelectionId,
  scopeSelectionSource,
  scopeSelectionSources,
  SCOPE_SELECTION_SOURCES,
} from './measurementCatalogue'

describe('the picker’s sections', () => {
  /**
   * The one that matters. A measurement whose section belongs to another tab — or to no tab at all
   * — is dropped out of the dialog by <code>groupIntoSections</code> with nothing to say so: the
   * tile is simply not there, and the reading can never be ticked again. It is also exactly the
   * mistake a new entry makes, since the section is the one field that has to agree with a field
   * beside it.
   */
  it('shows every entry of a tab, exactly once, under one of that tab’s own sections', () => {
    for (const { value: group } of SCOPE_MEASUREMENT_GROUPS) {
      const shown = scopeMeasurementSections(group).flatMap((entry) => entry.items)
      expect(shown.map((d) => d.id).sort(), group).toEqual(
        scopeMeasurementsIn(group)
          .map((d) => d.id)
          .sort(),
      )
      expect(new Set(shown).size, group).toBe(shown.length)
    }
  })

  it('keeps the catalogue’s order inside a section', () => {
    const peaks = scopeMeasurementSections('Vertical')[0]
    expect(peaks.section.id).toBe('peaks')
    expect(peaks.items.map((d) => d.id)).toEqual(['max', 'min', 'pkpk'])
  })

  it('defines no section that nothing is in, and none that two tabs share', () => {
    const ids = SCOPE_MEASUREMENT_SECTIONS.map((s) => s.id)
    expect(new Set(ids).size).toBe(ids.length)
    for (const section of SCOPE_MEASUREMENT_SECTIONS) {
      const members = SCOPE_MEASUREMENTS.filter((d) => d.section === section.id)
      expect(members.length, `${section.id} holds nothing`).toBeGreaterThan(0)
      for (const def of members) expect(def.group, def.id).toBe(section.group)
    }
  })

  it('answers one measurement’s section, for the table that draws its figure afterwards', () => {
    // The readings table draws a row in its section's colours, so what it needs is this lookup --
    // and an id with no section would silently fall back to an uncoloured figure, which reads as
    // one row that was missed rather than as a catalogue entry that is wrong.
    expect(scopeMeasurementSection('pkpk')?.id).toBe('peaks')
    expect(scopeMeasurementSection('rise')?.id).toBe('edges')
    expect(scopeMeasurementSection('correlation')?.id).toBe('match')
    expect(scopeMeasurementSection('nonesuch')).toBeNull()
    for (const def of SCOPE_MEASUREMENTS) {
      expect(scopeMeasurementSection(def.id), def.id).not.toBeNull()
    }
  })

  it('gives every section a heading worth reading', () => {
    for (const section of SCOPE_MEASUREMENT_SECTIONS) {
      expect(section.label.length, section.id).toBeGreaterThan(3)
      // Short is fine -- "DC content" is the whole of what that section has in common -- but
      // a section with no note at all is a badge the reader has to guess the meaning of.
      expect(section.note.length, section.id).toBeGreaterThan(5)
    }
  })

  it('tells one section from the next inside a tab, which is what the colour is for', () => {
    for (const { value: group } of SCOPE_MEASUREMENT_GROUPS) {
      const tones = SCOPE_MEASUREMENT_SECTIONS.filter((s) => s.group === group).map((s) => s.tone)
      expect(new Set(tones).size, group).toBe(tones.length)
    }
  })
})

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

describe('a selection entry', () => {
  it('is an id, or an id and the source it is taken off', () => {
    expect(scopeSelectionId('pkpk')).toBe('pkpk')
    expect(scopeSelectionSource('pkpk')).toBeNull()

    expect(scopeSelectionId('pkpk:ch1')).toBe('pkpk')
    expect(scopeSelectionSource('pkpk:ch1')).toBe('ch1')

    expect(scopeSelectionEntry('rms', 'math')).toBe('rms:math')
    expect(scopeSelectionEntry('rms', null)).toBe('rms')
  })

  /**
   * The rule the whole change rests on: a bare id is every source, which is exactly what it meant
   * before the picker had a Source control. Nothing stored needs migrating because of it.
   */
  it('reads a bare id as every source', () => {
    expect(scopeSelectionSources('pkpk')).toEqual(SCOPE_SELECTION_SOURCES)
    expect(scopeSelectionSources('pkpk:ch0')).toEqual(['ch0'])
  })

  /** A pair reading has no source: it reads the two inputs together and has one row of its own. */
  it('gives a Channel delay entry no sources at all', () => {
    expect(scopeSelectionSources('phase')).toEqual([])
    expect(scopeSelectionSources('phase:ch0')).toEqual([])
  })

  it('still knows when the X–Y figure is needed, whatever the entry looks like', () => {
    expect(scopeMeasurementsNeedPair(['pkpk:ch0'])).toBe(false)
    expect(scopeMeasurementsNeedPair(['pkpk:ch0', 'phase'])).toBe(true)
  })
})

describe('parseScopeMeasurementIds, with sources', () => {
  it('keeps one measurement once per source it was asked for on', () => {
    expect(parseScopeMeasurementIds(['pkpk:ch1', 'pkpk:ch0'])).toEqual(['pkpk:ch0', 'pkpk:ch1'])
  })

  it('orders by the catalogue first and the source second', () => {
    // rms comes after pkpk in the catalogue, whichever order they were pressed in.
    expect(parseScopeMeasurementIds(['rms:ch0', 'pkpk:math1', 'pkpk:ch0'])).toEqual([
      'pkpk:ch0',
      'pkpk:math1',
      'rms:ch0',
    ])
  })

  /**
   * There was one math channel until 2026-11-20, spelled `math`; there are two. An unknown source
   * is dropped by the loop below, so without the rename every stored selection would silently lose
   * its math rows — a setting disappearing rather than changing.
   */
  it('reads the one math channel’s old source name as the first of the two', () => {
    expect(parseScopeMeasurementIds(['pkpk:math'])).toEqual(['pkpk:math1'])
    expect(parseScopeMeasurementIds(['rms:math', 'rms:math2'])).toEqual(['rms:math1', 'rms:math2'])
  })

  it('still drops a source it has never had', () => {
    expect(parseScopeMeasurementIds(['pkpk:ch9', 'pkpk:stim'])).toEqual([])
  })

  it('reads a bare id, which is everything stored before this existed', () => {
    expect(parseScopeMeasurementIds(['pkpk', 'rms'])).toEqual(['pkpk', 'rms'])
  })

  it('drops an entry whose measurement this build does not know', () => {
    expect(parseScopeMeasurementIds(['nonesuch:ch0', 'pkpk:ch0'])).toEqual(['pkpk:ch0'])
  })

  it('drops a source this build does not know, rather than keeping a row nothing can draw', () => {
    expect(parseScopeMeasurementIds(['pkpk:ch7'])).toEqual([])
  })

  it('is still null for something that is not a list', () => {
    expect(parseScopeMeasurementIds('pkpk')).toBeNull()
    expect(parseScopeMeasurementIds(undefined)).toBeNull()
  })
})

describe('removeScopeSelectionRow', () => {
  it('takes one source off and leaves the measurement on the others', () => {
    expect(removeScopeSelectionRow(['pkpk:ch0', 'pkpk:ch1'], 'pkpk:ch0')).toEqual(['pkpk:ch1'])
  })

  /**
   * A bare id is shorthand for every source and there is no shorter way to say "all but this one",
   * so crossing one row off expands it. The alternative — dropping the measurement outright — is
   * what the cross did before a source could be chosen, and is not what it means now.
   */
  it('expands a bare entry rather than dropping the whole measurement', () => {
    expect(removeScopeSelectionRow(['pkpk'], 'pkpk:ch0')).toEqual([
      'pkpk:ch1',
      'pkpk:math1',
      'pkpk:math2',
    ])
  })

  it('leaves other measurements alone', () => {
    expect(removeScopeSelectionRow(['rms:ch0', 'pkpk:ch0'], 'pkpk:ch0')).toEqual(['rms:ch0'])
  })

  it('takes a pair row off whole, there being no source on it', () => {
    expect(removeScopeSelectionRow(['phase', 'pkpk:ch0'], 'phase:pair')).toEqual(['pkpk:ch0'])
  })

  it('changes nothing for a row that is not in the selection', () => {
    expect(removeScopeSelectionRow(['pkpk:ch0'], 'rms:ch0')).toEqual(['pkpk:ch0'])
  })
})
