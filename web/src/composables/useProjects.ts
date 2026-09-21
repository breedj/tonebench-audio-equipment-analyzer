import { computed, ref } from 'vue'
import {
  api,
  type MeasurementType,
  type MeasurementTypeAppliedResponse,
  type Project,
  type SaveMeasurementTypeRequest,
  type StagedMeasurementConfig,
} from '../api/client'
import { stopRun } from '../deviceControl'
import { useConfirm } from './useConfirm'

// Module-level, not inside the composable function, so every caller (the menu, the sidebar, the
// save dialog's pickers, the history filter) shares one state instead of drifting out of sync
// with independent copies — same shape as useSpectrumConfig.ts's singleton refs.
//
// The open project is the server's: one `.qa403` file at a time, opened and switched through
// useProjectSession, and read back here with `refreshSession` (GET /api/project). Null means the
// welcome screen. There is no client-side list of projects any more — the recent files list is
// useRecentProjects, and it is a list of files, not of rows.
const currentProject = ref<Project | null>(null)
const types = ref<MeasurementType[]>([])
const typesProjectId = ref<number | null>(null)
const error = ref<string | null>(null)
/** False until the first GET /api/project has answered, so the shell does not flash the welcome screen on a reload of an open project. */
const sessionLoaded = ref(false)
let refreshPromise: Promise<void> | null = null

// The project/type the sidebar has open — shared so the main pane (chart, history, save dialog
// defaults) can all scope themselves to "whatever's currently selected" without their own copy of
// the same two ids drifting apart.
const currentProjectId = computed(() => currentProject.value?.id ?? null)
const currentTypeId = ref<number | null>(null)
// The currently-selected type's generator/acquisition settings, as returned by the last apply —
// GeneratorPanel/SpectrumConfigPanel watch this to stage their forms. Null means nothing loaded yet.
const loadedParams = ref<MeasurementTypeAppliedResponse | null>(null)
// The open type's notes as they read on screen — the Notes box under every measurement's settings
// (MeasurementNotesPanel). Staged like the settings: an edit marks the type dirty and Save writes
// it, so a type's stored `notes` and this ref differ exactly while the red dot shows. Also what the
// Save measurement dialog copies into a new result's notes.
const typeNotes = ref('')

// A type's "current configuration" only ever exists in one place at a time: whatever's live in the
// device/pipeline right now (see MeasurementSaveService.BuildCurrentParametersJson). Selecting a
// different type overwrites that live state, so in practice at most the currently-open type can
// ever be dirty — a Set rather than a single flag is just future-proofing, not a sign multiple
// types can be dirty simultaneously today.
const dirtyTypeIds = ref<Set<number>>(new Set())
/**
 * The type whose stored settings were last pushed into the live state successfully. A save writes
 * the *live* state into a type, so it may only ever write the type that state came from: after a
 * failed apply the screen still holds the previous type's settings, and saving -- or autosaving,
 * which any watcher can trigger -- would write those over the failed type's own. That is exactly
 * what turned one 500 on selection into a permanently broken FFT type on 2026-09-21.
 */
const appliedTypeId = ref<number | null>(null)
// The file path of the last session answer, so a Save As (same project id in a new file) still
// counts as a change of project.
let lastSessionPath: string | null = null

/** Whether any measurement in the open project has unsaved settings — what enables Project ▸ Save. */
const hasDirty = computed(() => types.value.some((t) => dirtyTypeIds.value.has(t.id)))

/**
 * Saves the open type's live settings — App.vue's saveCurrentType, registered through
 * `setAutosaveFlush`, since building the staged config needs the panels App.vue owns. Called by
 * confirmDiscardCurrentIfDirty before it asks: with autosave on, an edit made a moment before a
 * switch is still inside the autosave debounce, and the right answer is to write it, not to ask
 * whether to throw it away. A no-op (resolving without saving) when autosave is off.
 */
let autosaveFlush: (() => Promise<void>) | null = null

export function useProjects() {
  /**
   * Reads which project the server has open and rebuilds the client state around it: types
   * reloaded, selection and staged settings cleared. Called once on first use and by
   * useProjectSession after every open/create/close/save-as, and it is the only path that changes
   * `currentProject` — the server owns that.
   */
  async function refreshSession() {
    if (refreshPromise) return refreshPromise

    refreshPromise = (async () => {
      try {
        const session = await api.getProjectSession()
        const next = session.open ? session.project : null
        const changed = next?.id !== currentProject.value?.id || session.path !== lastSessionPath
        lastSessionPath = session.path
        currentProject.value = next
        error.value = null
        if (changed) {
          currentTypeId.value = null
          loadedParams.value = null
          appliedTypeId.value = null
          typeNotes.value = ''
          dirtyTypeIds.value = new Set()
          types.value = []
          typesProjectId.value = null
          if (next) await loadTypes(next.id)
        }
      } catch (e) {
        error.value = (e as Error).message
      } finally {
        sessionLoaded.value = true
        refreshPromise = null
      }
    })()

    return refreshPromise
  }

  /**
   * True if the current type's live edits would be discarded by navigating away right now — asks
   * to confirm if so, resolves false (caller should abort the switch) on Cancel. A no-op resolving
   * true when there's nothing dirty to lose.
   */
  async function confirmDiscardCurrentIfDirty(): Promise<boolean> {
    if (currentTypeId.value === null || !dirtyTypeIds.value.has(currentTypeId.value)) return true
    // Autosave first; if it wrote the type, there is nothing to discard. A failed save leaves the
    // type dirty and falls through to the question, so an edit is never dropped silently.
    if (autosaveFlush) {
      await autosaveFlush().catch(() => undefined)
      if (!dirtyTypeIds.value.has(currentTypeId.value)) return true
    }
    const name = types.value.find((t) => t.id === currentTypeId.value)?.name ?? 'this measurement'
    const ok = await useConfirm().confirm({
      title: 'Unsaved changes',
      message: `Discard unsaved changes to "${name}"?`,
      confirmLabel: 'Discard',
    })
    if (!ok) return false
    markClean(currentTypeId.value)
    return true
  }

  /**
   * Selects a measurement type in the sidebar. Stops whatever's currently running first (a
   * different measurement's generator/acquisition has no business still going once you've moved
   * on), then loads this one's parameters — silent ones (spectrum, ranges) applied immediately,
   * generator/acquisition staged into loadedParams for the Run button. See SpectrumConfigPanel.vue and App.vue's runFromCurrentSettings.
   */
  async function selectType(id: number) {
    if (id !== currentTypeId.value && !(await confirmDiscardCurrentIfDirty())) return
    await stopRun()
    currentTypeId.value = id
    typeNotes.value = types.value.find((t) => t.id === id)?.notes ?? ''
    appliedTypeId.value = null
    loadedParams.value = await applyMeasurementType(id)
    appliedTypeId.value = id
    markClean(id)
  }

  /** The Notes box: a user edit, so it dirties the open type like any staged setting. */
  function setTypeNotes(notes: string) {
    if (notes === typeNotes.value) return
    typeNotes.value = notes
    markDirty()
  }

  /** The notes to store for a type: the staged text for the open one, its stored notes for any other. */
  function notesToSave(type: MeasurementType): string | null {
    if (type.id !== currentTypeId.value) return type.notes
    const trimmed = typeNotes.value.trim()
    return trimmed.length > 0 ? trimmed : null
  }

  /** Registers the autosave flush — see `autosaveFlush`. App.vue calls this once. */
  function setAutosaveFlush(flush: (() => Promise<void>) | null) {
    autosaveFlush = flush
  }

  /** Flags the currently-open type as having live edits not yet reflected in its stored params_json. */
  function markDirty() {
    // Nothing on screen belongs to a type whose settings never loaded, so nothing can dirty it.
    if (currentTypeId.value === null || appliedTypeId.value !== currentTypeId.value) return
    if (dirtyTypeIds.value.has(currentTypeId.value)) return
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
        appliedTypeId.value = null
      }
      markClean(id)
      await loadTypes(projectId)
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  /**
   * Rewrites the sidebar order from the given full list of type ids. Applied to `types` first and
   * sent afterwards, because a drop that only takes effect a round-trip later reads as a dropped
   * drag; a failed request reloads the server's order back over the optimistic one. The list must
   * be every type in the project exactly once — the server rejects anything else (409) rather
   * than half-applying it. See ProjectRepository.ReorderTypesAsync.
   */
  async function reorderTypes(projectId: number, orderedIds: number[]) {
    const byId = new Map(types.value.map((t) => [t.id, t]))
    const reordered = orderedIds.map((id) => byId.get(id)).filter((t): t is MeasurementType => t !== undefined)
    if (reordered.length !== orderedIds.length) return

    const previous = types.value
    types.value = reordered
    try {
      await api.reorderMeasurementTypes(projectId, orderedIds)
      error.value = null
    } catch (e) {
      types.value = previous
      error.value = (e as Error).message
      await loadTypes(projectId).catch(() => {})
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
  async function saveMeasurementType(id: number, staged?: StagedMeasurementConfig, paramsJson?: string) {
    const type = types.value.find((t) => t.id === id)
    if (!type) throw new Error(`Measurement type ${id} not found.`)
    if (id === currentTypeId.value && appliedTypeId.value !== id) {
      const message = `The settings of "${type.name}" could not be loaded, so they are not saved — select it again once the problem is fixed.`
      error.value = message
      throw new Error(message)
    }
    try {
      await api.updateMeasurementType(id, { kind: type.kind, name: type.name, notes: notesToSave(type), staged, paramsJson })
      await loadTypes(type.projectId)
      if (id === currentTypeId.value) typeNotes.value = types.value.find((t) => t.id === id)?.notes ?? ''
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
  async function saveMeasurementTypeAs(
    id: number,
    newName: string,
    staged?: StagedMeasurementConfig,
    paramsJson?: string,
  ): Promise<number> {
    const type = types.value.find((t) => t.id === id)
    if (!type) throw new Error(`Measurement type ${id} not found.`)
    try {
      const { id: newId } = await api.createMeasurementType(type.projectId, {
        kind: type.kind, name: newName, notes: notesToSave(type), staged, paramsJson,
      })
      loadedParams.value = await applyMeasurementType(id)
      appliedTypeId.value = id
      // The original reverts to its own stored notes along with its stored settings.
      typeNotes.value = type.notes ?? ''
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
   * currently-open type, since that's the only one it could possibly describe. `paramsJson` is the
   * same story for a sweep type — SweepConfigPanel is only mounted for the open one, so any other
   * dirty sweep type falls through to the server's "keep what's stored" path.
   */
  async function saveProject(projectId: number, staged?: StagedMeasurementConfig, paramsJson?: string) {
    const dirty = types.value.filter((t) => t.projectId === projectId && dirtyTypeIds.value.has(t.id))
    for (const t of dirty) {
      const isOpen = t.id === currentTypeId.value
      await saveMeasurementType(t.id, isOpen ? staged : undefined, isOpen ? paramsJson : undefined)
    }
  }

  if (!sessionLoaded.value) void refreshSession()

  return {
    currentProject,
    sessionLoaded,
    types,
    typesProjectId,
    currentProjectId,
    currentTypeId,
    loadedParams,
    appliedTypeId,
    typeNotes,
    setTypeNotes,
    dirtyTypeIds,
    hasDirty,
    error,
    refreshSession,
    confirmDiscardCurrentIfDirty,
    loadTypes,
    selectType,
    createMeasurementType,
    updateMeasurementType,
    deleteMeasurementType,
    reorderTypes,
    applyMeasurementType,
    markDirty,
    setAutosaveFlush,
    saveMeasurementType,
    saveMeasurementTypeAs,
    saveProject,
  }
}
