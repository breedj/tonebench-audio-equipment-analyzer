import { computed, ref } from 'vue'
import { api, type GainUnitName } from '../api/client'

/**
 * The external input gain: what sits between the DUT and each analyzer input, in dB (a 10:1
 * divider is −20). Module-level singleton like useCaptureDelay, and stored the same way — in the
 * server's configuration database (`acquisition.externalGain`), applied *server-side* at the
 * pipeline's door, so the live FFT screen and every runner read DUT-referred levels alike.
 *
 * This is the **shared bench** — the QA403's and the simulator's. The sound card keeps its own in
 * its configuration document, and `DeviceSessionService` applies whichever analyzer is live; the
 * status bar edits whichever that is, routing in App.vue's `setExternalGain`. So what is read back
 * here is not necessarily what the pipeline is applying, and the one thing that must always be the
 * *effective* figure — the badge on the bar — reads `DeviceStatus.externalGain*` instead.
 *
 * The browser needs this document for exactly one thing now: the simulator's ranging watcher in
 * App.vue, which has to hand the range ladder the analyzer's own peak rather than the DUT's (the
 * meters frame carries the DUT's). The simulator runs on this bench, so this is its gain.
 */
const leftDb = ref(0)
const rightDb = ref(0)
const unit = ref<GainUnitName>('Db')
let loadPromise: Promise<void> | null = null

export function useExternalGain() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        const config = await api.getExternalGain()
        leftDb.value = config.leftDb
        rightDb.value = config.rightDb
      } catch {
        // A setting that could not be read is not worth an error on screen: 0 is what a fresh
        // install has, and the next edit writes a fresh document.
      }
    })()
    return loadPromise
  }

  async function set(config: { leftDb: number; rightDb: number; unit?: GainUnitName }) {
    const clean = {
      leftDb: sanitize(config.leftDb),
      rightDb: sanitize(config.rightDb),
      // Left where it was when the caller only means to change the two figures.
      unit: config.unit ?? unit.value,
    }
    leftDb.value = clean.leftDb
    rightDb.value = clean.rightDb
    unit.value = clean.unit
    await api.setExternalGain(clean)
  }

  /** The gain of channel index 0 (left) or 1 (right), for the ranging watcher. */
  function gainDb(channel: number): number {
    return channel === 1 ? rightDb.value : leftDb.value
  }

  return {
    leftDb,
    rightDb,
    unit,
    /** True when either input has something in front of it — the status bar's cue. */
    any: computed(() => leftDb.value !== 0 || rightDb.value !== 0),
    load,
    set,
    gainDb,
  }
}

/** ±60 dB is the server's bound (ExternalGainConfig.MaxAbsDb); anything else is a typo, not a fixture. */
const MAX_ABS_DB = 60

function sanitize(db: number): number {
  if (!Number.isFinite(db)) return 0
  return Math.max(-MAX_ABS_DB, Math.min(MAX_ABS_DB, db))
}
