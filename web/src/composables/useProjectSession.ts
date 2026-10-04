import { ref, watch } from 'vue'
import { api, type CreateProjectRequest, type ProjectPropertiesRequest, type SaveProjectAsRequest } from '../api/client'
import { stopRun } from '../deviceControl'
import { useConfirm } from './useConfirm'
import { useProjects } from './useProjects'
import { useRecentProjects } from './useRecentProjects'

const APP_TITLE = 'Tonebench'

/**
 * The Project menu's verbs. Each one asks about unsaved changes where those would be lost, stops
 * whatever is running (the server refuses a switch mid-run anyway — this turns that refusal into
 * the ordinary case), calls the server, then reloads the session and the recent list so every
 * consumer of useProjects sees the new project at once. Errors are left for the caller's
 * withErrorReporting, the same as every other action in App.vue.
 *
 * Module-level singleton: the header's menu and the welcome screen drive one session.
 */
const busy = ref(false)
let titleWatched = false

export function useProjectSession() {
  const projects = useProjects()
  const recent = useRecentProjects()

  // The window title carries the project name. Guarded for the node environment vitest runs in.
  if (!titleWatched && typeof document !== 'undefined') {
    titleWatched = true
    watch(
      projects.currentProject,
      (project) => {
        document.title = project ? `${project.name} – ${APP_TITLE}` : APP_TITLE
      },
      { immediate: true },
    )
  }

  async function run<T>(action: () => Promise<T>): Promise<T> {
    busy.value = true
    try {
      return await action()
    } finally {
      busy.value = false
    }
  }

  async function afterChange() {
    await projects.refreshSession()
    await recent.load()
  }

  /** Leaves the open project, if any, in a state the server will let go of: nothing dirty, nothing running. */
  async function prepareToLeave(): Promise<boolean> {
    if (!(await projects.confirmDiscardCurrentIfDirty())) return false
    if (projects.currentProject.value) await stopRun()
    return true
  }

  async function open(path: string): Promise<boolean> {
    if (!(await prepareToLeave())) return false
    await run(() => api.openProject(path))
    await afterChange()
    return true
  }

  async function create(req: CreateProjectRequest): Promise<boolean> {
    if (!(await prepareToLeave())) return false
    await run(() => api.createProject(req))
    await afterChange()
    return true
  }

  async function close(): Promise<boolean> {
    if (!(await prepareToLeave())) return false
    await run(() => api.closeProject())
    await afterChange()
    return true
  }

  /** Confirms, then closes and deletes the open project's file. Nothing here goes to a recycle bin. */
  async function remove(): Promise<boolean> {
    const project = projects.currentProject.value
    if (!project) return false
    const path = (await api.getProjectSession()).path
    if (!path) return false

    const ok = await useConfirm().confirm({
      title: 'Delete project',
      message: `Delete "${project.name}" and its file ${path}? Every measurement stored in it is deleted with it. This cannot be undone.`,
      confirmLabel: 'Delete',
      danger: true,
    })
    if (!ok) return false

    // No unsaved-changes prompt: the whole file is going, dirty or not.
    await stopRun()
    await run(() => api.deleteProject(path))
    await afterChange()
    return true
  }

  /**
   * Save As copies what is stored, so the dirty types are saved first — `saveDirty` is App.vue's
   * saveCurrentProject, which knows the staged form state this composable does not.
   */
  async function saveAs(req: SaveProjectAsRequest, saveDirty: () => Promise<void>): Promise<boolean> {
    if (!projects.currentProject.value) return false
    await saveDirty()
    await stopRun()
    await run(() => api.saveProjectAs(req))
    await afterChange()
    return true
  }

  async function updateProperties(req: ProjectPropertiesRequest): Promise<boolean> {
    if (!projects.currentProject.value) return false
    await run(() => api.updateProjectProperties(req))
    await afterChange()
    return true
  }

  return {
    busy,
    open,
    create,
    close,
    remove,
    saveAs,
    updateProperties,
  }
}
