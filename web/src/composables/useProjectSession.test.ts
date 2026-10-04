import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type Project, type ProjectSession } from '../api/client'
import { useConfirm } from './useConfirm'
import { useProjects } from './useProjects'
import { useProjectSession } from './useProjectSession'

const amp: Project = { id: 1, name: 'GM70 amp', notes: null, createdUtc: '2026-09-19T10:00:00Z' }

function session(project: Project | null, path: string | null): ProjectSession {
  return { open: project !== null, path, project }
}

/**
 * The composable's job is sequencing: ask about unsaved changes, stop the run, call the server,
 * then refresh the session and the recent list. Every server call is a spy; the state that
 * matters afterwards is useProjects' (module-level, hence the reset in beforeEach).
 */
describe('useProjectSession', () => {
  let current: ProjectSession

  beforeEach(async () => {
    vi.restoreAllMocks()
    current = session(null, null)
    vi.spyOn(api, 'getProjectSession').mockImplementation(async () => current)
    vi.spyOn(api, 'listMeasurementTypes').mockResolvedValue([])
    vi.spyOn(api, 'listRecentProjects').mockResolvedValue([])
    vi.spyOn(api, 'cancelRuns').mockResolvedValue(undefined as never)
    vi.spyOn(api, 'stopAcquisition').mockResolvedValue(undefined as never)
    vi.spyOn(api, 'setGenerator').mockResolvedValue(undefined as never)
    await useProjects().refreshSession()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('open: calls the server, then reflects the open project and reloads the recent list', async () => {
    const open = vi.spyOn(api, 'openProject').mockImplementation(async (path) => {
      current = session(amp, path)
      return current
    })
    const recent = vi.spyOn(api, 'listRecentProjects').mockResolvedValue([
      { path: 'C:\\P\\amp.tbench', name: amp.name, lastOpenedUtc: '2026-09-19T10:00:00Z', exists: true },
    ])

    expect(await useProjectSession().open('C:\\P\\amp.tbench')).toBe(true)

    expect(open).toHaveBeenCalledWith('C:\\P\\amp.tbench')
    expect(useProjects().currentProject.value).toEqual(amp)
    expect(useProjects().currentProjectId.value).toBe(1)
    expect(recent).toHaveBeenCalled()
  })

  it('open: with no project open, nothing is stopped first', async () => {
    vi.spyOn(api, 'openProject').mockImplementation(async (path) => {
      current = session(amp, path)
      return current
    })
    await useProjectSession().open('C:\\P\\amp.tbench')
    expect(api.stopAcquisition).not.toHaveBeenCalled()
  })

  it('close: stops the run, closes, and leaves nothing open', async () => {
    current = session(amp, 'C:\\P\\amp.tbench')
    await useProjects().refreshSession()
    const close = vi.spyOn(api, 'closeProject').mockImplementation(async () => {
      current = session(null, null)
    })

    expect(await useProjectSession().close()).toBe(true)

    expect(api.stopAcquisition).toHaveBeenCalled()
    expect(close).toHaveBeenCalled()
    expect(useProjects().currentProject.value).toBeNull()
  })

  it('close: a dirty measurement asks first, and Cancel aborts without touching the server', async () => {
    current = session(amp, 'C:\\P\\amp.tbench')
    vi.spyOn(api, 'listMeasurementTypes').mockResolvedValue([
      { id: 7, projectId: 1, kind: 'FftSpectrum', name: 'noise', notes: null, paramsJson: '{}', sortOrder: 0, createdUtc: '' },
    ])
    await useProjects().refreshSession()
    useProjects().currentTypeId.value = 7
    // As selectType would have left it: a type only counts as dirty once its settings loaded.
    useProjects().appliedTypeId.value = 7
    useProjects().markDirty()
    const close = vi.spyOn(api, 'closeProject').mockResolvedValue(undefined)

    const pending = useProjectSession().close()
    // The question is on screen; answer it the way the dialog would.
    expect(useConfirm().state.value?.title).toBe('Unsaved changes')
    useConfirm().answer(false)

    expect(await pending).toBe(false)
    expect(close).not.toHaveBeenCalled()
    expect(useProjects().currentProject.value).toEqual(amp)
  })

  it('delete: confirms with the path, then deletes that path', async () => {
    current = session(amp, 'C:\\P\\amp.tbench')
    await useProjects().refreshSession()
    const del = vi.spyOn(api, 'deleteProject').mockImplementation(async () => {
      current = session(null, null)
    })

    const pending = useProjectSession().remove()
    await Promise.resolve()
    await Promise.resolve()
    expect(useConfirm().state.value?.danger).toBe(true)
    expect(useConfirm().state.value?.message).toContain('C:\\P\\amp.tbench')
    useConfirm().answer(true)

    expect(await pending).toBe(true)
    expect(del).toHaveBeenCalledWith('C:\\P\\amp.tbench')
    expect(useProjects().currentProject.value).toBeNull()
  })

  it('save as: saves dirty types first, then switches to the copy', async () => {
    current = session(amp, 'C:\\P\\amp.tbench')
    await useProjects().refreshSession()
    const order: string[] = []
    const saveAs = vi.spyOn(api, 'saveProjectAs').mockImplementation(async (req) => {
      order.push('saveAs')
      current = session({ ...amp, name: req.name }, req.path)
      return current
    })

    const done = await useProjectSession().saveAs(
      { name: 'copy', notes: null, path: 'C:\\P\\copy.tbench', includeResults: true },
      async () => {
        order.push('saveDirty')
      },
    )

    expect(done).toBe(true)
    expect(order).toEqual(['saveDirty', 'saveAs'])
    expect(saveAs).toHaveBeenCalledWith({ name: 'copy', notes: null, path: 'C:\\P\\copy.tbench', includeResults: true })
    expect(useProjects().currentProject.value?.name).toBe('copy')
  })

  it('properties: updates the name in place without reloading the types', async () => {
    current = session(amp, 'C:\\P\\amp.tbench')
    await useProjects().refreshSession()
    const listTypes = vi.spyOn(api, 'listMeasurementTypes').mockResolvedValue([])
    vi.spyOn(api, 'updateProjectProperties').mockImplementation(async (req) => {
      current = session({ ...amp, name: req.name, notes: req.notes }, current.path)
      return current
    })

    await useProjectSession().updateProperties({ name: 'renamed', notes: 'd' })

    expect(useProjects().currentProject.value?.name).toBe('renamed')
    expect(listTypes).not.toHaveBeenCalled()
  })
})
