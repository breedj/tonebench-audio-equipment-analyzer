import type { WeightingSpec, WeightingStandardName } from './streaming/protocol'

/**
 * The noise weighting as the FFT settings panel, the metric tiles and the chart's annunciator name
 * it. Pure so it can be tested; the server owns the curves themselves (Qa403.Dsp.Weighting).
 */

export const NO_WEIGHTING: WeightingSpec = { standard: 'None', user1: false, user2: false }

export const WEIGHTING_OPTIONS: { value: WeightingStandardName; label: string }[] = [
  { value: 'None', label: 'None' },
  { value: 'A', label: 'A' },
  { value: 'C', label: 'C' },
  { value: 'ItuR468', label: 'ITU-R 468' },
  { value: 'CcirArm', label: 'CCIR/ARM' },
  { value: 'RiaaPlayback', label: 'RIAA playback' },
  { value: 'RiaaRecord', label: 'RIAA record' },
]

/** The short form the metric tiles carry as a qualifier: "A", "468", "A+U1". */
const SHORT: Record<WeightingStandardName, string> = {
  None: '',
  A: 'A',
  C: 'C',
  ItuR468: '468',
  CcirArm: 'CCIR',
  RiaaPlayback: 'RIAA',
  RiaaRecord: 'RIAA⁻¹',
}

/** The long form the chart's annunciator shows: "A-weighted", "ITU-R 468", "C + User 1 (my table)". */
const LONG: Record<WeightingStandardName, string> = {
  None: '',
  A: 'A-weighted',
  C: 'C-weighted',
  ItuR468: 'ITU-R 468',
  CcirArm: 'CCIR/ARM',
  RiaaPlayback: 'RIAA playback',
  RiaaRecord: 'RIAA record',
}

export function isWeighted(spec: WeightingSpec | null | undefined): boolean {
  return !!spec && (spec.standard !== 'None' || spec.user1 || spec.user2)
}

/**
 * The annunciator text, or null when nothing is weighted. A user slot is named after its table
 * when the name is known; a checked slot with no table says so, because it weighs nothing.
 */
export function weightingLabel(
  spec: WeightingSpec | null | undefined,
  user1Name?: string | null,
  user2Name?: string | null,
): string | null {
  if (!isWeighted(spec)) return null
  const parts: string[] = []
  if (spec!.standard !== 'None') parts.push(LONG[spec!.standard])
  if (spec!.user1) parts.push(user1Name ? `User 1 (${user1Name})` : 'User 1 (no table)')
  if (spec!.user2) parts.push(user2Name ? `User 2 (${user2Name})` : 'User 2 (no table)')
  return parts.join(' + ')
}

/** The tile qualifier, or null when nothing is weighted: "A", "A+U1", "U2". */
export function weightingQualifier(spec: WeightingSpec | null | undefined): string | null {
  if (!isWeighted(spec)) return null
  const parts: string[] = []
  if (spec!.standard !== 'None') parts.push(SHORT[spec!.standard])
  if (spec!.user1) parts.push('U1')
  if (spec!.user2) parts.push('U2')
  return parts.join('+')
}

/**
 * A stored type's weighting fields (params_json / MeasurementTypeAppliedResponse) as a spec; a row
 * an older build wrote, with none of them, is unweighted.
 */
export function weightingFromStored(
  standard: string | null | undefined,
  user1: boolean | null | undefined,
  user2: boolean | null | undefined,
): WeightingSpec {
  const known = WEIGHTING_OPTIONS.find((o) => o.value.toLowerCase() === (standard ?? '').toLowerCase())
  return { standard: known?.value ?? 'None', user1: user1 ?? false, user2: user2 ?? false }
}

/** The measurement-band presets the FFT settings panel offers. `Custom` is whatever the Configuration dialog holds. */
export const BANDWIDTH_PRESETS = [
  { value: 'audio', label: '20 Hz – 20 kHz', loHz: 20, hiHz: 20000 },
  { value: 'wide', label: '20 Hz – 80 kHz', loHz: 20, hiHz: 80000 },
] as const
export type BandwidthPreset = (typeof BANDWIDTH_PRESETS)[number]['value'] | 'custom'

/** Which preset the band edges are, or `custom` when they are neither. */
export function bandwidthPreset(loHz: number, hiHz: number): BandwidthPreset {
  return BANDWIDTH_PRESETS.find((p) => p.loHz === loHz && p.hiHz === hiHz)?.value ?? 'custom'
}

/** The band edges a preset stands for; null for `custom`, which leaves the values alone. */
export function bandwidthPresetValues(preset: BandwidthPreset): { loHz: number; hiHz: number } | null {
  const found = BANDWIDTH_PRESETS.find((p) => p.value === preset)
  return found ? { loHz: found.loHz, hiHz: found.hiHz } : null
}
