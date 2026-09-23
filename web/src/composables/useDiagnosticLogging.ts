import { ref } from 'vue'
import { api } from '../api/client'

/**
 * Configuration ▸ General ▸ "Detailed logging". Off, the app writes its ordinary log; on, the
 * same file also carries every API request with its status and timing and the Debug-level lines
 * — what a tester is asked to switch on before reproducing a problem and sending the file in.
 * Stored server-side, because it is the server that logs and because the reproduction usually
 * involves a restart.
 *
 * Module-level singleton like useAutosave.
 */
const enabled = ref(false)
const logsDirectory = ref<string | null>(null)
const error = ref<string | null>(null)
let loadPromise: Promise<void> | null = null

export function useDiagnosticLogging() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        const status = await api.getDiagnosticLogging()
        enabled.value = status.enabled
        logsDirectory.value = status.logsDirectory
      } catch {
        // Off is the server's own default too; the folder line stays blank until a reload.
      }
    })()
    return loadPromise
  }

  async function setEnabled(value: boolean) {
    enabled.value = value
    error.value = null
    try {
      const status = await api.setDiagnosticLogging(value)
      enabled.value = status.enabled
      logsDirectory.value = status.logsDirectory
    } catch (e) {
      error.value = (e as Error).message
    }
  }

  async function openLogFolder() {
    error.value = null
    try {
      await api.openLogFolder()
    } catch (e) {
      error.value = (e as Error).message
    }
  }

  return { enabled, logsDirectory, error, load, setEnabled, openLogFolder }
}
