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

import {
  groupIntoSections,
  type PickerSection,
  type PickerSectionOf,
} from '../panels/pickerSections'
import type { GeneratorKind } from '../api/client'

export interface StimulusKindOption {
  value: GeneratorKind
  label: string
  /** A figure from <code>scope/displayIcons.ts</code>. */
  icon: string
  /** One line on what it is for, which is what the dialog has room for and a dropdown has not. */
  title: string
  /** Which section of the picker it is offered under — see <code>STIMULUS_SECTIONS</code>. */
  section: StimulusSectionId
}

export type StimulusSectionId = 'tones' | 'broadband' | 'waveforms' | 'steady'

/**
 * The picker's four sections — Joost's design of 2026-11-20.
 *
 * <p><b>Grouped by what the stimulus is for rather than by what it looks like</b>, which is the
 * question a reader opening this dialog actually has. A tone burst is a sine, so by shape it
 * belongs with the tones; by purpose it is beside them because both are read at one frequency. The
 * four waveforms are together because what they share is a <em>known harmonic series</em> — the
 * reason any of them is ever chosen — and DC sits with the file because neither is a signal this
 * analyzer can characterise, which is the caveat both of their sentences carry.</p>
 */
export const STIMULUS_SECTIONS: readonly PickerSection[] = Object.freeze([
  { id: 'tones', label: 'Tones', note: 'distortion, level, response', tone: 'blue', mark: 'orange' },
  { id: 'broadband', label: 'Broadband', note: 'the whole band at once', tone: 'violet', mark: 'amber' },
  { id: 'waveforms', label: 'Waveforms', note: 'known harmonics, edges', tone: 'teal', mark: 'pink' },
  { id: 'steady', label: 'Steady & file', note: 'DC and your own signal', tone: 'red', mark: 'cyan' },
])

/**
 * One kind's section, for the button that opens the picker.
 *
 * <p>So the figure on the button is drawn exactly as the tile it was chosen from drew it — which
 * is the whole of what the button is: a picture of the choice. Null for a kind the picker does not
 * offer (<code>Off</code>, <code>EchoChirp</code>), which a stored measurement can still carry.</p>
 */
export function stimulusSection(kind: GeneratorKind): PickerSection | null {
  const option = stimulusKind(kind)
  if (!option) return null
  return STIMULUS_SECTIONS.find((section) => section.id === option.section) ?? null
}

/** The kinds under their sections, in the order the dialog draws them. */
export function stimulusSections(
  kinds: readonly StimulusKindOption[] = STIMULUS_KINDS,
): PickerSectionOf<StimulusKindOption>[] {
  return groupIntoSections(STIMULUS_SECTIONS, kinds, (kind) => kind.section)
}

export const STIMULUS_KINDS: readonly StimulusKindOption[] = Object.freeze([
  {
    value: 'Tones' as const,
    label: 'Sine tones',
    icon: 'stimTones',
    title:
      'One tone, or several. The ordinary stimulus: distortion, level and frequency response are all read off it. Two tones at once is an intermodulation test.',
    section: 'tones',
  },
  {
    value: 'Multitone' as const,
    label: 'Multitone',
    icon: 'stimMultitone',
    title:
      'A comb of tones across the band at once, so distortion and noise are read in the gaps between them. A whole response in one capture.',
    section: 'broadband',
  },
  {
    value: 'WhiteNoise' as const,
    label: 'White noise',
    icon: 'stimNoise',
    title: 'Equal energy per hertz. For noise floors, and for anything that wants no particular frequency.',
    section: 'broadband',
  },
  {
    value: 'Square' as const,
    label: 'Square',
    icon: 'stimSquare',
    title:
      'Hard-switched, so the fastest edge the DAC can produce — and odd harmonics only. What a scope is pointed at for ringing and rise time.',
    section: 'waveforms',
  },
  {
    value: 'Triangle' as const,
    label: 'Triangle',
    icon: 'stimTriangle',
    title: 'A linear ramp up and down: odd harmonics falling twice as fast as a square’s.',
    section: 'waveforms',
  },
  {
    value: 'Sawtooth' as const,
    label: 'Sawtooth',
    icon: 'stimSawtooth',
    title:
      'The one shape here that is not half-wave symmetric, so it is the only one with even harmonics of its own — which is what lets it say anything about second-order behaviour.',
    section: 'waveforms',
  },
  {
    value: 'Pulse' as const,
    label: 'Pulse',
    icon: 'stimPulse',
    title:
      'A square with its duty chosen. Bipolar at every duty, so the level and the range it needs do not move as the width does.',
    section: 'waveforms',
  },
  {
    value: 'ToneBurst' as const,
    label: 'Tone burst',
    icon: 'stimBurst',
    title:
      'A tone switched on and off in whole cycles. For what an amplifier does with a signal that starts — recovery, thermal behaviour, a supply that sags.',
    section: 'tones',
  },
  {
    value: 'DcLevel' as const,
    label: 'DC level',
    icon: 'stimDc',
    title:
      'A steady voltage, signed. Both analyzer inputs are AC-coupled and will not read it back, and a sound card will not pass it at all.',
    section: 'steady',
  },
  {
    value: 'Arbitrary' as const,
    label: 'Arbitrary (.wav)',
    icon: 'stimArbitrary',
    title: 'A file played on a loop, resampled to the analyzer’s own rate and normalised to the level you set.',
    section: 'steady',
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
