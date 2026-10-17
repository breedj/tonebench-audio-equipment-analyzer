import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type Project, type ProjectSession } from '../api/client'
import { useProjects } from './useProjects'

const amp: Project = { id: 1, name: 'GM70 amp', notes: null, createdUtc: '2026-10-17T10:00:00Z' }

/**
 * Selecting a measurement stops whatever is running — `selectType` calls deviceControl's
 * `stopRun`, which cancels every runner — so until 2026-10-17 a stray click on the sidebar threw
 * away a sweep minutes into its grid, without asking and without saying so. Joost's call is that
 * the run wins: the switch is refused while anything is measuring, and the way out is the screen's
 * own Stop or Cancel.
 */
describe('useProjects: selecting another measurement while one is running', () => {
  beforeEach(async () => {
    vi.restoreAllMocks()
    vi.spyOn(api, 'getProjectSession').mockResolvedValue({ open: true, path: 'C:\\P\\amp.tbench', project: amp } satisfies ProjectSession)
    vi.spyOn(api, 'listMeasurementTypes').mockResolvedValue([
      { id: 7, projectId: 1, kind: 'ThdVsAmplitude', name: 'sweeping', notes: null, paramsJson: '{}', sortOrder: 0, createdUtc: '' },
      { id: 8, projectId: 1, kind: 'FftSpectrum', name: 'other', notes: null, paramsJson: '{}', sortOrder: 1, createdUtc: '' },
    ])
    vi.spyOn(api, 'listRecentProjects').mockResolvedValue([])
    vi.spyOn(api, 'cancelRuns').mockResolvedValue(undefined as never)
    vi.spyOn(api, 'stopAcquisition').mockResolvedValue(undefined as never)
    vi.spyOn(api, 'setGenerator').mockResolvedValue(undefined as never)
    vi.spyOn(api, 'applyMeasurementType').mockResolvedValue({ spectrum: null, generator: null, acquisition: null } as never)

    const projects = useProjects()
    projects.setRunActive(false)
    await projects.refreshSession()
    await projects.loadTypes(1)
    await projects.selectType(7)
  })

  afterEach(() => {
    useProjects().setRunActive(false)
    vi.restoreAllMocks()
  })

  it('is refused, with a reason, and nothing is stopped on the way', async () => {
    const projects = useProjects()
    const cancel = vi.spyOn(api, 'cancelRuns')
    projects.setRunActive(true)

    await projects.selectType(8)

    expect(projects.currentTypeId.value).toBe(7)
    expect(projects.error.value).toMatch(/running/i)
    // The whole point: the run is still running. stopRun is what a switch would have called.
    expect(cancel).not.toHaveBeenCalled()
  })

  it('still lets the running measurement be re-selected, since that changes nothing', async () => {
    const projects = useProjects()
    projects.setRunActive(true)

    await projects.selectType(7)

    expect(projects.currentTypeId.value).toBe(7)
    expect(projects.error.value).toBeNull()
  })

  it('goes through again once the run ends, and the refusal does not outlive it', async () => {
    const projects = useProjects()
    projects.setRunActive(true)
    await projects.selectType(8)
    expect(projects.error.value).not.toBeNull()

    projects.setRunActive(false)
    expect(projects.error.value).toBeNull()

    await projects.selectType(8)
    expect(projects.currentTypeId.value).toBe(8)
  })
})
