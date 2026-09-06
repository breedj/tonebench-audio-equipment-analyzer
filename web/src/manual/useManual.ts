import { ref } from 'vue'
import { manualPageForKind } from './pages'

// Module-level singleton, same pattern as useChartDisplay.ts/useAppTheme.ts: the header's manual
// button and every measurement screen's own help button drive one dialog, so opening from either
// place has to reach the same state.

const open = ref(false)
/** Which page is showing; null is the index. */
const pageId = ref<string | null>(null)

export function useManual() {
  /** Opens the manual at a page, or at the index when no id is given. */
  function openManual(id: string | null = null) {
    pageId.value = id
    open.value = true
  }

  /** Opens the help page for a measurement kind, falling back to the index for a kind with no page. */
  function openManualForKind(kind: string | null | undefined) {
    openManual(manualPageForKind(kind)?.id ?? null)
  }

  function closeManual() {
    open.value = false
  }

  function showPage(id: string | null) {
    pageId.value = id
  }

  return { open, pageId, openManual, openManualForKind, closeManual, showPage }
}
