import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type MeasurementType, type Project, type ProjectSession } from '../api/client'
import { useConfirm } from './useConfirm'
import { useProjects } from './useProjects'

const amp: Project = { id: 1, name: 'GM70 amp', notes: null, createdUtc: '2026-09-19T10:00:00Z' }

function type(id: number, name: string): MeasurementType {
  return { id, projectId: 1, kind: 'FftSpectrum', name, notes: null, paramsJson: '{}', sortOrder: id, createdUtc: '' }
}

/**
 * Lets the question reach the dialog: `deleteMeasurementType` awaits the result count before it
 * asks, so the state is not set in the same microtask the call is made in.
 */
async function pendingQuestion() {
  for (let i = 0; i < 20 && useConfirm().state.value === null; i++) await Promise.resolve()
  return useConfirm().state.value
}

/**
 * Deleting a measurement type takes every result ever saved under it with it (see
 * ProjectRepository.DeleteTypeAsync), the server never refuses, and the sidebar's ✕ is one hover
 * away from the row's own name — which is how a stray click took a project file's
 * "Amp 1 kHz at 1 W" and its five stored results on 2026-10-02. The confirmation therefore lives in
 * the composable, where no caller can skip it, and these pin that: nothing reaches the server until
 * the question is answered yes, and the question carries a count read at the moment it is asked.
 */
describe('useProjects: deleting a measurement type', () => {
  let stored: MeasurementType[]

  beforeEach(async () => {
    vi.restoreAllMocks()
    stored = [type(11, 'Amp 1 kHz at 1 W'), type(12, 'Loopback')]
    vi.spyOn(api, 'getProjectSession').mockResolvedValue({
      open: true, path: 'C:\\P\\amp.tbench', project: amp,
    } satisfies ProjectSession)
    vi.spyOn(api, 'listMeasurementTypes').mockImplementation(async () => stored.map((t) => ({ ...t })))
    vi.spyOn(api, 'listRecentProjects').mockResolvedValue([])
    vi.spyOn(api, 'countMeasurementTypeResults').mockResolvedValue({ count: 5 })
    vi.spyOn(api, 'deleteMeasurementType').mockImplementation(async (id) => {
      stored = stored.filter((t) => t.id !== id)
    })

    await useProjects().refreshSession()
    await useProjects().loadTypes(1)
  })

  afterEach(() => {
    useConfirm().answer(false)
    vi.restoreAllMocks()
  })

  it('asks before anything is deleted, and Cancel leaves the measurement and its results alone', async () => {
    const pending = useProjects().deleteMeasurementType(11, 1)

    const question = await pendingQuestion()
    expect(question?.danger).toBe(true)
    expect(question?.message).toContain('"Amp 1 kHz at 1 W"')
    useConfirm().answer(false)

    expect(await pending).toBe(false)
    expect(api.deleteMeasurementType).not.toHaveBeenCalled()
    expect(useProjects().types.value.map((t) => t.id)).toEqual([11, 12])
  })

  it('deletes it once the question is answered yes, and reloads the list', async () => {
    const pending = useProjects().deleteMeasurementType(11, 1)

    await pendingQuestion()
    useConfirm().answer(true)

    expect(await pending).toBe(true)
    expect(api.deleteMeasurementType).toHaveBeenCalledWith(11)
    expect(useProjects().types.value.map((t) => t.id)).toEqual([12])
  })

  /**
   * The count is the part of what the delete destroys that the sidebar row does not show, so it is
   * read for the question rather than carried on `types` — those rows are only reloaded when a
   * *type* changes, so a count riding on them would say 0 for a measurement run and saved since the
   * project was opened.
   */
  it('names the number of stored results, read fresh for the question', async () => {
    const pending = useProjects().deleteMeasurementType(11, 1)

    expect((await pendingQuestion())?.message).toContain('All 5 results')
    expect(api.countMeasurementTypeResults).toHaveBeenCalledWith(11)
    useConfirm().answer(false)
    await pending
  })

  it('still asks, warning the count is unknown, when the count cannot be read', async () => {
    vi.spyOn(api, 'countMeasurementTypeResults').mockRejectedValue(new Error('409 no project open'))

    const pending = useProjects().deleteMeasurementType(11, 1)

    const question = await pendingQuestion()
    expect(question).not.toBeNull()
    expect(question?.message).toContain('could not be read')
    expect(question?.message).not.toContain('no saved results')
    useConfirm().answer(false)

    expect(await pending).toBe(false)
    expect(api.deleteMeasurementType).not.toHaveBeenCalled()
  })
})
