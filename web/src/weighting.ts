import type { WeightingSpec, WeightingStandardName } from './streaming/protocol'

/**
 * The noise weighting and the measurement band as the Weighting panel, the metrics picker, the
 * metric tiles and the chart's annunciator name them. Pure so it can be tested; the server owns
 * the curves themselves (Tonebench.Dsp.Weighting).
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

/** The measurement-band presets the metrics picker offers. `Custom` is the two edges typed beside it. */
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

/**
 * The band a measurement starts with, and what the metrics picker's Custom boxes show until
 * something else has been set. It is also SpectrumPipelineOptions' own default server-side, so a
 * bench that has never touched the band reads these two numbers rather than a blank pair.
 */
export const DEFAULT_BANDWIDTH = { loHz: 20, hiHz: 20000 } as const

/**
 * One end of a band, written the way the presets write theirs: whole hertz below a kilohertz,
 * kHz above it with no trailing zeros. Deliberately not response/format.ts's formatHz, whose two
 * decimals are there for a measured corner -- a band edge is a number the user typed, and
 * "20.00 Hz - 20.00 kHz" reads as a precision it does not have.
 */
function formatBandEdge(hz: number): string {
  if (!Number.isFinite(hz)) return '?'
  if (hz >= 1000) return `${Number((hz / 1000).toFixed(3))} kHz`
  return `${Number(hz.toFixed(2))} Hz`
}

/**
 * The band as the picker's dropdown and the tiles that are integrated over it both name it. A
 * preset gets that preset's own label, so the footer under a THD+N tile and the dropdown above it
 * cannot read differently for the same two numbers.
 */
export function bandwidthLabel(loHz: number, hiHz: number): string {
  const preset = BANDWIDTH_PRESETS.find((p) => p.loHz === loHz && p.hiHz === hiHz)
  return preset ? preset.label : `${formatBandEdge(loHz)} – ${formatBandEdge(hiHz)}`
}

/**
 * Why these two edges are not a band, or null when they are. Mirrors the server's own check on
 * `PUT /api/spectrum-config` (AcquisitionEndpoints) so the picker can refuse before it asks: a
 * dialog that sends an impossible band gets a 400 whose message names the wire field rather than
 * the control. A blank box is NaN through `v-model.number`, which is not a band either.
 */
export function bandwidthRangeError(loHz: number, hiHz: number): string | null {
  if (!Number.isFinite(loHz) || !Number.isFinite(hiHz)) return 'Both band edges have to be numbers.'
  if (loHz < 0) return 'The lower band edge cannot be negative.'
  if (hiHz <= loHz) return 'The upper band edge has to be above the lower one.'
  return null
}
