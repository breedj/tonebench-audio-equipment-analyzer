import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type MeasurementType, type MeasurementTypeAppliedResponse, type Project, type ProjectSession } from '../api/client'
import { useProjects } from './useProjects'

const amp: Project = { id: 1, name: 'GM70 amp', notes: null, createdUtc: '2026-09-19T10:00:00Z' }

/**
 * The Notes box is a controlled textarea over `typeNotes`, and autosave fires 750 ms after a
 * keystroke (App.vue's AUTOSAVE_DEBOUNCE_MS). A save trims what it stores — whitespace-only notes
 * are stored as null — so re-reading the stored text back into the box unconditionally deleted the
 * space the user had just typed between two words, one second after pressing it. Reported
 * 2026-09-25. These pin both halves: the staged text survives its own save, and a save that really
 * did land different text is still adopted.
 */
describe('useProjects: the Notes box across a save', () => {
  let stored: MeasurementType[]

  beforeEach(async () => {
    vi.restoreAllMocks()
    stored = [
      { id: 11, projectId: 1, kind: 'FftSpectrum', name: 'Loopback', notes: null, paramsJson: '{}', sortOrder: 0, createdUtc: '' },
    ]
    vi.spyOn(api, 'getProjectSession').mockResolvedValue({ open: true, path: 'C:\\P\\amp.tbench', project: amp } satisfies ProjectSession)
    vi.spyOn(api, 'listMeasurementTypes').mockImplementation(async () => stored.map((t) => ({ ...t })))
    vi.spyOn(api, 'listRecentProjects').mockResolvedValue([])
    vi.spyOn(api, 'cancelRuns').mockResolvedValue(undefined as never)
    vi.spyOn(api, 'stopAcquisition').mockResolvedValue(undefined as never)
    vi.spyOn(api, 'setGenerator').mockResolvedValue(undefined as never)
    vi.spyOn(api, 'applyMeasurementType').mockResolvedValue({} as MeasurementTypeAppliedResponse)
    vi.spyOn(api, 'updateMeasurementType').mockImplementation(async (id, request) => {
      const type = stored.find((t) => t.id === id)
      if (type) type.notes = request.notes ?? null
    })

    await useProjects().refreshSession()
    await useProjects().loadTypes(1)
    await useProjects().selectType(11)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('leaves the trailing space alone, though it is not what was stored', async () => {
    const projects = useProjects()

    // Mid-sentence, the instant before the debounce elapses.
    projects.setTypeNotes('EL84 push')
    projects.setTypeNotes('EL84 push ')
    await projects.saveMeasurementType(11)

    expect(stored[0].notes).toBe('EL84 push')
    expect(projects.typeNotes.value).toBe('EL84 push ')
  })

  it('still adopts a stored text that is not simply what is on screen', async () => {
    const projects = useProjects()

    projects.setTypeNotes('mine')
    // Something other than this box decided what the type's notes are — a rename dialog, another
    // tab, a server that normalised them.
    vi.spyOn(api, 'updateMeasurementType').mockImplementation(async (id) => {
      const type = stored.find((t) => t.id === id)
      if (type) type.notes = 'theirs'
    })
    await projects.saveMeasurementType(11)

    expect(projects.typeNotes.value).toBe('theirs')
  })

  it('stores whitespace-only notes as nothing, and does not fight the box about it', async () => {
    const projects = useProjects()

    projects.setTypeNotes('   ')
    await projects.saveMeasurementType(11)

    expect(stored[0].notes).toBeNull()
    expect(projects.typeNotes.value).toBe('   ')
  })
})
