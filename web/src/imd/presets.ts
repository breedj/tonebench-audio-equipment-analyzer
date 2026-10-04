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
  label: string
  lowHz: number
  highHz: number
  ratio: number
  method: ImdMethodName
  defaultMaxOrder: number
}

/** Highest order counted by default per method — the QA40x's own defaults (four SMPTE sideband pairs; the CCIF 2nd- and 3rd-order products). */
export const IMD_DEFAULT_MAX_ORDER: Record<ImdMethodName, number> = { Smpte: 5, Ccif: 3 }

export const IMD_PRESETS: readonly ImdPreset[] = [
  { value: 'Custom', label: 'Custom', lowHz: 0, highHz: 0, ratio: 1, method: 'Smpte', defaultMaxOrder: 5 },
  { value: 'Smpte', label: 'SMPTE 60 Hz + 7 kHz (4:1)', lowHz: 60, highHz: 7000, ratio: 4, method: 'Smpte', defaultMaxOrder: 5 },
  { value: 'Din', label: 'DIN 250 Hz + 8 kHz (4:1)', lowHz: 250, highHz: 8000, ratio: 4, method: 'Smpte', defaultMaxOrder: 5 },
  { value: 'CcifItu', label: 'CCIF/ITU-R 19 kHz + 20 kHz (1:1)', lowHz: 19000, highHz: 20000, ratio: 1, method: 'Ccif', defaultMaxOrder: 3 },
  { value: 'Ccif18p5', label: 'CCIF 18.5 + 19.5 kHz (1:1)', lowHz: 18500, highHz: 19500, ratio: 1, method: 'Ccif', defaultMaxOrder: 3 },
]

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
