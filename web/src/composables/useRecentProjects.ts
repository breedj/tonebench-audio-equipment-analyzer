import { ref } from 'vue'
import { api, type RecentProject } from '../api/client'
import { sortRecent } from '../projects/recent'

/**
 * The welcome screen's list: the ten project files opened most recently, as the server keeps them
 * in the configuration database (a project file copied to another bench must not carry this
 * machine's list). Module-level singleton like the other shared stores here. Reloaded after every
 * session change by useProjectSession, since every open, create and delete moves it.
 */
const recent = ref<RecentProject[]>([])
const error = ref<string | null>(null)

export function useRecentProjects() {
  async function load() {
    try {
      recent.value = sortRecent(await api.listRecentProjects())
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
    }
  }

  /** Forgets one entry — the user's call, since a missing file may just be on a drive that is unplugged. */
  async function remove(path: string) {
    try {
      await api.removeRecentProject(path)
      recent.value = recent.value.filter((e) => e.path !== path)
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
    }
  }

  return { recent, error, load, remove }
}
