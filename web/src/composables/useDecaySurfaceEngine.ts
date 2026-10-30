import { ref, watch } from 'vue'
import { readStoredPreference, writeStoredPreference } from '../browserStorage'

/**
 * <p>Which chart draws the two decay waterfalls: the turnable 3D surface, or the Canvas 2D
 * projection always.</p>
 *
 * <p><b>It was one of three gates and is now the only one.</b> Until 2026-10-30 the 3D surface was
 * drawn by a WebGPU engine that, handed no adapter, drew a plausible surface that was not the data
 * — so the chart had to be capability-gated and this setting was "3D where the browser can manage
 * it". <c>vis-graph3d</c> is a plain 2D canvas and draws the same picture anywhere, so there is
 * nothing left to probe: this is a preference between two ways of reading one measurement, and the
 * only other thing that still decides is whether the mode is a waterfall at all rather than the
 * sonogram, which is a flat colour map with nothing for a box to add.</p>
 *
 * <p><b>Browser-local, not a document in the configuration database</b> — the same storage the
 * theme uses. It is a choice about how to look at a chart, made at whichever machine is looking;
 * a setting in the configuration database is shared by every browser that connects to the app.</p>
 *
 * <p>A module-level singleton, like <c>useAppTheme</c>: the Configuration dialog's selector and
 * <c>App.vue</c>'s chart switch have to agree on one value.</p>
 */

/**
 * <p>The stored spellings are <c>'Auto'</c> and <c>'Classic'</c> and are <b>deliberately not
 * renamed</b>, although 'Auto' no longer describes anything automatic. They are what is already
 * written into this browser's storage, and a bench that chose the flat projection must keep it
 * across this change rather than silently getting the 3D one back.</p>
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
 * Whether the 3D surface is wanted at all.
 *
 * <p>This is the preference only. The mode still has to be a waterfall rather than the sonogram —
 * see <c>decayModeIs3d</c> — which is the one other thing that decides.</p>
 */
export function prefers3dSurface(mode: DecaySurfaceEngine): boolean {
  return mode === 'Auto'
}

export function useDecaySurfaceEngine() {
  return { decaySurfaceEngine }
}
