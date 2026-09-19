import { ref } from 'vue'
import { api } from '../api/client'

/**
 * "Show welcome page on startup": checked, the app starts on the welcome screen; unchecked, the
 * server reopens the project that was open last (if its file still exists — see
 * ProjectStartupHostedService). Read by both the welcome screen and the Configuration dialog, so
 * it is a module-level singleton, and stored server-side because the server is what acts on it.
 * Same load/save shape as useUiLayout.
 */
const showWelcomeOnStartup = ref(true)
let loadPromise: Promise<void> | null = null

export function useStartupConfig() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        showWelcomeOnStartup.value = (await api.getStartupConfig()).showWelcomeOnStartup
      } catch {
        // The default is what the app always did; nothing to show an error on yet.
      }
    })()
    return loadPromise
  }

  async function setShowWelcomeOnStartup(show: boolean) {
    showWelcomeOnStartup.value = show
    try {
      await api.setStartupConfig({ showWelcomeOnStartup: show })
    } catch {
      // Applied on screen already; a failed write is found out next session, which for a
      // preference is the right trade.
    }
  }

  return { showWelcomeOnStartup, load, setShowWelcomeOnStartup }
}
