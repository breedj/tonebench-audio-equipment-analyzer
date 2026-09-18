import {
  RESPONSE_RESOLUTIONS,
  RESPONSE_SMOOTHING,
  type FrequencyResponseParameters,
  type ReportRow,
  type ReportSection,
  type ResponseView,
  type SteppedResponseParameters,
  type SweepLevelMode,
  type SweepLevelUnit,
} from '../../api/client'
import type { ResponseTrace } from '../../composables/useFrequencyResponse'
import { isSteppedResponseKind } from '../../measurements/kinds'
import { formatBandwidth, formatCorner, formatDb, formatHz, formatRatio } from '../../response/format'
import {
  CLAMPED_CORNER_NOTE,
  peakLabel,
  primaryResponseTrace,
  REFERENCE_FELL_BACK_NOTE,
  referenceLabel,
} from '../../response/measurements'
import type { ReportElementId } from '../elements'
import type { ReportSubject } from '../types'
import { descriptionSections, genericSettingsSection } from './generic'

/**
 * The frequency-response chapter: the chirp's and the stepped sine's, since the two produce the same
 * curves and are read off the same Measurements panel. The chart itself is not built here — it is a
 * PNG the orchestrator renders — but every number is, from the same helpers the panel uses, so the
 * paper and the screen cannot disagree.
 */

const LEVEL_MODE_LABELS: Record<SweepLevelMode, string> = {
  GeneratorVoltage: 'Input voltage (generator)',
  OutputVoltage: 'DUT output voltage',
  OutputPower: 'DUT output power',
}

function formatLevel(value: number, unit: SweepLevelUnit): string {
  switch (unit) {
    case 'Dbv':
      return `${formatNumber(value)} dBV`
    case 'Volt':
      return `${formatNumber(value)} V`
    case 'MilliVolt':
      return `${formatNumber(value)} mV`
    case 'Watt':
      return `${formatNumber(value)} W`
  }
}

function formatNumber(value: number): string {
  if (!Number.isFinite(value)) return '—'
  return Number.isInteger(value) ? String(value) : String(Number(value.toFixed(3)))
}

function levelRow(params: { levelMode?: SweepLevelMode; levelValue?: number; levelUnit?: SweepLevelUnit; loadOhms?: number | null }): ReportRow | null {
  if (params.levelMode === undefined || params.levelValue === undefined || params.levelUnit === undefined) return null
  let value = `${formatLevel(params.levelValue, params.levelUnit)} — ${LEVEL_MODE_LABELS[params.levelMode]}`
  if (params.levelMode === 'OutputPower' && typeof params.loadOhms === 'number') value += ` into ${formatNumber(params.loadOhms)} Ω`
  return { label: 'Level', value }
}

function bandwidthReferenceRow(params: {
  bandwidthReference?: string | null
  bandwidthReferenceLowHz?: number | null
  bandwidthReferenceHighHz?: number | null
}): ReportRow | null {
  if (!params.bandwidthReference) return null
  if (params.bandwidthReference === 'Peak') return { label: 'Bandwidth reference', value: 'Peak' }
  const low = typeof params.bandwidthReferenceLowHz === 'number' ? formatHz(params.bandwidthReferenceLowHz) : '?'
  const high = typeof params.bandwidthReferenceHighHz === 'number' ? formatHz(params.bandwidthReferenceHighHz) : '?'
  return { label: 'Bandwidth reference', value: `Passband median, ${low} – ${high}` }
}

function captureDelayRow(params: { captureDelayMs?: number | null }): ReportRow | null {
  if (typeof params.captureDelayMs !== 'number' || params.captureDelayMs <= 0) return null
  return { label: 'Capture delay', value: `${formatNumber(params.captureDelayMs)} ms` }
}

function generatorRow(params: { generatorAmpDbv?: number | null }): ReportRow | null {
  if (typeof params.generatorAmpDbv !== 'number') return null
  return { label: 'Generator level', value: `${params.generatorAmpDbv.toFixed(2)} dBV` }
}

function push(rows: ReportRow[], row: ReportRow | null) {
  if (row) rows.push(row)
}

function chirpSettingsRows(params: Partial<FrequencyResponseParameters & { captureDelayMs?: number | null }>): ReportRow[] {
  const rows: ReportRow[] = []
  if (typeof params.frequencySpanHz === 'number') {
    const start = typeof params.chirpStartHz === 'number' ? formatHz(params.chirpStartHz) : null
    rows.push({ label: 'Sweep', value: start ? `${start} – ${formatHz(params.frequencySpanHz)}` : `to ${formatHz(params.frequencySpanHz)}` })
  }
  if (typeof params.resolutionMultiplier === 'number') {
    const option = RESPONSE_RESOLUTIONS.find((r) => r.multiplier === params.resolutionMultiplier)
    rows.push({ label: 'Resolution', value: option ? `${option.label} Hz` : `×${params.resolutionMultiplier}` })
  }
  push(rows, levelRow(params))
  if (typeof params.rightChannelIsReference === 'boolean') {
    rows.push({
      label: 'Reference',
      value: params.rightChannelIsReference ? 'Right input (left measured relative to it)' : 'Generated sweep',
    })
  }
  if (typeof params.smoothingDenominator === 'number') {
    const option = RESPONSE_SMOOTHING.find((s) => s.denominator === params.smoothingDenominator)
    rows.push({
      label: 'Smoothing',
      value: option ? option.label : params.smoothingDenominator > 0 ? `1/${params.smoothingDenominator} octave` : 'None',
    })
  }
  if (typeof params.averageCount === 'number') rows.push({ label: 'Averages', value: String(params.averageCount) })
  push(rows, generatorRow(params))
  push(rows, bandwidthReferenceRow(params))
  push(rows, captureDelayRow(params))
  return rows
}

function steppedSettingsRows(params: Partial<SteppedResponseParameters & { captureDelayMs?: number | null; bandwidthReference?: string | null; bandwidthReferenceLowHz?: number | null; bandwidthReferenceHighHz?: number | null }>): ReportRow[] {
  const rows: ReportRow[] = []
  if (typeof params.startFreqHz === 'number' && typeof params.endFreqHz === 'number') {
    rows.push({ label: 'Sweep', value: `${formatHz(params.startFreqHz)} – ${formatHz(params.endFreqHz)}` })
  }
  if (typeof params.stepsPerOctave === 'number') rows.push({ label: 'Steps per octave', value: String(params.stepsPerOctave) })
  push(rows, levelRow(params))
  if (typeof params.generateLeft === 'boolean' || typeof params.generateRight === 'boolean') {
    const channels = [params.generateLeft ? 'Left' : null, params.generateRight ? 'Right' : null].filter(Boolean)
    rows.push({ label: 'Tone on', value: channels.length > 0 ? channels.join(' and ') : 'Neither' })
  }
  if (typeof params.rightChannelIsReference === 'boolean') {
    rows.push({
      label: 'Reference',
      value: params.rightChannelIsReference ? 'Right input (left measured relative to it)' : 'None (channel difference)',
    })
  }
  if (params.adaptiveResolution === true) {
    rows.push({ label: 'Resolution', value: 'Adaptive per point' })
  } else if (typeof params.fftSize === 'number' || typeof params.sampleRateHz === 'number' || params.window) {
    const parts = [
      typeof params.fftSize === 'number' ? `${params.fftSize}-point` : null,
      params.window ?? null,
      typeof params.sampleRateHz === 'number' ? `at ${formatHz(params.sampleRateHz)}` : null,
    ].filter(Boolean)
    rows.push({ label: 'Resolution', value: parts.join(' ') })
  }
  if (typeof params.averageCount === 'number') rows.push({ label: 'Averages', value: String(params.averageCount) })
  push(rows, generatorRow(params))
  push(rows, bandwidthReferenceRow(params))
  push(rows, captureDelayRow(params))
  return rows
}

/** The Settings section's rows for either response kind, labelled the way the config panels label them. */
export function frequencyResponseSettingsRows(kind: string, paramsJson: string): ReportRow[] {
  let params: Record<string, unknown>
  try {
    params = JSON.parse(paramsJson) as Record<string, unknown>
  } catch {
    return []
  }
  if (!params || typeof params !== 'object') return []
  return isSteppedResponseKind(kind) ? steppedSettingsRows(params) : chirpSettingsRows(params)
}

/** The Measurements panel, as sections: the corner table, then the peak and the reference, then the notes that apply. */
export function frequencyResponseMeasurementSections(traces: ResponseTrace[], view: ResponseView, showLeft: boolean): ReportSection[] {
  const trace = primaryResponseTrace(traces, view, showLeft)
  if (!trace) return [{ kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' }]

  const sections: ReportSection[] = [
    {
      kind: 'table',
      title: 'Measurements',
      headers: ['', 'f(low)', 'f(high)', 'Bandwidth'],
      cells: [
        [
          '±1 dB',
          formatCorner(trace.oneDb.lowerHz, trace.oneDb.lowerClamped, 'lower'),
          formatCorner(trace.oneDb.upperHz, trace.oneDb.upperClamped, 'upper'),
          formatBandwidth(trace.oneDb),
        ],
        [
          '−3 dB',
          formatCorner(trace.threeDb.lowerHz, trace.threeDb.lowerClamped, 'lower'),
          formatCorner(trace.threeDb.upperHz, trace.threeDb.upperClamped, 'upper'),
          formatBandwidth(trace.threeDb),
        ],
      ],
    },
  ]

  const rows: ReportRow[] = [
    { label: peakLabel(view), value: `${formatDb(trace.peakDb)} (${formatRatio(trace.peakDb)})` },
    { label: 'Peak at', value: formatHz(trace.peakFreqHz) },
  ]
  const reference = referenceLabel(trace)
  if (reference) {
    rows.push({
      label: 'Reference',
      value: trace.referenceDb === null ? reference : `${formatDb(trace.referenceDb)} — ${reference}`,
    })
  }
  rows.push({ label: 'Curve', value: trace.kind === 'Gain' ? 'Gain (left relative to the reference)' : `${trace.kind} input` })
  sections.push({ kind: 'keyValues', rows })

  const notes: string[] = []
  if (trace.referenceFellBackToPeak) notes.push(REFERENCE_FELL_BACK_NOTE)
  if (trace.threeDb.lowerClamped || trace.threeDb.upperClamped || trace.oneDb.lowerClamped || trace.oneDb.upperClamped) {
    notes.push(CLAMPED_CORNER_NOTE)
  }
  if (notes.length > 0) sections.push({ kind: 'paragraph', text: notes.join('\n') })

  return sections
}

/**
 * Every section of a frequency-response chapter except the chart, in report order, for the chosen
 * elements. The orchestrator puts the chart image first when `chart` is chosen.
 */
export function frequencyResponseSections(subject: ReportSubject, elements: readonly ReportElementId[]): ReportSection[] {
  const sections: ReportSection[] = []

  if (elements.includes('description')) sections.push(...descriptionSections(subject))

  if (elements.includes('settings')) {
    const rows = frequencyResponseSettingsRows(subject.kind, subject.paramsJson)
    sections.push(rows.length > 0 ? { kind: 'keyValues', title: 'Settings', rows } : genericSettingsSection(subject.paramsJson))
  }

  if (elements.includes('measurements')) {
    if (subject.data.kind === 'response') {
      sections.push(...frequencyResponseMeasurementSections(subject.data.traces, subject.data.display.view, subject.data.display.showLeft))
    } else {
      sections.push({ kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' })
    }
  }

  return sections
}
