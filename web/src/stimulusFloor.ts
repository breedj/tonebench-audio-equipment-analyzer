/**
 * The lowest frequency the connected analyzer can actually excite, and what to say about a sweep
 * asked to start below it.
 *
 * A QA403 is DC-coupled and has no floor, so this is silent on one. A sound card is AC-coupled at
 * *both* ends: below its corner the stimulus is attenuated on the way out and what returns is
 * attenuated again on the way in, and nothing fails — the deconvolution divides by the stimulus it
 * meant to play, so the card's own high-pass appears on the trace as the DUT's roll-off, with normal
 * coherence and nothing to suggest otherwise.
 *
 * The server clamps and says so afterwards (`StimulusFloor`); this is the same thing said *before*
 * the run, so the raising is not a surprise. It is a notice rather than a `problem`: the measurement
 * is perfectly good over the band the card can drive, and a default carried over from a QA403
 * project must not become an error to decode.
 *
 * Its own module because nothing in a `.vue` is testable under the node environment vitest runs in,
 * and because three panels ask the same question.
 */
export function stimulusFloorNotice(requestedHz: number, deviceFloorHz: number | null | undefined): string | null {
  if (!Number.isFinite(requestedHz)) return null
  if (deviceFloorHz == null || !Number.isFinite(deviceFloorHz) || deviceFloorHz <= 0) return null
  if (requestedHz >= deviceFloorHz) return null

  return `This analyzer cannot produce a usable stimulus below ${format(deviceFloorHz)} Hz — it is AC-coupled at both ends — so the sweep will start there rather than at ${format(requestedHz)} Hz. Below that corner it measures its own high-pass and reports it as the DUT's.`
}

function format(hz: number): string {
  return Number(hz.toFixed(1)).toString()
}
