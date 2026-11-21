import {
  groupIntoSections,
  type PickerSection,
  type PickerSectionOf,
} from '../panels/pickerSections'
import type { GeneratorKind, GeneratorTone, ImdMethodName, IntermodulationConfigRequest } from '../api/client'

/**
 * The standard two-tone IMD tests. Mirrors Tonebench.Dsp.IntermodulationPresets — the ids are the C#
 * enum names because they travel in a measurement type's params_json — and presets.test.ts pins
 * the numbers to the tutorial's. `ratio` is amplitude (low:high), so 4 means the LF tone is
 * 12.04 dB above the HF one.
 */
export type ImdPresetId = 'Custom' | 'Smpte' | 'Din' | 'CcifItu' | 'Ccif18p5'

export interface ImdPreset {
  value: ImdPresetId
  /** The whole name, with its figures — what the button that opens the picker says. */
  label: string
  /**
   * The name alone, for a tile that carries the figures as chips beside it.
   *
   * <p>Separate from <code>label</code> rather than derived from it: "CCIF/ITU-R 19 kHz + 20 kHz
   * (1:1)" is one string to a reader scanning a dropdown and three different facts on a tile, and
   * splitting it with a regex would be a rule about punctuation rather than about the presets.</p>
   */
  name: string
  /** What the test is, in a line — the part a dropdown has no room for. */
  description: string
  /** Which section of the picker it is offered under. */
  section: ImdSectionId
  lowHz: number
  highHz: number
  ratio: number
  method: ImdMethodName
  defaultMaxOrder: number
}

export type ImdSectionId = 'modulation' | 'twin' | 'custom'

/** Highest order counted by default per method — the QA40x's own defaults (four SMPTE sideband pairs; the CCIF 2nd- and 3rd-order products). */
export const IMD_DEFAULT_MAX_ORDER: Record<ImdMethodName, number> = { Smpte: 5, Ccif: 3 }

export const IMD_PRESETS: readonly ImdPreset[] = [
  {
    value: 'Custom', label: 'Custom', name: 'Custom', section: 'custom',
    description: 'Your own two frequencies and level ratio, set in the stimulus panel.',
    lowHz: 0, highHz: 0, ratio: 1, method: 'Smpte', defaultMaxOrder: 5,
  },
  {
    value: 'Smpte', label: 'SMPTE 60 Hz + 7 kHz (4:1)', name: 'SMPTE', section: 'modulation',
    description: 'A low tone modulates a high one; the sidebands around 7 kHz are what is read.',
    lowHz: 60, highHz: 7000, ratio: 4, method: 'Smpte', defaultMaxOrder: 5,
  },
  {
    value: 'Din', label: 'DIN 250 Hz + 8 kHz (4:1)', name: 'DIN', section: 'modulation',
    description: 'The same method with a higher low tone, so it reaches less far into the bass.',
    lowHz: 250, highHz: 8000, ratio: 4, method: 'Smpte', defaultMaxOrder: 5,
  },
  {
    value: 'CcifItu', label: 'CCIF/ITU-R 19 kHz + 20 kHz (1:1)', name: 'CCIF / ITU-R', section: 'twin',
    description: 'Two equal tones 1 kHz apart. Products land at 1 kHz and at 18 and 21 kHz.',
    lowHz: 19000, highHz: 20000, ratio: 1, method: 'Ccif', defaultMaxOrder: 3,
  },
  {
    value: 'Ccif18p5', label: 'CCIF 18.5 + 19.5 kHz (1:1)', name: 'CCIF 18.5 + 19.5', section: 'twin',
    description: 'The same pair 500 Hz lower. Products at 1 kHz, 17.5 and 20.5 kHz.',
    lowHz: 18500, highHz: 19500, ratio: 1, method: 'Ccif', defaultMaxOrder: 3,
  },
]

/**
 * The picker's three sections — Joost's design of 2026-11-20.
 *
 * <p><b>By method, which is the thing being chosen.</b> SMPTE and DIN are one test at two
 * frequencies and CCIF is a different one, so grouping them that way puts the real decision at the
 * top level and the frequencies underneath it. <b>Custom is slate rather than an accent</b>: it is
 * deliberately not one of the named standards beside it, and a hue there would make it look like a
 * third one.</p>
 */
export const IMD_SECTIONS: readonly PickerSection[] = Object.freeze([
  { id: 'modulation', label: 'Modulation', note: 'low tone + high tone, 4:1', tone: 'blue', mark: 'orange' },
  { id: 'twin', label: 'Twin tone', note: 'two high tones, 1:1', tone: 'violet', mark: 'amber' },
  { id: 'custom', label: 'Custom', note: 'your own pair', tone: 'slate', mark: 'blue' },
])

/** The presets under their sections, in the order the dialog draws them. */
export function imdSections(
  presets: readonly ImdPreset[] = IMD_PRESETS,
): PickerSectionOf<ImdPreset>[] {
  return groupIntoSections(IMD_SECTIONS, presets, (preset) => preset.section)
}

/**
 * The chips beside a tile's name: the two frequencies and the ratio.
 *
 * <p>Custom has no figures of its own, so it names its three fields instead — which is what it is
 * offering rather than a promise about what it will play.</p>
 */
export function imdPresetChips(preset: ImdPreset): { text: string; tone: 'f1' | 'f2' | null }[] {
  if (preset.value === 'Custom') {
    return [
      { text: 'f1', tone: 'f1' },
      { text: 'f2', tone: 'f2' },
      { text: 'ratio', tone: null },
    ]
  }
  return [
    { text: formatPresetHz(preset.lowHz), tone: 'f1' },
    { text: formatPresetHz(preset.highHz), tone: 'f2' },
    { text: `${preset.ratio}:1`, tone: null },
  ]
}

/** A preset frequency as its chip writes it: hertz under a kilohertz, kilohertz above. */
export function formatPresetHz(hz: number): string {
  if (hz >= 1000) {
    const k = hz / 1000
    return `${Number.isInteger(k) ? k : k.toFixed(1)} kHz`
  }
  return `${hz} Hz`
}

export function imdPreset(id: ImdPresetId): ImdPreset {
  return IMD_PRESETS.find((p) => p.value === id) ?? IMD_PRESETS[0]!
}

/**
 * Splits a sine-equivalent level into the two tone levels: the pair's peak equals the peak of a
 * sine at `sineEquivalentDbv`, with V1 = ratio · V2. 4:1 → −1.94 / −13.98 dB, 1:1 → −6.02 dB each.
 * This is the tutorial's convention — every IMD test is set up so the two-tone peak equals the
 * rated-power sine's peak — and Tonebench.Dsp.IntermodulationPresets.ToneLevelsDbv is the same formula.
 */
export function toneLevelsDbv(sineEquivalentDbv: number, ratio: number): { lowDbv: number; highDbv: number } {
  const lowShare = ratio / (ratio + 1)
  const highShare = 1 / (ratio + 1)
  return {
    lowDbv: sineEquivalentDbv + 20 * Math.log10(lowShare),
    highDbv: sineEquivalentDbv + 20 * Math.log10(highShare),
  }
}

/** The inverse: the sine whose peak equals the pair's peak, 20·log10(V1 + V2). Null for anything but exactly two tones. */
export function sineEquivalentDbv(tones: readonly GeneratorTone[]): number | null {
  if (tones.length !== 2) return null
  const sum = tones.reduce((acc, t) => acc + Math.pow(10, t.ampDbv / 20), 0)
  return 20 * Math.log10(sum)
}

/** The two tones a preset plays at a sine-equivalent level, low tone first. */
export function presetTones(preset: ImdPreset, sineEquivalentDbvValue: number): GeneratorTone[] {
  const { lowDbv, highDbv } = toneLevelsDbv(sineEquivalentDbvValue, preset.ratio)
  return [
    { freqHz: preset.lowHz, ampDbv: round(lowDbv) },
    { freqHz: preset.highHz, ampDbv: round(highDbv) },
  ]
}

function round(dbv: number): number {
  return Math.round(dbv * 100) / 100
}

/**
 * The IMD spec the FFT screen pushes to the pipeline for a staged stimulus: exactly two sine tones
 * make one, anything else clears it. The tones are ordered low first regardless of how they were
 * typed, since the method's product rules are written in terms of f1 < f2.
 */
export function imdSpecFromStimulus(
  kind: GeneratorKind,
  tones: readonly GeneratorTone[],
  analysis: { method: ImdMethodName; maxOrder: number },
): IntermodulationConfigRequest {
  if (kind !== 'Tones' || tones.length !== 2) return { enabled: false }
  const [a, b] = tones as [GeneratorTone, GeneratorTone]
  const lowHz = Math.min(a.freqHz, b.freqHz)
  const highHz = Math.max(a.freqHz, b.freqHz)
  if (!(lowHz > 0) || !(highHz > lowHz)) return { enabled: false }
  return { enabled: true, toneLowHz: lowHz, toneHighHz: highHz, method: analysis.method, maxOrder: analysis.maxOrder }
}
