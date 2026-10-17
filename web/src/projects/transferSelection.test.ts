import { describe, expect, it } from 'vitest'
import type { ProjectContentsType, ProjectTransfer } from '../api/client'
import {
  EMPTY_SELECTION,
  clashingTypes,
  formatBytes,
  groupState,
  mergeBlocked,
  selectedBytes,
  selectedResultCount,
  setEverything,
  setGroup,
  setResult,
  transferItems,
  transferReadiness,
  transferSummary,
  unknownKinds,
  type NameClashPolicy,
  type TransferSelectionState,
} from './transferSelection'

function type(
  typeId: number,
  name: string,
  kind = 'FFTSpectrum',
  results: { resultId: number; bytes?: number }[] = [],
): ProjectContentsType {
  return {
    typeId,
    kind,
    name,
    notes: null,
    sortOrder: typeId,
    results: results.map((r) => ({
      resultId: r.resultId,
      name: `run ${r.resultId}`,
      timestampUtc: '2026-10-16T09:00:00Z',
      source: null,
      captures: 1,
      approximateBytes: r.bytes ?? 1024,
    })),
  }
}

const always = (policy: NameClashPolicy) => () => policy

describe('groupState', () => {
  it('is none for a measurement that is not ticked', () => {
    expect(groupState(type(1, 'Amp', 'FFTSpectrum', [{ resultId: 10 }]), EMPTY_SELECTION)).toBe('none')
  })

  it('is all when every result under it is ticked', () => {
    const node = type(1, 'Amp', 'FFTSpectrum', [{ resultId: 10 }, { resultId: 11 }])
    expect(groupState(node, setGroup(EMPTY_SELECTION, node, true))).toBe('all')
  })

  it('is some when only part of it is', () => {
    const node = type(1, 'Amp', 'FFTSpectrum', [{ resultId: 10 }, { resultId: 11 }])
    const partial = setResult(EMPTY_SELECTION, node, 10, true)
    expect(groupState(node, partial)).toBe('some')
  })

  it('is some for a measurement ticked with none of its results — the settings-only pick', () => {
    const node = type(1, 'Amp', 'FFTSpectrum', [{ resultId: 10 }])
    const settingsOnly = setResult(setGroup(EMPTY_SELECTION, node, true), node, 10, false)
    expect(settingsOnly.types.has(1)).toBe(true)
    expect(settingsOnly.results.size).toBe(0)
    expect(groupState(node, settingsOnly)).toBe('some')
  })

  it('is all for a ticked measurement that has no results at all', () => {
    const node = type(1, 'Never run')
    expect(groupState(node, setGroup(EMPTY_SELECTION, node, true))).toBe('all')
  })
})

describe('setGroup / setResult / setEverything', () => {
  const nodes = [
    type(1, 'Amp', 'FFTSpectrum', [{ resultId: 10 }, { resultId: 11 }]),
    type(2, 'Sweep', 'ThdVsFrequency', [{ resultId: 20 }]),
  ]

  it('replaces the sets rather than mutating them, so Vue sees the change', () => {
    const next = setGroup(EMPTY_SELECTION, nodes[0], true)
    expect(next.types).not.toBe(EMPTY_SELECTION.types)
    expect(next.results).not.toBe(EMPTY_SELECTION.results)
    expect(EMPTY_SELECTION.types.size).toBe(0)
  })

  it('unticking a measurement takes its results with it', () => {
    const all = setEverything(nodes, true)
    const without = setGroup(all, nodes[0], false)
    expect([...without.types]).toEqual([2])
    expect([...without.results]).toEqual([20])
  })

  it('ticking a result implies its measurement', () => {
    const next = setResult(EMPTY_SELECTION, nodes[1], 20, true)
    expect(next.types.has(2)).toBe(true)
  })

  it('setEverything(false) empties both sets', () => {
    expect(setEverything(nodes, false)).toEqual(EMPTY_SELECTION)
    expect(setEverything(nodes, true).results.size).toBe(3)
  })
})

describe('clashingTypes', () => {
  it('maps an incoming measurement to the one of that name already there', () => {
    const clashes = clashingTypes([type(1, 'Amp'), type(2, 'Other')], [type(90, 'Amp'), type(91, 'Unrelated')])
    expect([...clashes.keys()]).toEqual([1])
    expect(clashes.get(1)?.typeId).toBe(90)
  })

  it('compares names case-sensitively, because the database does', () => {
    // measurement_type.name carries no COLLATE NOCASE, so SQLite's unique index is a binary
    // compare and "amp" and "Amp" coexist. Warning about this clash would offer a Merge into a
    // measurement with a different name, for a transfer that would have gone through untouched.
    expect(clashingTypes([type(1, 'Amp')], [type(90, 'amp')]).size).toBe(0)
    expect(clashingTypes([type(1, 'Amp')], [type(90, 'Amp')]).size).toBe(1)
  })

  it('is empty against an empty destination', () => {
    expect(clashingTypes([type(1, 'Amp')], []).size).toBe(0)
  })
})

describe('mergeBlocked', () => {
  const incoming = [type(1, 'Amp', 'FFTSpectrum'), type(2, 'Sweep', 'ThdVsFrequency')]

  it('names a clash whose kinds differ, only under Merge', () => {
    const clashes = clashingTypes(incoming, [type(90, 'Amp', 'ThdVsFrequency'), type(91, 'Sweep', 'ThdVsFrequency')])

    expect(mergeBlocked(incoming, clashes, always('Merge'))).toEqual([1])
    expect(mergeBlocked(incoming, clashes, always('Rename'))).toEqual([])
    expect(mergeBlocked(incoming, clashes, always('Skip'))).toEqual([])
  })

  it('is empty when the kinds match', () => {
    const clashes = clashingTypes(incoming, [type(90, 'Amp', 'FFTSpectrum')])
    expect(mergeBlocked(incoming, clashes, always('Merge'))).toEqual([])
  })
})

describe('unknownKinds', () => {
  it('reports kinds this build has never heard of, once each and sorted', () => {
    expect(unknownKinds([type(1, 'a', 'FFTSpectrum'), type(2, 'b', 'Telepathy'), type(3, 'c', 'Telepathy')]))
      .toEqual(['Telepathy'])
  })

  it('is empty for a file of kinds we know', () => {
    expect(unknownKinds([type(1, 'a', 'FFTSpectrum'), type(2, 'b', 'ThdVsFrequency'), type(3, 'c', 'Impedance')]))
      .toEqual([])
  })
})

describe('selectedBytes and selectedResultCount', () => {
  const nodes = [
    type(1, 'Amp', 'FFTSpectrum', [{ resultId: 10, bytes: 1000 }, { resultId: 11, bytes: 2000 }]),
    type(2, 'Sweep', 'ThdVsFrequency', [{ resultId: 20, bytes: 4000 }]),
  ]

  it('totals only the ticked results', () => {
    const partial = setResult(EMPTY_SELECTION, nodes[0], 11, true)
    expect(selectedBytes(nodes, partial)).toBe(2000)
    expect(selectedResultCount(nodes, partial)).toBe(1)

    const all = setEverything(nodes, true)
    expect(selectedBytes(nodes, all)).toBe(7000)
    expect(selectedResultCount(nodes, all)).toBe(3)
  })

  it('counts nothing for a measurement ticked with no results', () => {
    const settingsOnly: TransferSelectionState = { types: new Set([1]), results: new Set() }
    expect(selectedBytes(nodes, settingsOnly)).toBe(0)
    expect(selectedResultCount(nodes, settingsOnly)).toBe(0)
  })
})

describe('formatBytes', () => {
  it('reads as a file manager would', () => {
    expect(formatBytes(0)).toBe('0 bytes')
    expect(formatBytes(512)).toBe('512 bytes')
    expect(formatBytes(2048)).toBe('2 kB')
    expect(formatBytes(536_870_912)).toBe('512 MB')
    expect(formatBytes(1_288_490_189)).toBe('1.2 GB')
  })

  it('never says NaN', () => {
    // A response from an older build would not carry the field at all.
    expect(formatBytes(null)).toBe('0 bytes')
    expect(formatBytes(undefined)).toBe('0 bytes')
    expect(formatBytes(Number.NaN)).toBe('0 bytes')
    expect(formatBytes(-1)).toBe('0 bytes')
  })
})

describe('transferItems', () => {
  const nodes = [
    type(1, 'Amp', 'FFTSpectrum', [{ resultId: 10 }, { resultId: 11 }]),
    type(2, 'Sweep', 'ThdVsFrequency', [{ resultId: 20 }]),
  ]

  it('sends only the ticked measurements, with only their ticked results', () => {
    const selection = setResult(EMPTY_SELECTION, nodes[0], 11, true)
    expect(transferItems(nodes, selection)).toEqual([{ typeId: 1, resultIds: [11] }])
  })

  it('sends a ticked measurement with an empty result list — settings only', () => {
    expect(transferItems(nodes, { types: new Set([2]), results: new Set() })).toEqual([{ typeId: 2, resultIds: [] }])
  })

  it('carries a per-measurement policy only where one was chosen', () => {
    const items = transferItems(nodes, setEverything(nodes, true), new Map([[1, 'Merge' as NameClashPolicy]]))
    expect(items[0].onNameClash).toBe('Merge')
    expect('onNameClash' in items[1]).toBe(false)
  })
})

describe('transferReadiness', () => {
  const nodes = [type(1, 'Amp', 'FFTSpectrum', [{ resultId: 10 }])]
  const base = { types: nodes, hasTarget: true, busy: false, blocked: [] as number[], policyFor: always('Rename') }

  it('is not ready with nothing ticked', () => {
    const { ready, reason } = transferReadiness({ ...base, selection: EMPTY_SELECTION })
    expect(ready).toBe(false)
    expect(reason).toMatch(/Tick at least one/)
  })

  it('is not ready without a file chosen', () => {
    const { ready, reason } = transferReadiness({ ...base, hasTarget: false, selection: setEverything(nodes, true) })
    expect(ready).toBe(false)
    expect(reason).toMatch(/Choose a file/)
  })

  it('is not ready while busy, and says nothing — the footer is showing progress', () => {
    const { ready, reason } = transferReadiness({ ...base, busy: true, selection: setEverything(nodes, true) })
    expect(ready).toBe(false)
    expect(reason).toBeNull()
  })

  it('is not ready when a merge is blocked, and says how many', () => {
    const one = transferReadiness({ ...base, blocked: [1], selection: setEverything(nodes, true) })
    expect(one.ready).toBe(false)
    expect(one.reason).toMatch(/One measurement cannot be merged/)

    const two = transferReadiness({ ...base, blocked: [1, 2], selection: setEverything(nodes, true) })
    expect(two.reason).toMatch(/^2 measurements cannot be merged/)
  })

  it('is not ready when everything ticked would be skipped', () => {
    // Running it and reporting nothing copied is worse than refusing to start.
    const { ready, reason } = transferReadiness({
      ...base,
      policyFor: always('Skip'),
      selection: setEverything(nodes, true),
    })
    expect(ready).toBe(false)
    expect(reason).toMatch(/nothing would be copied/)
  })

  it('is ready with something ticked, a file chosen and nothing blocked', () => {
    expect(transferReadiness({ ...base, selection: setEverything(nodes, true) })).toEqual({ ready: true, reason: null })
  })
})

describe('transferSummary', () => {
  function report(types: ProjectTransfer['types'], resultsCopied = 1): ProjectTransfer {
    return { types, resultsCopied, capturesCopied: resultsCopied }
  }

  it('counts in the singular and the plural', () => {
    expect(
      transferSummary(report([{ sourceTypeId: 1, sourceName: 'Amp', destinationTypeId: 9, destinationName: 'Amp', outcome: 'Created' }], 1), 'imported'),
    ).toBe('1 result imported · 1 new measurement.')

    expect(
      transferSummary(
        report(
          [
            { sourceTypeId: 1, sourceName: 'Amp', destinationTypeId: 9, destinationName: 'Amp', outcome: 'Created' },
            { sourceTypeId: 2, sourceName: 'Sweep', destinationTypeId: 8, destinationName: 'Sweep', outcome: 'Created' },
          ],
          4,
        ),
        'exported',
      ),
    ).toBe('4 results exported · 2 new measurements.')
  })

  it('names what was renamed and what was skipped', () => {
    const summary = transferSummary(
      report(
        [
          { sourceTypeId: 1, sourceName: 'Amp', destinationTypeId: 9, destinationName: 'Amp (2)', outcome: 'Created' },
          { sourceTypeId: 2, sourceName: 'Sweep', destinationTypeId: 8, destinationName: 'Sweep', outcome: 'Merged' },
          { sourceTypeId: 3, sourceName: 'Noise', destinationTypeId: null, destinationName: null, outcome: 'Skipped' },
        ],
        2,
      ),
      'imported',
    )

    expect(summary).toContain('renamed “Amp (2)”')
    expect(summary).toContain('1 measurement merged')
    expect(summary).toContain('skipped “Noise”')
  })

  it('does not claim a rename for a measurement that kept its name', () => {
    const summary = transferSummary(
      report([{ sourceTypeId: 1, sourceName: 'Amp', destinationTypeId: 9, destinationName: 'Amp', outcome: 'Created' }]),
      'imported',
    )
    expect(summary).not.toContain('renamed')
  })
})
