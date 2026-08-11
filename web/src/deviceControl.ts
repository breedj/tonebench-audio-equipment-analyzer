// Plain module functions, not a composable — this holds no reactive state, it just gives
// useProjects.ts (switching measurements) and App.vue's Stop button one shared
// implementation of "make sure nothing is playing or acquiring" instead of two.
import { api } from './api/client'

// A minimal, always-valid "off" payload — kind/tones don't matter once disabled, so this doesn't
// need to know or preserve whatever was actually configured before.
const DISABLED_CHANNEL = { enabled: false, kind: 'Tones' as const, tones: [] }

/**
 * Stops acquisition and silences the generator. Safe to call even when neither is running
 * (StopAcquisitionAsync is idempotent server-side, and disabling an already-disabled channel is a
 * no-op) — used both for the explicit Stop button and automatically when switching measurements.
 */
export async function stopRun(): Promise<void> {
  await api.setGenerator({ left: DISABLED_CHANNEL, right: DISABLED_CHANNEL })
  await api.stopAcquisition()
}
