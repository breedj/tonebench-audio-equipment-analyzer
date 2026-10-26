import type { ResponseStimulusName } from '../api/client'
import { extractsHarmonics, stimulusLabel } from './responseStimulus'

/**
 * <p>Why the impulse screen's Harmonics view has nothing to draw, in one line, or null when it has
 * something.</p>
 *
 * <p>Harmonic curves are read out of a sweep's own deconvolution residue — a sweep's distortion
 * comes back as packets at known places, while noise spreads its evenly across the record as a
 * raised floor that nothing can separate — so every PN stimulus produces none at all. Pink PN has
 * been the default since 2026-10-24, which makes an empty Harmonics view the ordinary case rather
 * than a corner, and it drew an empty chart with the Autofit button disabled beside it, both
 * correct and neither saying so. Reported 2026-10-26 as the display not working any more.</p>
 *
 * <p>In a module because two places say it — the line under the chart and the one in the display
 * rail beside the disabled button — and an explanation that exists twice is one that comes to
 * disagree with itself. Testable here, as nothing in a <c>.vue</c> is.</p>
 */
export function harmonicsUnavailableNote(
  stimulus: ResponseStimulusName | null | undefined,
  hasCurves: boolean,
): string | null {
  if (hasCurves) return null
  // Nothing has run, or the run is from a build that does not report its stimulus. The chart's own
  // empty state covers the first and there is nothing honest to say about the second.
  if (!stimulus) return null
  if (extractsHarmonics(stimulus)) return null
  return `This run played ${stimulusLabel(stimulus)}. Harmonic curves come from a sweep, whose distortion `
    + 'returns as separate packets the analysis can pick out; noise spreads its distortion evenly '
    + 'across the record, where nothing can separate it. Measure with Sweep (chirp) and Harmonics on.'
}

/** The same thing for a control's tooltip, where the full paragraph would not fit. */
export function harmonicsUnavailableShort(
  stimulus: ResponseStimulusName | null | undefined,
  hasCurves: boolean,
): string | null {
  if (harmonicsUnavailableNote(stimulus, hasCurves) === null) return null
  return `No harmonic curves: this run played ${stimulusLabel(stimulus as ResponseStimulusName)}, and only a sweep produces them.`
}
