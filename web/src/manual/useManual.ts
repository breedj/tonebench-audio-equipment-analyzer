import { ref } from 'vue'
import { manualPageById, manualPageForKind, type ManualEdition } from './pages'

// Module-level singleton, same pattern as useChartDisplay.ts/useAppTheme.ts: the header's manual
// button and every measurement screen's own help button drive one dialog, so opening from either
// place has to reach the same state.

const open = ref(false)
/** Which page is showing; null is the index. */
const pageId = ref<string | null>(null)

// Which of the two manuals the index and the sidebar list. It follows whatever page is opened, so
// this only really decides what the *index* shows — which is why it is remembered: a reader who has
// switched to the simple guide should not be handed the full manual's contents next time. Stored
// per browser like the theme, since it is a reading preference and nothing else depends on it.
const EDITION_STORAGE_KEY = 'qa403.manual.edition'

function storedEdition(): ManualEdition {
  return localStorage.getItem(EDITION_STORAGE_KEY) === 'guide' ? 'guide' : 'reference'
}

const edition = ref<ManualEdition>(storedEdition())

function setEdition(next: ManualEdition) {
  edition.value = next
  localStorage.setItem(EDITION_STORAGE_KEY, next)
}

export function useManual() {
  /** Opens the manual at a page, or at the index when no id is given. */
  function openManual(id: string | null = null) {
    showPage(id)
    open.value = true
  }

  /** Opens the help page for a measurement kind, falling back to the index for a kind with no page. */
  function openManualForKind(kind: string | null | undefined) {
    openManual(manualPageForKind(kind)?.id ?? null)
  }

  function closeManual() {
    open.value = false
  }

  /**
   * Shows a page. The sidebar and the index follow it into its own edition, so a cross-edition link
   * — the banner at the top of a page that has a counterpart — lands the reader in the right manual
   * rather than in one manual showing the other's contents.
   */
  function showPage(id: string | null) {
    pageId.value = id
    const page = manualPageById(id)
    if (page) setEdition(page.edition)
  }

  /** Switches manual. The page showing belongs to the other one, so this lands on the contents. */
  function showEdition(next: ManualEdition) {
    setEdition(next)
    pageId.value = null
  }

  return { open, pageId, edition, openManual, openManualForKind, closeManual, showPage, showEdition }
}
