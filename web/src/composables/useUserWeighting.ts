import { ref } from 'vue'
import { api, type UserWeightingSummary } from '../api/client'

/**
 * The two user weighting slots as the server holds them: name and extent of the loaded table,
 * or null. Module-level singleton like the other shared stores. The tables themselves live in the
 * config database and in the server's live setting — the browser only ever picks a file for the
 * server to read (it cannot read a local path itself) and shows what got loaded.
 */
const user1 = ref<UserWeightingSummary | null>(null)
const user2 = ref<UserWeightingSummary | null>(null)
let loadPromise: Promise<void> | null = null

function apply(response: { user1: UserWeightingSummary | null; user2: UserWeightingSummary | null }) {
  user1.value = response.user1
  user2.value = response.user2
}

export function useUserWeighting() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        apply(await api.getUserWeighting())
      } catch {
        // Nothing loaded is what the panel shows anyway; the next import writes fresh state.
      }
    })()
    return loadPromise
  }

  /** Imports the file at `path` into a slot. Throws with the server's message on a bad file. */
  async function importFrom(slot: 1 | 2, path: string) {
    apply(await api.importUserWeighting(slot, path))
  }

  async function clear(slot: 1 | 2) {
    apply(await api.clearUserWeighting(slot))
  }

  function nameOf(slot: 1 | 2): string | null {
    return (slot === 1 ? user1.value : user2.value)?.name ?? null
  }

  return { user1, user2, load, importFrom, clear, nameOf }
}
