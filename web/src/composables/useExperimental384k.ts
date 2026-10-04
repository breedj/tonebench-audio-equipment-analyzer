import { ref } from 'vue'
import { api } from '../api/client'

/**
 * Configuration ▸ Acquisition ▸ "Allow 384 kHz sample rate (experimental)". On, the analyzer reports
 * 384 kHz among its capture rates and it appears in the FFT screen's Sample rate dropdown; off, that
 * rate does not exist as far as the rest of the app is concerned, and starting an acquisition at it
 * is refused with a 409.
 *
 * Off by default. 384 kHz is undocumented by QuantAsylum, and measured on a QA403 it is input-only:
 * the ADC genuinely re-clocks, the DAC emits garbage. The server enforces that half — a voiced
 * generator above 192 kHz is refused — so this flag only decides whether the rate is offered.
 *
 * Module-level singleton like useAutosave, and stored server-side rather than in this browser,
 * because it is a property of the bench and of the analyzer attached to it.
 */
const experimental384kEnabled = ref(false)
let loadPromise: Promise<void> | null = null

export function useExperimental384k() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        experimental384kEnabled.value = (await api.getExperimental384k()).enabled
      } catch {
        // Off is the server's own default too, so there is nothing to report yet.
      }
    })()
    return loadPromise
  }

  async function setExperimental384kEnabled(enabled: boolean) {
    experimental384kEnabled.value = enabled
    try {
      await api.setExperimental384k({ enabled })
    } catch {
      // Applied on screen already; a preference that failed to persist is found out next session,
      // which is the right trade for a preference and the wrong one for a measurement.
    }
  }

  return { experimental384kEnabled, load, setExperimental384kEnabled }
}
