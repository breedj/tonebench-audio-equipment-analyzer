import type { ResponseStimulusName } from '../api/client'

/**
 * The stimulus a response measurement plays, and the two questions every panel offering it asks:
 * which of them need the noise cut-off, and which of them can draw harmonic curves.
 *
 * <p>In a module rather than in the two `.vue` panels for the usual reason — nothing in a `.vue` is
 * testable under the node environment vitest runs in — and shared rather than copied because the
 * frequency-response and impulse-response screens offer the same list and must agree about what a
 * stimulus means. They are the same measurement at two ends, and a list that drifted between them
 * is how one screen comes to offer a choice the other refuses.</p>
 */
export const RESPONSE_STIMULI: { value: ResponseStimulusName; label: string }[] = [
  { value: 'Chirp', label: 'Sweep (chirp)' },
  { value: 'PinkNoise', label: 'Pink PN' },
  { value: 'WhiteNoise', label: 'White PN' },
  { value: 'SpeechMale2011', label: 'Male PN [2011]' },
  { value: 'SpeechFemale2011', label: 'Female PN [2011]' },
  { value: 'SpeechMale2020', label: 'Male PN [2020]' },
]

/** What the dropdown calls a stimulus, for prose that has to name one the user picked. */
export function stimulusLabel(stimulus: ResponseStimulusName): string {
  return RESPONSE_STIMULI.find((s) => s.value === stimulus)?.label ?? stimulus
}

/** Everything that is not the sweep: a periodic noise sequence over exactly one analysis record. */
export function isNoiseStimulus(stimulus: ResponseStimulusName): boolean {
  return stimulus !== 'Chirp'
}

/**
 * Whether the cut-off control means anything. The white sequence has no slope for a corner to
 * start, and the sweep has no sequence at all — so the field is offered for pink and the three
 * speech shapes only, which are the ones that rise towards DC without it.
 */
export function usesNoiseCutoff(stimulus: ResponseStimulusName): boolean {
  return isNoiseStimulus(stimulus) && stimulus !== 'WhiteNoise'
}

/**
 * Whether this stimulus can be asked for harmonic curves. Only the sweep: its distortion comes back
 * as packets at known places, which is what the impulse screen reads them from, while noise spreads
 * its distortion evenly over the record as a raised floor that nothing can separate out.
 */
export function extractsHarmonics(stimulus: ResponseStimulusName): boolean {
  return stimulus === 'Chirp'
}

/** Whether the chirp's own start frequency is read. Nothing else has a start. */
export function usesChirpStart(stimulus: ResponseStimulusName): boolean {
  return stimulus === 'Chirp'
}

/**
 * The one line a panel shows under the dropdown, naming what the choice costs rather than repeating
 * the label. Null for the sweep, which is the default and needs no explaining.
 */
export function stimulusNotice(stimulus: ResponseStimulusName): string | null {
  switch (stimulus) {
    case 'Chirp':
      return null
    case 'PinkNoise':
      return 'Equal energy per octave — the best signal-to-noise at the bottom of the band, and what ARTA recommends for acoustic work.'
    case 'WhiteNoise':
      return 'Equal energy per bin, so the top of the band is measured as well as the bottom. ARTA recommends it for electronic systems.'
    default:
      return 'A speech-shaped spectrum from IEC 60268-16, for speech-intelligibility work. Most of its energy is below 1 kHz.'
  }
}

/**
 * A stored stimulus name, or the sweep. Absent on anything saved before the choice existed, which
 * is what those builds played; a name this build does not know is treated the same way rather than
 * refused, since the alternative is a measurement that cannot be opened.
 */
export function readStimulus(stored: string | null | undefined): ResponseStimulusName {
  return RESPONSE_STIMULI.some((option) => option.value === stored)
    ? (stored as ResponseStimulusName)
    : 'Chirp'
}
