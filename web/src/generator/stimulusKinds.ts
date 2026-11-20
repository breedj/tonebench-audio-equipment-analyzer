/**
 * The stimuli the generator offers, as a table rather than a list of names in a dropdown.
 *
 * <p><b>Why a figure and a sentence.</b> Ten entries named <em>Square</em>, <em>Triangle</em>,
 * <em>Sawtooth</em> and <em>Pulse</em> are four words a reader parses where four pictures are
 * scanned, and the difference between the last two is one letter of their names and the whole of
 * their spectra — which is the same argument <code>scope/measurementIcons.ts</code> makes about
 * thirty measurement names. What a <code>&lt;select&gt;</code> cannot carry is exactly that
 * picture.</p>
 *
 * <p>In a module rather than in the panel because a dialog and the button that opens it both need
 * it, and because the audit that keeps every offered kind buildable can then import the list
 * instead of parsing it out of a <code>.vue</code> by its literal braces.</p>
 *
 * <p><b>Two kinds are deliberately absent and must stay so.</b> <code>Off</code> is not a stimulus:
 * the Generator panel's own channel pair is the on/off control, and a second way to say "silent"
 * would be two controls disagreeing. <code>EchoChirp</code> is hidden rather than deleted — the
 * kind, its form in the panel and the request type all still exist, and a stored measurement that
 * carries one still opens on it; it is slated for a UI of its own.</p>
 */

import type { GeneratorKind } from '../api/client'

export interface StimulusKindOption {
  value: GeneratorKind
  label: string
  /** A figure from <code>scope/displayIcons.ts</code>. */
  icon: string
  /** One line on what it is for, which is what the dialog has room for and a dropdown has not. */
  title: string
}

export const STIMULUS_KINDS: readonly StimulusKindOption[] = Object.freeze([
  {
    value: 'Tones' as const,
    label: 'Sine tones',
    icon: 'stimTones',
    title:
      'One tone, or several. The ordinary stimulus: distortion, level and frequency response are all read off it. Two tones at once is an intermodulation test.',
  },
  {
    value: 'Multitone' as const,
    label: 'Multitone',
    icon: 'stimMultitone',
    title:
      'A comb of tones across the band at once, so distortion and noise are read in the gaps between them. A whole response in one capture.',
  },
  {
    value: 'WhiteNoise' as const,
    label: 'White noise',
    icon: 'stimNoise',
    title: 'Equal energy per hertz. For noise floors, and for anything that wants no particular frequency.',
  },
  {
    value: 'Square' as const,
    label: 'Square',
    icon: 'stimSquare',
    title:
      'Hard-switched, so the fastest edge the DAC can produce — and odd harmonics only. What a scope is pointed at for ringing and rise time.',
  },
  {
    value: 'Triangle' as const,
    label: 'Triangle',
    icon: 'stimTriangle',
    title: 'A linear ramp up and down: odd harmonics falling twice as fast as a square’s.',
  },
  {
    value: 'Sawtooth' as const,
    label: 'Sawtooth',
    icon: 'stimSawtooth',
    title:
      'The one shape here that is not half-wave symmetric, so it is the only one with even harmonics of its own — which is what lets it say anything about second-order behaviour.',
  },
  {
    value: 'Pulse' as const,
    label: 'Pulse',
    icon: 'stimPulse',
    title:
      'A square with its duty chosen. Bipolar at every duty, so the level and the range it needs do not move as the width does.',
  },
  {
    value: 'ToneBurst' as const,
    label: 'Tone burst',
    icon: 'stimBurst',
    title:
      'A tone switched on and off in whole cycles. For what an amplifier does with a signal that starts — recovery, thermal behaviour, a supply that sags.',
  },
  {
    value: 'DcLevel' as const,
    label: 'DC level',
    icon: 'stimDc',
    title:
      'A steady voltage, signed. Both analyzer inputs are AC-coupled and will not read it back, and a sound card will not pass it at all.',
  },
  {
    value: 'Arbitrary' as const,
    label: 'Arbitrary (.wav)',
    icon: 'stimArbitrary',
    title: 'A file played on a loop, resampled to the analyzer’s own rate and normalised to the level you set.',
  },
])

/** One kind's entry, or null for a kind this picker does not offer — `Off` and `EchoChirp`. */
export function stimulusKind(kind: GeneratorKind): StimulusKindOption | null {
  return STIMULUS_KINDS.find((option) => option.value === kind) ?? null
}

/**
 * What the button that opens the picker says.
 *
 * <p>A kind the picker does not offer still has to read as something, because a stored measurement
 * can carry one: it keeps its own name rather than showing blank or being quietly swapped for a
 * kind nobody chose.</p>
 */
export function stimulusKindLabel(kind: GeneratorKind): string {
  return stimulusKind(kind)?.label ?? kind
}
