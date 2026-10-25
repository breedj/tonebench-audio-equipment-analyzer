import { ref, watch } from 'vue'
import { readStoredPreference, writeStoredPreference } from '../browserStorage'

/**
 * <p>Which chart draws the two decay waterfalls: the GPU surface where the browser can manage it,
 * or the Canvas 2D projection always.</p>
 *
 * <p><b>Browser-local, not a document in the configuration database</b> — the same storage the
 * theme uses, and for the same reason. Whether the GPU surface is worth having is a property of
 * <em>this browser on this machine</em>: WebGPU is there or it is not, the driver behaves or it
 * does not. A setting in the configuration database is shared by every browser that connects to
 * the app, so a choice made in one would silently follow a bench to another where it means
 * something else.</p>
 *
 * <p>A module-level singleton, like <c>useAppTheme</c>: the Configuration dialog's selector and
 * <c>App.vue</c>'s chart switch have to agree on one value.</p>
 */

export type DecaySurfaceEngine = 'Auto' | 'Classic'

const STORAGE_KEY = 'tonebench.decaySurfaceEngine'

/** Anything unrecognised — an absent key, a cleared store, a hand-edited value — reads as Auto. */
export function parseEngine(stored: string | null): DecaySurfaceEngine {
  return stored === 'Classic' ? 'Classic' : 'Auto'
}

const decaySurfaceEngine = ref<DecaySurfaceEngine>(parseEngine(readStoredPreference(STORAGE_KEY)))

watch(decaySurfaceEngine, (mode) => {
  writeStoredPreference(STORAGE_KEY, mode)
})

/**
 * Whether the GPU surface may be used at all.
 *
 * <p>This is the preference only. It is one of three things that have to agree before the GPU
 * chart draws — the browser must offer a WebGPU adapter, and the mode must be a waterfall rather
 * than the sonogram, which is a flat colour map with nothing for a lit mesh to add.</p>
 */
export function prefersGpuSurface(mode: DecaySurfaceEngine): boolean {
  return mode === 'Auto'
}

export function useDecaySurfaceEngine() {
  return { decaySurfaceEngine }
}
