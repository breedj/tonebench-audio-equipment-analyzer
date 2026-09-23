import {
  IMPULSE_GATE_WINDOWS,
  IMPULSE_REFERENCE_MODES,
  RESPONSE_RESOLUTIONS,
  RESPONSE_SMOOTHING,
  type HarmonicCurveJson,
  type ImpulseResponseParameters,
  type ImpulseTraceJson,
  type ReportRow,
  type ReportSection,
  type SweepLevelMode,
  type SweepLevelUnit,
} from '../../api/client'
import { harmonicRows, impulseReadingRows, type ImpulseRunFacts } from '../../measurements/impulseReadings'
import { formatHz } from '../../response/format'
import type { ReportElementId } from '../elements'
import type { ReportSubject } from '../types'
import { descriptionSections, genericSettingsSection } from './generic'

/**
 * The impulse-response chapter. The charts are PNGs the orchestrator renders (the impulse, the
 * gated response, the harmonics and both decay surfaces); the settings rows are labelled the way
 * the config panel labels them, and the readings come from measurements/impulseReadings.ts — the
 * helper the Display panel's Measurement block reads — so the paper and the screen quote the same
 * numbers.
 *
 * The gated response's ±1 dB / −3 dB corners are deliberately **not** printed, although the stored
 * traces carry them: the impulse screen hides them too, because a gated response says nothing below
 * its own resolution limit and a corner quoted from there would be the window's, not the DUT's.
 */

const LEVEL_MODE_LABELS: Record<SweepLevelMode, string> = {
  GeneratorVoltage: 'Input voltage',
  OutputVoltage: 'Output voltage',
  OutputPower: 'Output power',
}

function formatNumber(value: number): string {
  if (!Number.isFinite(value)) return '—'
  return Number.isInteger(value) ? String(value) : String(Number(value.toFixed(3)))
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

/** The Settings section's rows for an impulse-response run. */
export function impulseResponseSettingsRows(paramsJson: string): ReportRow[] {
  let params: Partial<ImpulseResponseParameters & { captureDelayMs?: number | null }>
  try {
    params = JSON.parse(paramsJson) as Partial<ImpulseResponseParameters & { captureDelayMs?: number | null }>
  } catch {
    return []
  }
  if (!params || typeof params !== 'object') return []

  const rows: ReportRow[] = []
  if (typeof params.frequencySpanHz === 'number') {
    const start = typeof params.chirpStartHz === 'number' ? formatHz(params.chirpStartHz) : null
    rows.push({ label: 'Sweep', value: start ? `${start} – ${formatHz(params.frequencySpanHz)}` : `to ${formatHz(params.frequencySpanHz)}` })
  }
  if (typeof params.resolutionMultiplier === 'number') {
    const option = RESPONSE_RESOLUTIONS.find((r) => r.multiplier === params.resolutionMultiplier)
    rows.push({ label: 'Resolution', value: option ? `${option.label} Hz` : `×${params.resolutionMultiplier}` })
  }
  if (params.levelMode !== undefined && params.levelValue !== undefined && params.levelUnit !== undefined) {
    let value = `${formatLevel(params.levelValue, params.levelUnit)} — ${LEVEL_MODE_LABELS[params.levelMode]}`
    if (params.levelMode === 'OutputPower' && typeof params.loadOhms === 'number') value += ` into ${formatNumber(params.loadOhms)} Ω`
    rows.push({ label: 'Level', value })
  }
  if (typeof params.averageCount === 'number') rows.push({ label: 'Averages', value: String(params.averageCount) })
  if (typeof params.gatePreMs === 'number' && typeof params.gatePostMs === 'number') {
    const window = params.gateWindow ? IMPULSE_GATE_WINDOWS.find((w) => w.value === params.gateWindow)?.label ?? params.gateWindow : null
    const resolvedAbove = params.gatePreMs + params.gatePostMs > 0 ? `, resolves ${(1000 / (params.gatePreMs + params.gatePostMs)).toFixed(0)} Hz` : ''
    rows.push({ label: 'Gate', value: `−${formatNumber(params.gatePreMs)} / +${formatNumber(params.gatePostMs)} ms${window ? `, ${window}` : ''}${resolvedAbove}` })
  }
  if (typeof params.smoothingDenominator === 'number') {
    const option = RESPONSE_SMOOTHING.find((s) => s.denominator === params.smoothingDenominator)
    rows.push({
      label: 'Smoothing',
      value: option ? option.label : params.smoothingDenominator > 0 ? `1/${params.smoothingDenominator} octave` : 'None',
    })
  }
  if (params.referenceMode) {
    rows.push({ label: 'Reference', value: IMPULSE_REFERENCE_MODES.find((m) => m.value === params.referenceMode)?.label ?? params.referenceMode })
  }
  if (typeof params.extractHarmonics === 'boolean') {
    rows.push({
      label: 'Harmonics',
      value: params.extractHarmonics ? `Extracted up to H${params.maxHarmonicOrder ?? '?'}` : 'Not extracted',
    })
  }
  if (typeof params.generatorAmpDbv === 'number') rows.push({ label: 'Generator level', value: `${params.generatorAmpDbv.toFixed(2)} dBV` })
  if (typeof params.captureDelayMs === 'number' && params.captureDelayMs > 0) {
    rows.push({ label: 'Capture delay', value: `${formatNumber(params.captureDelayMs)} ms` })
  }
  return rows
}

/** The Measurement block as a section, then the harmonic orders' levels when any were extracted. */
export function impulseResponseMeasurementSections(
  impulse: ImpulseTraceJson | null,
  run: ImpulseRunFacts,
  harmonics: readonly HarmonicCurveJson[],
): ReportSection[] {
  if (!impulse) return [{ kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' }]

  const sections: ReportSection[] = [{ kind: 'keyValues', title: 'Measurements', rows: impulseReadingRows(impulse, run) }]

  if (harmonics.length > 0) {
    const { headers, cells } = harmonicRows(harmonics)
    sections.push(
      { kind: 'table', title: 'Harmonic distortion', headers, cells },
      {
        kind: 'paragraph',
        text: 'dBc relative to the fundamental at each frequency; a dash where the order’s curve does not reach it.',
      },
    )
  }

  return sections
}

/** Every section of an impulse-response chapter except the charts, in report order, for the chosen elements. */
export function impulseResponseSections(subject: ReportSubject, elements: readonly ReportElementId[]): ReportSection[] {
  const sections: ReportSection[] = []

  if (elements.includes('description')) sections.push(...descriptionSections(subject))

  if (elements.includes('settings')) {
    const rows = impulseResponseSettingsRows(subject.paramsJson)
    sections.push(rows.length > 0 ? { kind: 'keyValues', title: 'Settings', rows } : genericSettingsSection(subject.paramsJson))
  }

  if (elements.includes('measurements')) {
    const data = subject.data
    sections.push(
      ...(data.kind === 'impulse'
        ? impulseResponseMeasurementSections(data.impulse, data.run, data.harmonics)
        : impulseResponseMeasurementSections(null, EMPTY_RUN, [])),
    )
  }

  return sections
}

const EMPTY_RUN: ImpulseRunFacts = {
  referenceMode: 'None',
  referenceName: null,
  latencySamples: null,
  averagesTaken: null,
  averageCount: 0,
  generatorAmpDbv: null,
}
