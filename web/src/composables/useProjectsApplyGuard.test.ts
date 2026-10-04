import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type MeasurementTypeAppliedResponse, type Project, type ProjectSession } from '../api/client'
import { useProjects } from './useProjects'

const amp: Project = { id: 1, name: 'GM70 amp', notes: null, createdUtc: '2026-09-19T10:00:00Z' }

/**
 * A save writes the *live* state into a type, so it may only write the type that state came
 * from. On 2026-09-21 an FFT type whose stored settings could not be applied (a 500 on selection)
 * was then autosaved from the screen state the previous type had left behind, which turned one
 * failed load into a permanently broken type. These pin the guard: a type whose apply failed
 * cannot become dirty, cannot be saved, and a later successful apply lifts both.
 */
describe('useProjects: a type whose settings failed to load', () => {
  beforeEach(async () => {
    vi.restoreAllMocks()
    vi.spyOn(api, 'getProjectSession').mockResolvedValue({ open: true, path: 'C:\\P\\amp.tbench', project: amp } satisfies ProjectSession)
    vi.spyOn(api, 'listMeasurementTypes').mockResolvedValue([
      { id: 7, projectId: 1, kind: 'FftSpectrum', name: 'broken', notes: null, paramsJson: '{}', sortOrder: 0, createdUtc: '' },
      { id: 8, projectId: 1, kind: 'FftSpectrum', name: 'fine', notes: null, paramsJson: '{}', sortOrder: 1, createdUtc: '' },
    ])
    vi.spyOn(api, 'listRecentProjects').mockResolvedValue([])
    vi.spyOn(api, 'cancelRuns').mockResolvedValue(undefined as never)
    vi.spyOn(api, 'stopAcquisition').mockResolvedValue(undefined as never)
    vi.spyOn(api, 'setGenerator').mockResolvedValue(undefined as never)
    await useProjects().refreshSession()
    await useProjects().loadTypes(1)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('cannot be dirtied or saved, so the previous screen state is never written over it', async () => {
    vi.spyOn(api, 'applyMeasurementType').mockRejectedValue(new Error('500 Object reference not set'))
    const update = vi.spyOn(api, 'updateMeasurementType').mockResolvedValue(undefined as never)
    const projects = useProjects()

    await expect(projects.selectType(7)).rejects.toThrow('500')
    expect(projects.currentTypeId.value).toBe(7)
    expect(projects.appliedTypeId.value).toBeNull()

    projects.markDirty()
    expect(projects.dirtyTypeIds.value.has(7)).toBe(false)

    await expect(projects.saveMeasurementType(7)).rejects.toThrow(/could not be loaded/)
    expect(update).not.toHaveBeenCalled()
  })

  it('is saveable again once an apply succeeds', async () => {
    vi.spyOn(api, 'applyMeasurementType').mockResolvedValue({} as MeasurementTypeAppliedResponse)
    const update = vi.spyOn(api, 'updateMeasurementType').mockResolvedValue(undefined as never)
    const projects = useProjects()

    await projects.selectType(8)
    expect(projects.appliedTypeId.value).toBe(8)

    projects.markDirty()
    expect(projects.dirtyTypeIds.value.has(8)).toBe(true)

    await projects.saveMeasurementType(8)
    expect(update).toHaveBeenCalledTimes(1)
  })
})
