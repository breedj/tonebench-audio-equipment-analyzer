import { ref, watch } from 'vue'

// Module-level singleton (same pattern as useAppTheme.ts) — the Configuration dialog's field and
// App.vue's Run/Single handlers must agree on one value. Purely a client-side scheduling knob (how
// long to wait, after the generator is enabled, before starting acquisition) — never sent to the
// server, so it's persisted in localStorage rather than the server-side spectrum config.

const STORAGE_KEY = 'qa403.captureDelayMs'

function loadInitial(): number {
  const stored = Number(localStorage.getItem(STORAGE_KEY))
  return Number.isFinite(stored) && stored >= 0 ? stored : 0
}

const captureDelayMs = ref<number>(loadInitial())

watch(captureDelayMs, (ms) => {
  localStorage.setItem(STORAGE_KEY, String(ms))
})

export function useCaptureDelay() {
  return { captureDelayMs }
}
