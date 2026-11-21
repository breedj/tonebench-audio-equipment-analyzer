import { ref } from 'vue'
import { api, type PanelOrderConfig } from '../api/client'
import type { PanelRailId } from '../panels/panelOrder'

/**
 * The order each measurement kind's two rails are stacked in, as the user dragged them.
 *
 * <p>Module-level singleton like <code>useUiLayout</code>, and stored beside it in the server's
 * configuration database (<code>ui.panelOrder</code>) for the same reasons: every tab shows the
 * same rail, a browser cache clear does not silently reset it, and a project file copied to
 * another machine does not carry one window's layout with it.</p>
 *
 * <p><b>Per kind, not per measurement</b> — which is what was asked for (2026-11-21): open another
 * FFT measurement and its rails are stacked the way the last one was left. That is also why it is
 * not in <code>params_json</code>: a panel order is a habit of the person at the bench, not a
 * property of what was measured.</p>
 *
 * <p>Empty until the load answers, which reads as "the order the screen gives them" — so a rail
 * paints in its natural order and then settles into the stored one, rather than the reverse.</p>
 */
const kinds = ref<PanelOrderConfig['kinds']>({})
let loadPromise: Promise<void> | null = null

export function usePanelOrder() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        const stored = await api.getPanelOrder()
        kinds.value = stored.kinds ?? {}
      } catch {
        // An order that could not be read is not worth an error on screen: the default is the
        // order the screen gives them, and the next drag writes a fresh document.
      }
    })()
    return loadPromise
  }

  /** What is stored for one rail of one kind, or an empty list where nothing is. */
  function orderFor(kind: string | null | undefined, rail: PanelRailId): string[] {
    if (!kind) return []
    return kinds.value[kind]?.[rail] ?? []
  }

  /**
   * Remembers a rail's new order.
   *
   * <p>Applied locally before the request so the drag is felt at once; a failed write leaves the
   * screen as dragged and only the next session finds out, which for a layout is the right trade —
   * <code>useUiLayout</code>'s own rule. The whole document is written each time, so two rails of
   * one kind can never get out of step on the server.</p>
   */
  async function setOrder(kind: string | null | undefined, rail: PanelRailId, ids: readonly string[]) {
    if (!kind) return
    // Replaced rather than mutated, so a watcher on the map sees it move.
    kinds.value = { ...kinds.value, [kind]: { ...kinds.value[kind], [rail]: ids.slice() } }
    try {
      await api.setPanelOrder({ kinds: kinds.value })
    } catch {
      // See above.
    }
  }

  /** Puts one kind's rails back to the order the screen gives them. */
  async function reset(kind: string | null | undefined) {
    if (!kind || !kinds.value[kind]) return
    const next = { ...kinds.value }
    delete next[kind]
    kinds.value = next
    try {
      await api.setPanelOrder({ kinds: kinds.value })
    } catch {
      // See above.
    }
  }

  return { kinds, load, orderFor, setOrder, reset }
}
