import { ref } from 'vue'
import { api } from '../api/client'

/**
 * Configuration ▸ General ▸ "Autosave measurement settings". On, every change to the open
 * measurement's settings is written into it straight away — App.vue watches the dirty flag and
 * calls the same save the Save button does — so the button never has to be pressed and the red
 * dot never stays lit. Off (the default), saving is explicit, as it always was.
 *
 * Module-level singleton like useStartupConfig, and stored server-side for the same reason a
 * bench preference is: it belongs to this installation, not to one browser tab.
 */
const autosaveEnabled = ref(false)
let loadPromise: Promise<void> | null = null

export function useAutosave() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        autosaveEnabled.value = (await api.getAutosaveConfig()).enabled
      } catch {
        // Off is what the app always did; nothing to show an error on yet.
      }
    })()
    return loadPromise
  }

  async function setAutosaveEnabled(enabled: boolean) {
    autosaveEnabled.value = enabled
    try {
      await api.setAutosaveConfig({ enabled })
    } catch {
      // Applied on screen already; a failed write is found out next session, which for a
      // preference is the right trade.
    }
  }

  return { autosaveEnabled, load, setAutosaveEnabled }
}
