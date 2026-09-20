import { ref } from 'vue'
import { api } from '../api/client'
import {
  DEFAULT_SCREENSHOT_SETTINGS,
  toScreenshotAspectRatioId,
  toScreenshotDestinationId,
  toScreenshotWidthPx,
  type ScreenshotSettings,
} from '../screenshot'

/**
 * The Screenshot dialog's answers, remembered between screenshots and between sessions
 * (`screenshot.settings` in the server's configuration database, like the report settings): a
 * series of screenshots for one document wants the same size, ratio and destination every time,
 * and the title is kept too when **Save text** is ticked — so the same caption need not be retyped
 * for every chart of a session. Unticked, the title is stored as null and the dialog reopens blank.
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
          title: stored.saveText && typeof stored.title === 'string' ? stored.title : '',
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

  /** Remembers the dialog's answers. Applied locally first, so a failed write still shapes this session's screenshots. */
  async function remember(next: ScreenshotSettings) {
    settings.value = { ...next }
    try {
      await api.setScreenshotSettings({
        title: next.saveText ? next.title : null,
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
