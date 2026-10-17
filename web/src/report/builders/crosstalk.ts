import type { CrosstalkParameters, ReportRow, ReportSection, SweepLevelMode, SweepLevelUnit } from '../../api/client'
import type { ResponseTrace } from '../../composables/useFrequencyResponse'
import { crosstalkDirectionLabel, crosstalkReadings } from '../../crosstalk/readings'
import { formatHz } from '../../response/format'
import type { ReportElementId } from '../elements'
import type { ReportSubject } from '../types'
import { descriptionSections, genericSettingsSection } from './generic'

/**
 * The crosstalk chapter. The chart is a PNG the orchestrator renders (the response chart in its
 * Crosstalk view); the settings rows are labelled the way the config panel labels them, and the
 * readings come from crosstalk/readings.ts — the same helper the Measurements panel reads — so the
 * paper and the screen quote the same numbers.
 */

const LEVEL_MODE_LABELS: Record<SweepLevelMode, string> = {
  GeneratorVoltage: 'Input voltage (generator)',
  OutputVoltage: 'DUT output voltage (driven channel)',
  OutputPower: 'DUT output power (driven channel)',
}

const DIRECTION_LABELS: Record<string, string> = {
  LeftToRight: 'Left → Right (left driven, right read)',
  RightToLeft: 'Right → Left (right driven, left read)',
  Both: 'Both, one pass each way',
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
    case 'MilliWatt':
      return `${formatNumber(value)} mW`
  }
}

/** The Settings section's rows for a crosstalk run. */
export function crosstalkSettingsRows(paramsJson: string): ReportRow[] {
  let params: Partial<CrosstalkParameters>
  try {
    params = JSON.parse(paramsJson) as Partial<CrosstalkParameters>
  } catch {
    return []
  }
  if (!params || typeof params !== 'object') return []

  const rows: ReportRow[] = []
  if (typeof params.startFreqHz === 'number' && typeof params.endFreqHz === 'number') {
    rows.push({ label: 'Sweep', value: `${formatHz(params.startFreqHz)} – ${formatHz(params.endFreqHz)}` })
  }
  if (typeof params.stepsPerOctave === 'number') rows.push({ label: 'Steps per octave', value: String(params.stepsPerOctave) })
  if (typeof params.direction === 'string') rows.push({ label: 'Direction', value: DIRECTION_LABELS[params.direction] ?? params.direction })
  if (params.levelMode !== undefined && params.levelValue !== undefined && params.levelUnit !== undefined) {
    let value = `${formatLevel(params.levelValue, params.levelUnit)} — ${LEVEL_MODE_LABELS[params.levelMode]}`
    if (params.levelMode === 'OutputPower' && typeof params.loadOhms === 'number') value += ` into ${formatNumber(params.loadOhms)} Ω`
    rows.push({ label: 'Level', value })
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
  // One generator level per direction run: the output-referred modes solve each pass's own.
  if (typeof params.generatorAmpDbvLeftToRight === 'number') {
    rows.push({ label: 'Generator level L→R', value: `${params.generatorAmpDbvLeftToRight.toFixed(2)} dBV` })
  }
  if (typeof params.generatorAmpDbvRightToLeft === 'number') {
    rows.push({ label: 'Generator level R→L', value: `${params.generatorAmpDbvRightToLeft.toFixed(2)} dBV` })
  }
  if (typeof params.captureDelayMs === 'number' && params.captureDelayMs > 0) {
    rows.push({ label: 'Capture delay', value: `${formatNumber(params.captureDelayMs)} ms` })
  }
  return rows
}

/** The Measurements panel as one table: a row per direction, the figures the panel quotes. */
export function crosstalkMeasurementSections(traces: ResponseTrace[]): ReportSection[] {
  const rows = traces
    .filter((t) => t.kind === 'CrosstalkLeftToRight' || t.kind === 'CrosstalkRightToLeft')
    .map((t) => ({ label: crosstalkDirectionLabel(t.kind), readings: crosstalkReadings(t) }))
    .filter((r): r is { label: string; readings: NonNullable<ReturnType<typeof crosstalkReadings>> } => r.readings !== null)
  if (rows.length === 0) return [{ kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' }]

  const db = (v: number) => `${v.toFixed(1)} dB`
  return [
    {
      kind: 'table',
      title: 'Measurements',
      headers: ['Direction', 'At 1 kHz', 'Worst', 'Low end', 'High end'],
      cells: rows.map((r) => [
        r.label,
        `${db(r.readings.at.db)} (${formatHz(r.readings.at.freqHz)})`,
        `${db(r.readings.worst.db)} at ${formatHz(r.readings.worst.freqHz)}`,
        `${db(r.readings.low.db)} at ${formatHz(r.readings.low.freqHz)}`,
        `${db(r.readings.high.db)} at ${formatHz(r.readings.high.freqHz)}`,
      ]),
    },
  ]
}

/** Every section of a crosstalk chapter except the chart, in report order, for the chosen elements. */
export function crosstalkSections(subject: ReportSubject, elements: readonly ReportElementId[]): ReportSection[] {
  const sections: ReportSection[] = []

  if (elements.includes('description')) sections.push(...descriptionSections(subject))

  if (elements.includes('settings')) {
    const rows = crosstalkSettingsRows(subject.paramsJson)
    sections.push(rows.length > 0 ? { kind: 'keyValues', title: 'Settings', rows } : genericSettingsSection(subject.paramsJson))
  }

  if (elements.includes('measurements')) {
    sections.push(...crosstalkMeasurementSections(subject.data.kind === 'crosstalk' ? subject.data.traces : []))
  }

  return sections
}
