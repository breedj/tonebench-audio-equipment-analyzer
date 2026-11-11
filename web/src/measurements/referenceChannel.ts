import type { ReferenceChannelJson } from '../api/client'

/**
 * What to say when the reference input is not carrying a reference.
 *
 * <p><b>The gap this closes.</b> <em>Right channel is reference</em> is ticked by default and
 * nothing checked that the right input was fed. On a bench with one channel wired that produced a
 * complete, plausible measurement built on dividing by noise — the Gain trace, its phase, its group
 * delay and the latency reading, all of it — with nothing on screen saying so. Reported from the
 * bench 2026-11-11, where it first surfaced one layer along, as the clock-drift residual claiming a
 * figure was stale that three independent measurements agreed on to 0.3 ppm.</p>
 *
 * <p>The decision is the server's — see `ResponseCaptureSupport.CheckReferenceChannel`, which
 * carries why the test is phase roughness and not coherence or level. What is here is the wording,
 * in a module rather than in the two `.vue` panels for the usual reason (nothing in a `.vue` is
 * testable here) and shared rather than copied, because the frequency-response and impulse-response
 * screens are the same measurement at two ends.</p>
 */

/**
 * The amber line, or null when there is nothing to say — no reference on this run, or one that is
 * carrying a signal.
 */
export function referenceChannelWarning(
  reference: ReferenceChannelJson | null | undefined,
): string | null {
  if (!reference || reference.usable) return null
  if (!Number.isFinite(reference.snrDb)) return null

  return (
    `Almost nothing is arriving on the reference input — about ${reference.snrDb.toFixed(0)} dB of ` +
    `signal against its own noise. Feed the generator output to the right input as well as to the ` +
    `DUT, or clear this box: a bench with one channel wired has no reference to measure against. ` +
    `Until then the Gain trace, its phase and group delay, and the delay reading are a division by ` +
    `noise — the curves will look plausible and mean nothing.`
  )
}

/**
 * The quiet counterpart: what the reference channel is carrying, when it is carrying something.
 *
 * <p>Worth a line of its own because the warning above is the first thing in this app to measure
 * it at all, and a figure that only ever appears when it is bad teaches nobody what good looks
 * like. Capped rather than printed: a synthetic trace reads the estimator's own 200 dB ceiling,
 * which is a statement about double precision rather than about a bench.</p>
 */
export function referenceChannelNote(
  reference: ReferenceChannelJson | null | undefined,
): string | null {
  if (!reference || !reference.usable) return null
  if (!Number.isFinite(reference.snrDb)) return null

  return reference.snrDb >= REFERENCE_SNR_CEILING_DB
    ? 'The reference input is carrying the stimulus cleanly.'
    : `The reference input is carrying about ${reference.snrDb.toFixed(0)} dB of signal against its own noise.`
}

/**
 * Past this the estimate is reporting the arithmetic's own floor rather than the bench's, so the
 * note says "cleanly" instead of quoting a figure nobody could act on. Below `TransferFunctionSnr`'s
 * own 200 dB cap so a simulated run, which reads the cap exactly, lands on the wording rather than
 * on the number.
 */
export const REFERENCE_SNR_CEILING_DB = 120
