import { ref } from 'vue'
import {
  api,
  type MeasurementType,
  type MeasurementTypeAppliedResponse,
  type Project,
  type SaveMeasurementTypeRequest,
  type SaveProjectRequest,
  type StagedMeasurementConfig,
} from '../api/client'
import { stopRun } from '../deviceControl'

// Module-level, not inside the composable function, so every caller (the project panel, the save
// dialog's pickers, the history filter) shares one fetched list instead of drifting out of sync
// with independent copies — same shape as useSpectrumConfig.ts's singleton refs.
const projects = ref<Project[]>([])
const types = ref<MeasurementType[]>([])
const typesProjectId = ref<number | null>(null)
const error = ref<string | null>(null)
let loadProjectsPromise: Promise<void> | null = null

// The project/type the sidebar has open — shared so the main pane (chart, history, save dialog
// defaults) can all scope themselves to "whatever's currently selected" without their own copy of
// the same two ids drifting apart.
const currentProjectId = ref<number | null>(null)
const currentTypeId = ref<number | null>(null)
// The currently-selected type's generator/acquisition settings, as returned by the last apply —
// GeneratorPanel/SpectrumConfigPanel watch this to stage their forms. Null means nothing loaded yet.
const loadedParams = ref<MeasurementTypeAppliedResponse | null>(null)

// A type's "current configuration" only ever exists in one place at a time: whatever's live in the
// device/pipeline right now (see MeasurementSaveService.BuildCurrentParametersJson). Selecting a
// different type overwrites that live state, so in practice at most the currently-open type can
// ever be dirty — a Set rather than a single flag is just future-proofing, not a sign multiple
// types can be dirty simultaneously today.
const dirtyTypeIds = ref<Set<number>>(new Set())

export function useProjects() {
  async function loadProjects() {
    if (loadProjectsPromise) return loadProjectsPromise

    loadProjectsPromise = (async () => {
      try {
        projects.value = await api.listProjects()
        error.value = null
        // Nothing in the UI can pick a project yet on a cold load (the sidebar has nothing to
        // list until one is selected), so default to the first one rather than leaving the shell empty.
        if (currentProjectId.value === null && projects.value.length > 0) await selectProject(projects.value[0].id)
      } catch (e) {
        error.value = (e as Error).message
      } finally {
        loadProjectsPromise = null
      }
    })()

    return loadProjectsPromise
  }

  /**
   * True if the current type's live edits would be discarded by navigating away right now — asks
   * to confirm if so, returns false (caller should abort the switch) on Cancel. A no-op returning
   * true when there's nothing dirty to lose.
   */
  function confirmDiscardCurrentIfDirty(): boolean {
    if (currentTypeId.value === null || !dirtyTypeIds.value.has(currentTypeId.value)) return true
    const name = types.value.find((t) => t.id === currentTypeId.value)?.name ?? 'this measurement'
    if (!window.confirm(`Discard unsaved changes to "${name}"?`)) return false
    markClean(currentTypeId.value)
    return true
  }

  async function selectProject(id: number) {
    if (!confirmDiscardCurrentIfDirty()) return
    await stopRun()
    currentProjectId.value = id
    currentTypeId.value = null
    loadedParams.value = null
    await loadTypes(id)
  }

  /**
   * Selects a measurement type in the sidebar. Stops whatever's currently running first (a
   * different measurement's generator/acquisition has no business still going once you've moved
   * on), then loads this one's parameters — silent ones (spectrum, ranges) applied immediately,
   * generator/acquisition staged into loadedParams for the Run button. See SpectrumConfigPanel.vue and App.vue's runFromCurrentSettings.
   */
  async function selectType(id: number) {
    if (id !== currentTypeId.value && !confirmDiscardCurrentIfDirty()) return
    await stopRun()
    currentTypeId.value = id
    loadedParams.value = await applyMeasurementType(id)
    markClean(id)
  }

  /** Flags the currently-open type as having live edits not yet reflected in its stored params_json. */
  function markDirty() {
    if (currentTypeId.value === null || dirtyTypeIds.value.has(currentTypeId.value)) return
    dirtyTypeIds.value = new Set(dirtyTypeIds.value).add(currentTypeId.value)
  }

  function markClean(id: number) {
    if (!dirtyTypeIds.value.has(id)) return
    const next = new Set(dirtyTypeIds.value)
    next.delete(id)
    dirtyTypeIds.value = next
  }

  async function loadTypes(projectId: number) {
    try {
      types.value = await api.listMeasurementTypes(projectId)
      typesProjectId.value = projectId
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  async function createProject(req: SaveProjectRequest) {
    try {
      const { id } = await api.createProject(req)
      await loadProjects()
      error.value = null
      return id
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  async function updateProject(id: number, req: SaveProjectRequest) {
    try {
      await api.updateProject(id, req)
      await loadProjects()
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  async function deleteProject(id: number) {
    try {
      await api.deleteProject(id)
      await loadProjects()
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  async function createMeasurementType(projectId: number, req: SaveMeasurementTypeRequest) {
    try {
      const { id } = await api.createMeasurementType(projectId, req)
      await loadTypes(projectId)
      error.value = null
      return id
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  async function updateMeasurementType(id: number, projectId: number, req: SaveMeasurementTypeRequest) {
    try {
      await api.updateMeasurementType(id, req)
      await loadTypes(projectId)
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  async function deleteMeasurementType(id: number, projectId: number) {
    try {
      await api.deleteMeasurementType(id)
      if (currentTypeId.value === id) {
        currentTypeId.value = null
        loadedParams.value = null
      }
      markClean(id)
      await loadTypes(projectId)
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  /** Pushes a type's stored spectrum/range parameters into the live app and returns its generator/acquisition settings — see api.applyMeasurementType. */
  async function applyMeasurementType(id: number): Promise<MeasurementTypeAppliedResponse> {
    try {
      const response = await api.applyMeasurementType(id)
      error.value = null
      return response
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  /**
   * Persists the type's current live configuration (generator, ranges, spectrum settings) into its
   * own params_json in place — no dialog, since the name/kind/notes it keeps are already whatever
   * they were. Omitting paramsJson from the request is what tells the server to snapshot the live
   * state; see ProjectsEndpoints.cs's PUT handler.
   */
  async function saveMeasurementType(id: number, staged?: StagedMeasurementConfig) {
    const type = types.value.find((t) => t.id === id)
    if (!type) throw new Error(`Measurement type ${id} not found.`)
    try {
      await api.updateMeasurementType(id, { kind: type.kind, name: type.name, notes: type.notes, staged })
      await loadTypes(type.projectId)
      markClean(id)
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  /**
   * Clones the type's current live configuration into a brand-new measurement type under the same
   * project, then reverts the original back to its own last-saved params — the in-progress edits
   * end up living only on the new copy. Omitting paramsJson on create (like saveMeasurementType)
   * is what seeds the new type from the live state rather than an empty default.
   */
  async function saveMeasurementTypeAs(id: number, newName: string, staged?: StagedMeasurementConfig): Promise<number> {
    const type = types.value.find((t) => t.id === id)
    if (!type) throw new Error(`Measurement type ${id} not found.`)
    try {
      const { id: newId } = await api.createMeasurementType(type.projectId, { kind: type.kind, name: newName, notes: type.notes, staged })
      loadedParams.value = await applyMeasurementType(id)
      markClean(id)
      await loadTypes(type.projectId)
      error.value = null
      return newId
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  /**
   * Saves every dirty measurement type in the given project at once — see saveMeasurementType.
   * `staged` (the live Acquisition/Generator form state) only applies to whichever of those is the
   * currently-open type, since that's the only one it could possibly describe.
   */
  async function saveProject(projectId: number, staged?: StagedMeasurementConfig) {
    const dirty = types.value.filter((t) => t.projectId === projectId && dirtyTypeIds.value.has(t.id))
    for (const t of dirty) await saveMeasurementType(t.id, t.id === currentTypeId.value ? staged : undefined)
  }

  /**
   * Clones the current project and all of its measurement types — using each type's own
   * already-stored params_json, not whatever happens to be live right now — into a new project,
   * leaving the original untouched.
   */
  async function saveProjectAs(newName: string): Promise<number> {
    if (currentProjectId.value === null) throw new Error('No project selected.')
    const sourceProjectId = currentProjectId.value
    const sourceProject = projects.value.find((p) => p.id === sourceProjectId)
    try {
      const newProjectId = await createProject({ name: newName, notes: sourceProject?.notes ?? null })
      for (const t of types.value.filter((t) => t.projectId === sourceProjectId))
        await api.createMeasurementType(newProjectId, { kind: t.kind, name: t.name, notes: t.notes, paramsJson: t.paramsJson })
      error.value = null
      return newProjectId
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  if (projects.value.length === 0) loadProjects()

  return {
    projects,
    types,
    typesProjectId,
    currentProjectId,
    currentTypeId,
    loadedParams,
    dirtyTypeIds,
    error,
    loadProjects,
    loadTypes,
    selectProject,
    selectType,
    createProject,
    updateProject,
    deleteProject,
    createMeasurementType,
    updateMeasurementType,
    deleteMeasurementType,
    applyMeasurementType,
    markDirty,
    saveMeasurementType,
    saveMeasurementTypeAs,
    saveProject,
    saveProjectAs,
  }
}
