import { nextTick, useTemplateRef, watch, type Ref } from 'vue'

/**
 * Brings a dialog's error message to the eye when one appears.
 *
 * <p>Written for the simulation import, where it is the difference between a refusal and nothing
 * happening at all. That dialog lists one checkbox per run, so a 61-step file makes it some 1300 px
 * tall: the Import button is at the bottom, where the user is looking, and the message is at the
 * top. Measured against the reported case — press Import, wait forty seconds, and the explanation
 * renders <b>525 px above the top of the window</b> while the only visible change is the spinner
 * stopping. Reported 2026-10-19 as "the dialog does not close and nothing has been imported", which
 * is exactly what it looks like from down there.</p>
 *
 * <p>Scrolling rather than a second copy of the message beside the button: one failure should read
 * as one message, and the movement is itself the signal that the press was answered.</p>
 */
export interface ScrollableElement {
  scrollIntoView?: (options?: ScrollIntoViewOptions) => void
}

export function useErrorScroll(error: Ref<string | null>, name = 'errorBox') {
  // The element is found by the name the template binds (`ref="errorBox"`), so the dialog declares
  // nothing of its own -- a returned ref would be a variable its script never reads.
  const errorBox = useTemplateRef<ScrollableElement>(name)

  watch(error, async (now, before) => {
    // Only on a *new* message. Re-scrolling for the same text would fight a user who has scrolled
    // away to read the settings the message is about, and clearing one must not move anything.
    if (!now || now === before) return
    await nextTick()
    errorBox.value?.scrollIntoView?.({ block: 'center', behavior: 'smooth' })
  })
}
