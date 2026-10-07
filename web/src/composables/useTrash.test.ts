import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type TrashListing } from '../api/client'
import { useTrash } from './useTrash'

function listing(typeCount: number, resultCount: number): TrashListing {
  return {
    types: [
      {
        measurementTypeId: 1,
        name: 'Amp 1 kHz',
        kind: 'FFTSpectrum',
        deletedUtc: typeCount > 0 ? '2026-10-05T10:00:00.000Z' : null,
        restorableResultCount: 0,
        nameTaken: false,
        results: Array.from({ length: resultCount }, (_, i) => ({
          resultId: 100 + i,
          name: `run ${i}`,
          notes: null,
          timestampUtc: '2026-10-01T09:00:00.000Z',
          deletedUtc: '2026-10-06T11:00:00.000Z',
        })),
      },
    ],
    totalCount: typeCount + resultCount,
  }
}

/**
 * The store is a module-level singleton, so each test re-establishes it — the same idiom the
 * useProjects tests use.
 */
describe('useTrash', () => {
  beforeEach(async () => {
    vi.restoreAllMocks()
    vi.spyOn(api, 'listTrash').mockResolvedValue(listing(1, 2))
    await useTrash().load()
  })

  afterEach(() => {
    useTrash().closeTrash()
    vi.restoreAllMocks()
  })

  it('counts the measurements and the results in the trash', () => {
    expect(useTrash().count.value).toBe(3)
    expect(useTrash().groups.value).toHaveLength(1)
  })

  it('opens and closes without touching anything else', () => {
    const trash = useTrash()
    expect(trash.open.value).toBe(false)

    trash.openTrash()
    expect(trash.open.value).toBe(true)

    trash.closeTrash()
    expect(trash.open.value).toBe(false)
  })

  it('reloads the listing after a restore, and reports that it landed', async () => {
    vi.spyOn(api, 'restoreTrashedMeasurementType').mockResolvedValue(undefined)
    vi.mocked(api.listTrash).mockResolvedValue(listing(0, 0))

    expect(await useTrash().restoreType(1)).toBe(true)

    expect(api.restoreTrashedMeasurementType).toHaveBeenCalledWith(1)
    expect(useTrash().count.value).toBe(0)
    expect(useTrash().error.value).toBeNull()
  })

  /**
   * A 409 here is an ordinary answer — a name already taken, a measurement still in the trash — so
   * it is reported rather than thrown, and the listing is reloaded because the refusal is usually
   * about state that has moved on.
   */
  it('reports a refused restore without throwing, and keeps the listing', async () => {
    vi.spyOn(api, 'restoreTrashedMeasurementType').mockRejectedValue(
      new Error('409 A measurement named \'Amp 1 kHz\' already exists.'),
    )

    expect(await useTrash().restoreType(1)).toBe(false)

    expect(useTrash().error.value).toContain('already exists')
    expect(useTrash().count.value).toBe(3)
  })

  it('reloads after an empty, and after a single permanent delete', async () => {
    vi.spyOn(api, 'emptyTrash').mockResolvedValue({ measurementTypes: 1, results: 2 })
    vi.spyOn(api, 'purgeTrashedMeasurement').mockResolvedValue(undefined)
    vi.mocked(api.listTrash).mockResolvedValue(listing(0, 0))

    expect(await useTrash().empty()).toBe(true)
    expect(useTrash().count.value).toBe(0)

    expect(await useTrash().purgeResult(100)).toBe(true)
  })

  /**
   * The badge is loaded before the welcome screen has been left, so "no project open" is the normal
   * first answer — an empty trash, not something to complain about.
   */
  it('treats no project open as an empty trash rather than an error', async () => {
    vi.mocked(api.listTrash).mockRejectedValue(new Error('409 No project is open. Open or create a project first.'))

    await useTrash().load()

    expect(useTrash().count.value).toBe(0)
    expect(useTrash().error.value).toBeNull()
  })

  it('reports a trash that could not be read for any other reason', async () => {
    vi.mocked(api.listTrash).mockRejectedValue(new Error('500 something broke'))

    await useTrash().load()

    expect(useTrash().error.value).toContain('something broke')
  })

  it('puts the retention back when the server rejects it', async () => {
    vi.spyOn(api, 'getTrashRetention').mockResolvedValue({ days: 14 })
    vi.spyOn(api, 'setTrashRetention').mockRejectedValue(new Error('400 must be between 1 and 3650 days'))

    const trash = useTrash()
    const before = trash.retentionDays.value

    await trash.setRetentionDays(9999)

    expect(trash.retentionDays.value).toBe(before)
    expect(trash.error.value).toContain('between 1 and 3650')
  })

  it('adopts the days the server stored, not the ones asked for', async () => {
    vi.spyOn(api, 'setTrashRetention').mockResolvedValue({ days: 30 })

    await useTrash().setRetentionDays(30)

    expect(api.setTrashRetention).toHaveBeenCalledWith({ days: 30 })
    expect(useTrash().retentionDays.value).toBe(30)
  })
})
