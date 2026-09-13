import { ref } from 'vue'
import { api } from '../api/client'

/**
 * The capture delay: how long the DUT is given to settle after every stimulus change before the
 * analysis window starts filling.
 *
 * <p>Module-level singleton like the other shared stores here. Stored in the server's configuration
 * database (`acquisition.captureDelay`) and applied *server-side*, at the one place every generator
 * change passes through (DeviceSessionService.ConfigureGeneratorAsync) — so it covers the live FFT
 * screen's Run and every point of every sweep alike. It used to be a `setTimeout` in App.vue's Run
 * handler, which no sweep point ever saw: measured against the simulator's tube-amp model, the
 * 12 Hz high-pass ringing on each tone step sat at −77 dBV across the bins below the tone in every
 * point of a THD sweep and read as DC.</p>
 *
 * <p>The value is still needed here for the panels' time estimates, which is what this exposes it
 * for — nothing in the browser waits on it any more.</p>
 */
const captureDelayMs = ref(0)
let loadPromise: Promise<void> | null = null

/** The `localStorage` key the setting lived under before it moved to the server; read once to carry a stored value across, then removed. */
const LEGACY_STORAGE_KEY = 'qa403.captureDelayMs'

export function useCaptureDelay() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        captureDelayMs.value = (await api.getCaptureDelay()).captureDelayMs

        // One-time migration of the browser-side value: a bench that had set it should not
        // silently lose it on the update that moved it. Only ever upgrades from the default,
        // so a value already stored on the server wins over any tab's stale copy.
        const legacy = readLegacy()
        if (legacy !== null) {
          if (captureDelayMs.value === 0 && legacy > 0) await set(legacy)
          try {
            localStorage.removeItem(LEGACY_STORAGE_KEY)
          } catch {
            // Storage that cannot be written cannot hold a stale value worth clearing.
          }
        }
      } catch {
        // A setting that could not be read is not worth an error on screen: 0 is what a fresh
        // install has, and the next edit writes a fresh document.
      }
    })()
    return loadPromise
  }

  async function set(ms: number) {
    const value = Number.isFinite(ms) && ms >= 0 ? ms : 0
    captureDelayMs.value = value
    await api.setCaptureDelay({ captureDelayMs: value })
  }

  return { captureDelayMs, load, set }
}

function readLegacy(): number | null {
  try {
    const stored = localStorage.getItem(LEGACY_STORAGE_KEY)
    if (stored === null) return null
    const value = Number(stored)
    return Number.isFinite(value) && value >= 0 ? value : null
  } catch {
    return null
  }
}
