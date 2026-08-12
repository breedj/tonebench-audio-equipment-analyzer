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
 *
 * Order matters: stopAcquisition first, generator-disable second. DeviceSessionService.
 * ConfigureGeneratorAsync flushes the spectrum pipeline's retained snapshot whenever it's called
 * while still acquiring (correctly — an in-flight FFT record shouldn't straddle a stimulus
 * change). Disabling the generator before stopping acquisition used to hit exactly that branch,
 * wiping out the very capture "Save measurement result" needs — Stop, then Save reliably failed
 * with "No spectrum has been captured yet." even though the spectrum trace was still visibly on
 * screen (the client just keeps rendering the last frame it received, unaware the server discarded
 * its copy). Stopping acquisition first means _isAcquiring is already false by the time the
 * generator is disabled, so that branch isn't taken and the retained snapshot survives.
 */
export async function stopRun(): Promise<void> {
  await api.stopAcquisition()
  await api.setGenerator({ left: DISABLED_CHANNEL, right: DISABLED_CHANNEL })
}
