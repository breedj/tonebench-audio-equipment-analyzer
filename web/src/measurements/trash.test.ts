import { describe, expect, it } from 'vitest'
import type { TrashListing, TrashTypeEntry } from '../api/client'
import {
  deletePermanentlyConfirmation,
  emptyTrashConfirmation,
  toTrashGroups,
  trashCount,
  trashedMeasurementTypeIds,
} from './trash'

function type(over: Partial<TrashTypeEntry> = {}): TrashTypeEntry {
  return {
    measurementTypeId: 1,
    name: 'Amp 1 kHz',
    kind: 'FFTSpectrum',
    deletedUtc: '2026-10-05T10:00:00.000Z',
    restorableResultCount: 0,
    nameTaken: false,
    results: [],
    ...over,
  }
}

function result(resultId: number, name = 'run') {
  return {
    resultId,
    name,
    notes: null,
    timestampUtc: '2026-10-01T09:00:00.000Z',
    deletedUtc: '2026-10-06T11:00:00.000Z',
  }
}

function listing(types: TrashTypeEntry[]): TrashListing {
  return {
    types,
    totalCount:
      types.filter((t) => t.deletedUtc !== null).length + types.reduce((n, t) => n + t.results.length, 0),
  }
}

describe('toTrashGroups', () => {
  it('lets a deleted measurement be restored', () => {
    const [group] = toTrashGroups(listing([type({ restorableResultCount: 3 })]))

    expect(group.canRestore).toBe(true)
    expect(group.restoreBlockedReason).toBeNull()
    expect(group.restorableResultCount).toBe(3)
  })

  /**
   * The user's own rule: a measurement that is still there is not in the trash, so there is nothing
   * to put back. It is listed only because results of its own are.
   */
  it('shows a measurement that is not deleted as a header that cannot be restored', () => {
    const [group] = toTrashGroups(listing([type({ deletedUtc: null, results: [result(7)] })]))

    expect(group.deletedUtc).toBeNull()
    expect(group.canRestore).toBe(false)
    expect(group.restoreBlockedReason).toContain('not deleted')
  })

  /**
   * The other half of that rule, from the result's side. Restoring it under a trashed measurement
   * would leave it hidden anyway — the history list hides a result whose measurement is trashed —
   * so it would read as a restore that silently did nothing.
   */
  it('refuses a result whose own measurement is in the trash, and names it', () => {
    const [group] = toTrashGroups(
      listing([type({ name: 'Amp 1 kHz', deletedUtc: '2026-10-05T10:00:00.000Z', results: [result(7)] })]),
    )
    const [item] = group.results

    expect(item.canRestore).toBe(false)
    expect(item.restoreBlockedReason).toContain('"Amp 1 kHz"')
    expect(item.restoreBlockedReason).toContain('Restore that first')
  })

  it('allows a result whose measurement is still there', () => {
    const [group] = toTrashGroups(listing([type({ deletedUtc: null, results: [result(7)] })]))

    expect(group.results[0].canRestore).toBe(true)
    expect(group.results[0].restoreBlockedReason).toBeNull()
  })

  /**
   * The partial unique index of migration 013 lets a new measurement take a trashed one's name, so
   * the clash lands on the way back. The listing says so beforehand, which is what lets the view
   * disable the button instead of offering a restore that 409s.
   */
  it('refuses a restore whose name a live measurement has taken, and says which', () => {
    const [group] = toTrashGroups(listing([type({ name: 'Amp 1 kHz', nameTaken: true })]))

    expect(group.canRestore).toBe(false)
    expect(group.restoreBlockedReason).toContain('"Amp 1 kHz"')
    expect(group.restoreBlockedReason).toContain('Rename')
  })

  it('keeps each measurement with its own results', () => {
    const groups = toTrashGroups(
      listing([
        type({ measurementTypeId: 1, deletedUtc: null, results: [result(10), result(11)] }),
        type({ measurementTypeId: 2, results: [] }),
      ]),
    )

    expect(groups.map((g) => g.results.map((r) => r.resultId))).toEqual([[10, 11], []])
  })
})

describe('trashCount', () => {
  it('is the listing total, and zero before anything has been read', () => {
    expect(trashCount(null)).toBe(0)
    expect(trashCount(listing([]))).toBe(0)
    expect(trashCount(listing([type({ deletedUtc: null, results: [result(1), result(2)] })]))).toBe(2)
    expect(trashCount(listing([type({ results: [result(1)] })]))).toBe(2) // the measurement and the result
  })
})

/**
 * Emptying the trash, and a single permanent delete, are the two things in the app that really
 * cannot be undone — so these are the two places that sentence has to survive.
 */
describe('the permanent-delete confirmations', () => {
  it('says the empty cannot be undone, and counts what goes', () => {
    const options = emptyTrashConfirmation(
      listing([
        type({ measurementTypeId: 1, results: [] }),
        type({ measurementTypeId: 2, deletedUtc: null, results: [result(5), result(6)] }),
      ]),
    )

    expect(options.danger).toBe(true)
    expect(options.confirmLabel).toBe('Empty trash')
    expect(options.message).toContain('1 measurement and 2 results')
    expect(options.message).toContain('cannot be undone')
  })

  it('leaves out the half of the count that is zero, and does not say "1 results"', () => {
    expect(emptyTrashConfirmation(listing([type()])).message).toContain('1 measurement?')
    expect(emptyTrashConfirmation(listing([type()])).message).not.toContain('result')

    const oneResult = emptyTrashConfirmation(listing([type({ deletedUtc: null, results: [result(1)] })])).message
    expect(oneResult).toContain('1 result?')
    expect(oneResult).not.toContain('1 results')
    expect(oneResult).not.toContain('measurement')
  })

  it('warns that a measurement takes its results with it, and a result does not', () => {
    const measurement = deletePermanentlyConfirmation('Amp 1 kHz', 'measurement')
    expect(measurement.danger).toBe(true)
    expect(measurement.message).toContain('"Amp 1 kHz"')
    expect(measurement.message).toContain('Every result saved under it')
    expect(measurement.message).toContain('cannot be undone')

    const single = deletePermanentlyConfirmation('run 3', 'result')
    expect(single.message).toContain('"run 3"')
    expect(single.message).not.toContain('Every result saved under it')
    expect(single.message).toContain('cannot be undone')
  })
})

describe('trashedMeasurementTypeIds', () => {
  it('names the measurements an empty would destroy, so their parked runs can be dropped', () => {
    const groups = toTrashGroups({
      types: [type({ measurementTypeId: 4 }), type({ measurementTypeId: 7, name: 'Resp' })],
      totalCount: 2,
    })

    expect(trashedMeasurementTypeIds(groups)).toEqual([4, 7])
  })

  it('leaves out a measurement that is still there and only has trashed results', () => {
    // The whole rule. That group is a header, not an item: emptying takes its results and leaves
    // the measurement, so naming it would throw away the live run of a measurement the user can
    // still select.
    const groups = toTrashGroups({
      types: [
        type({ measurementTypeId: 4, deletedUtc: null, results: [result(1)] }),
        type({ measurementTypeId: 7 }),
      ],
      totalCount: 2,
    })

    expect(trashedMeasurementTypeIds(groups)).toEqual([7])
  })

  it('is empty for a trash holding nothing but results', () => {
    const groups = toTrashGroups({
      types: [type({ measurementTypeId: 4, deletedUtc: null, results: [result(1)] })],
      totalCount: 1,
    })

    expect(trashedMeasurementTypeIds(groups)).toEqual([])
  })
})
