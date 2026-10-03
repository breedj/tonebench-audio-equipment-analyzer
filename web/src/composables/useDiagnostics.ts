import { ref } from 'vue'
import { api, type DiagnosticsStatus, type StreamRecordingStatus } from '../api/client'

/**
 * Configuration ▸ Diagnostics, the two switches beside "Detailed logging" (which has its own
 * composable, {@link useDiagnosticLogging}, because it pre-dates this panel and the status bar does
 * not need it).
 *
 * Both live server-side, and they are deliberately not alike:
 *
 * - **Record every acquisition** is persisted (`diagnostics.streamRecording`) and survives a
 *   restart, like detailed logging — a user asked to reproduce something usually restarts.
 * - **Capture suspect records** is *not* persisted, and that is the existing `DiagnosticRecorder`'s
 *   own design: it writes a CSV row per analysed record, so arming it is a deliberate act with a
 *   natural end. The panel says so rather than hiding it.
 *
 * Module-level singletons like `useAutosave`, so the dialog and anything else read one copy.
 */
const recording = ref<StreamRecordingStatus | null>(null)
const capture = ref<DiagnosticsStatus | null>(null)
const error = ref<string | null>(null)
let loadPromise: Promise<void> | null = null

export function useDiagnostics() {
  /** Both statuses, once. `refresh` is the one to call for the counters, which move while the dialog is open. */
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = refresh()
    return loadPromise
  }

  async function refresh() {
    // Settled, not all: a build whose diagnostics endpoint is missing should still show the
    // recording switch rather than leaving the whole section blank.
    const [r, c] = await Promise.allSettled([api.getStreamRecording(), api.getDiagnostics()])
    if (r.status === 'fulfilled') recording.value = r.value
    if (c.status === 'fulfilled') capture.value = c.value
  }

  async function setRecording(enabled: boolean, maxFileMegabytes?: number) {
    error.value = null
    const megabytes = maxFileMegabytes ?? recording.value?.maxFileMegabytes ?? 256
    try {
      recording.value = await api.setStreamRecording(enabled, megabytes)
    } catch (e) {
      error.value = (e as Error).message
      // Re-read, so a rejected cap does not leave the box showing a value the server refused.
      await refresh()
    }
  }

  async function setCapture(config: Partial<Pick<DiagnosticsStatus, 'enabled' | 'captureRawOnTrigger' | 'triggerThresholdDb'>>) {
    error.value = null
    try {
      capture.value = await api.setDiagnostics(config)
    } catch (e) {
      error.value = (e as Error).message
      await refresh()
    }
  }

  async function openRecordingsFolder() {
    error.value = null
    try {
      await api.openRecordingsFolder()
    } catch (e) {
      error.value = (e as Error).message
    }
  }

  async function openDiagnosticsFolder() {
    error.value = null
    try {
      await api.openDiagnosticsFolder()
    } catch (e) {
      error.value = (e as Error).message
    }
  }

  return {
    recording,
    capture,
    error,
    load,
    refresh,
    setRecording,
    setCapture,
    openRecordingsFolder,
    openDiagnosticsFolder,
  }
}
