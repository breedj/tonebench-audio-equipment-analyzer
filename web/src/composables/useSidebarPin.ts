import { ref } from 'vue'
import { api } from '../api/client'

/**
 * Whether the measurement sidebar is pinned open, or collapsed to a rail that floats open on hover.
 *
 * <p>Module-level singleton like the other shared stores here. Stored in the server's configuration
 * database (`ui.layout`) rather than `localStorage`, so every tab shows the same layout and a
 * browser cache clear does not silently reset it — the same reasoning as the impedance fixture,
 * and the same reason it is not in the measurement history: a project file copied to another
 * machine must not carry this screen's layout with it.</p>
 *
 * <p>Pinned until the load answers, because pinned is what the app always did and a first paint at
 * full width that then collapses is less surprising than the reverse.</p>
 */
const sidebarPinned = ref(true)
let loadPromise: Promise<void> | null = null

export function useSidebarPin() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        sidebarPinned.value = (await api.getUiLayout()).sidebarPinned
      } catch {
        // A layout that could not be read is not worth an error on screen: the default is what
        // the app always did, and the next pin press writes a fresh document.
      }
    })()
    return loadPromise
  }

  async function setPinned(pinned: boolean) {
    // Applied before the request so the press is felt at once; a failed write leaves the screen
    // as pressed and only the next session finds out, which for a layout is the right trade.
    sidebarPinned.value = pinned
    try {
      await api.setUiLayout({ sidebarPinned: pinned })
    } catch {
      // See above.
    }
  }

  return { sidebarPinned, load, setPinned }
}
