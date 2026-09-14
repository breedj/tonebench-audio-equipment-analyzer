import { ref } from 'vue'
import { api, type UiLayoutConfig } from '../api/client'

/**
 * How the window is laid out: whether the measurement sidebar is pinned open or collapsed to a
 * rail that floats open on hover, and whether the settings rail and the display rail of the
 * measurement screens are collapsed to their edge strips.
 *
 * <p>Module-level singleton like the other shared stores here. Stored in the server's configuration
 * database (`ui.layout`) rather than `localStorage`, so every tab shows the same layout and a
 * browser cache clear does not silently reset it — the same reasoning as the impedance fixture,
 * and the same reason it is not in the measurement history: a project file copied to another
 * machine must not carry this screen's layout with it.</p>
 *
 * <p>Pinned and open until the load answers, because that is what the app always did and a first
 * paint at full width that then collapses is less surprising than the reverse.</p>
 */
const sidebarPinned = ref(true)
const settingsRailCollapsed = ref(false)
const displayRailCollapsed = ref(false)
let loadPromise: Promise<void> | null = null

function current(): UiLayoutConfig {
  return {
    sidebarPinned: sidebarPinned.value,
    settingsRailCollapsed: settingsRailCollapsed.value,
    displayRailCollapsed: displayRailCollapsed.value,
  }
}

export function useUiLayout() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        const layout = await api.getUiLayout()
        sidebarPinned.value = layout.sidebarPinned
        settingsRailCollapsed.value = layout.settingsRailCollapsed
        displayRailCollapsed.value = layout.displayRailCollapsed
      } catch {
        // A layout that could not be read is not worth an error on screen: the default is what
        // the app always did, and the next press writes a fresh document.
      }
    })()
    return loadPromise
  }

  // Applied before the request so the press is felt at once; a failed write leaves the screen
  // as pressed and only the next session finds out, which for a layout is the right trade. The
  // whole document is written each time, so the three flags never get out of step on the server.
  async function save() {
    try {
      await api.setUiLayout(current())
    } catch {
      // See above.
    }
  }

  function setPinned(pinned: boolean) {
    sidebarPinned.value = pinned
    return save()
  }

  function setSettingsRailCollapsed(collapsed: boolean) {
    settingsRailCollapsed.value = collapsed
    return save()
  }

  function setDisplayRailCollapsed(collapsed: boolean) {
    displayRailCollapsed.value = collapsed
    return save()
  }

  return {
    sidebarPinned,
    settingsRailCollapsed,
    displayRailCollapsed,
    load,
    setPinned,
    setSettingsRailCollapsed,
    setDisplayRailCollapsed,
  }
}
