import { ref } from 'vue'
import { api } from '../api/client'
import {
  DEFAULT_SCREENSHOT_SETTINGS,
  rememberScreenshotCaption,
  toScreenshotAspectRatioId,
  toScreenshotCaptions,
  toScreenshotDestinationId,
  toScreenshotWidthPx,
  type ScreenshotSettings,
} from '../screenshot'

/**
 * The Screenshot dialog's answers, remembered between screenshots and between sessions
 * (`screenshot.settings` in the server's configuration database, like the report settings): a
 * series of screenshots for one document wants the same size, ratio and destination every time,
 * and the captions are kept too when **Save text** is ticked — one per measurement, filed under
 * the name it was typed for, because a caption is only offered again on that one (see
 * `screenshotTitleFor`). Unticked, this measurement's caption is dropped from the list.
 *
 * Module-level singleton; loaded when the dialog first opens rather than at startup, since most
 * sessions never take a screenshot.
 */
const settings = ref<ScreenshotSettings>({ ...DEFAULT_SCREENSHOT_SETTINGS })
let loadPromise: Promise<void> | null = null

export function useScreenshotSettings() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        const stored = await api.getScreenshotSettings()
        settings.value = {
          // `title` and `titleFor` describe the screenshot in hand, so they start empty on every
          // load; what persists is the list the dialog looks this measurement up in.
          title: '',
          titleFor: '',
          captions: toScreenshotCaptions(stored.captions),
          saveText: stored.saveText === true,
          addDate: stored.addDate === true,
          widthPx: toScreenshotWidthPx(stored.widthPx),
          aspectRatio: toScreenshotAspectRatioId(stored.aspectRatio),
          destination: toScreenshotDestinationId(stored.destination),
          monochrome: stored.monochrome === true,
        }
      } catch {
        // The defaults are the server's own; a document that could not be read reads as that.
      }
    })()
    return loadPromise
  }

  /**
   * Remembers the dialog's answers. Applied locally first, so a failed write still shapes this
   * session's screenshots.
   *
   * <p>The caption just typed is filed under its own measurement, or removed from the list when
   * **Save text** is off — one list, computed once here, so the singleton and the database cannot
   * say different things. `title` and `titleFor` are deliberately not kept: they belong to the
   * screenshot that was just taken, and a stale one sitting here would take precedence over the
   * measurement's own name the next time the dialog opened.</p>
   */
  async function remember(next: ScreenshotSettings) {
    const captions = rememberScreenshotCaption(next.captions, next.titleFor, next.title, next.saveText)
    settings.value = { ...next, title: '', titleFor: '', captions }
    try {
      await api.setScreenshotSettings({
        captions,
        saveText: next.saveText,
        addDate: next.addDate,
        widthPx: next.widthPx,
        aspectRatio: next.aspectRatio,
        destination: next.destination,
        monochrome: next.monochrome,
      })
    } catch {
      // See above.
    }
  }

  return { settings, load, remember }
}
