import type {
  ImdVsAmplitudeParameters,
  ReportRow,
  ReportSection,
  SweepLevelMode,
  SweepLevelUnit,
  ThdSweepPoint,
  ThdVsAmplitudeParameters,
  ThdVsFrequencyParameters,
} from '../../api/client'
import { IMD_PRESETS } from '../../imd/presets'
import { isAmplitudeSweepKind, isImdSweepKind } from '../../measurements/kinds'
import { formatHz } from '../../response/format'
import { sweepChartWindow } from '../../sweep/display'
import { formatDistortion, sweepMetricColumns, sweepMetricRow, sweepSummary, type SweepUnit } from '../../sweep/readings'
import { sweepTraceLabel, type SweepTraceFamily } from '../../sweep/points'
import { formatSweepX, sweepXValue, type SweepXAxis } from '../../sweep/xAxis'
import type { ReportElementId } from '../elements'
import type { ReportSubject, ReportSubjectData } from '../types'
import { descriptionSections, genericSettingsSection } from './generic'

/**
 * The sweep chapter — THD vs frequency, THD vs amplitude and IMD vs amplitude, which share one
 * chart, one point shape and one metrics table and so share one builder. The chart is a PNG the
 * orchestrator renders; the settings rows are labelled the way the three config panels label them;
 * and the readings are the chart's own metrics table for every point, from sweep/readings.ts — the
 * same functions ThdSweepChart tabulates its cursor row with, so the paper and the screen quote
 * the same numbers.
 */

const LEVEL_MODE_LABELS: Record<SweepLevelMode, string> = {
  GeneratorVoltage: 'Generator voltage',
  OutputVoltage: 'DUT output voltage',
  OutputPower: 'DUT output power',
}

const THD_METHOD_LABELS: Record<string, string> = {
  Conventional: 'Conventional',
  FrequencyNormalized: 'Frequency normalized',
}

const IMD_METHOD_LABELS: Record<string, string> = {
  Smpte: 'SMPTE / DIN (sidebands re carrier)',
  Ccif: 'CCIF / ITU-R (difference tones)',
}

const CHANNEL_LABELS = ['Left', 'Right']

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

function formatVolt(value: number): string {
  return value < 1 ? `${formatNumber(value * 1000)} mV` : `${formatNumber(value)} V`
}

function parse<T>(paramsJson: string): Partial<T> | null {
  try {
    const params = JSON.parse(paramsJson) as Partial<T>
    return params && typeof params === 'object' ? params : null
  } catch {
    return null
  }
}

/** The rows every sweep kind shares, in the config panels' order: channels, resolution, averages, noise floor. */
function commonRows(params: {
  processLeft?: boolean
  processRight?: boolean
  adaptiveResolution?: boolean
  fftSize?: number
  window?: string
  sampleRateHz?: number
  averageCount?: number
  measureNoiseFloor?: boolean
}): ReportRow[] {
  const rows: ReportRow[] = []
  if (typeof params.processLeft === 'boolean' || typeof params.processRight === 'boolean') {
    const channels = [params.processLeft ? 'Left' : null, params.processRight ? 'Right' : null].filter(Boolean)
    rows.push({ label: 'Channels', value: channels.length > 0 ? channels.join(' and ') : 'Neither' })
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
  if (typeof params.averageCount === 'number') rows.push({ label: 'Averages per point', value: String(params.averageCount) })
  if (typeof params.measureNoiseFloor === 'boolean') {
    rows.push({ label: 'Noise floor', value: params.measureNoiseFloor ? 'Measured first, generator off' : 'Not measured' })
  }
  return rows
}

function captureDelayRow(params: { captureDelayMs?: number | null }): ReportRow | null {
  if (typeof params.captureDelayMs !== 'number' || params.captureDelayMs <= 0) return null
  return { label: 'Capture delay', value: `${formatNumber(params.captureDelayMs)} ms` }
}

function push(rows: ReportRow[], row: ReportRow | null) {
  if (row) rows.push(row)
}

function thdVsFrequencyRows(params: Partial<ThdVsFrequencyParameters & { captureDelayMs?: number | null }>): ReportRow[] {
  const rows: ReportRow[] = []
  if (typeof params.startFreqHz === 'number' && typeof params.endFreqHz === 'number') {
    rows.push({ label: 'Sweep', value: `${formatHz(params.startFreqHz)} – ${formatHz(params.endFreqHz)}` })
  }
  if (typeof params.stepsPerOctave === 'number') rows.push({ label: 'Steps per octave', value: String(params.stepsPerOctave) })
  if (params.levelMode !== undefined && params.levelValue !== undefined && params.levelUnit !== undefined) {
    let value = `${formatLevel(params.levelValue, params.levelUnit)} — ${LEVEL_MODE_LABELS[params.levelMode]}`
    if (params.levelMode === 'OutputPower' && typeof params.loadOhms === 'number') value += ` into ${formatNumber(params.loadOhms)} Ω`
    rows.push({ label: 'Level', value })
  }
  if (typeof params.loadOhms === 'number' && params.levelMode !== 'OutputPower') rows.push({ label: 'Load', value: `${formatNumber(params.loadOhms)} Ω` })
  rows.push(...commonRows(params))
  if (typeof params.thdMethod === 'string') rows.push({ label: 'THD method', value: THD_METHOD_LABELS[params.thdMethod] ?? params.thdMethod })
  // Probed for the output-referred modes, so only known after a run; 0 on a type never run.
  if (typeof params.generatorAmpDbv === 'number' && params.levelMode !== 'GeneratorVoltage' && params.generatorAmpDbv !== 0) {
    rows.push({ label: 'Generator level', value: `${params.generatorAmpDbv.toFixed(2)} dBV` })
  }
  push(rows, captureDelayRow(params))
  return rows
}

function thdVsAmplitudeRows(params: Partial<ThdVsAmplitudeParameters & { captureDelayMs?: number | null }>): ReportRow[] {
  const rows: ReportRow[] = []
  if (typeof params.fixedFreqHz === 'number') rows.push({ label: 'Frequency', value: formatHz(params.fixedFreqHz) })
  if (typeof params.startAmplitudeVolt === 'number' && typeof params.endAmplitudeVolt === 'number') {
    rows.push({ label: 'Amplitude', value: `${formatVolt(params.startAmplitudeVolt)} – ${formatVolt(params.endAmplitudeVolt)}` })
  }
  if (typeof params.stepsPerOctave === 'number') rows.push({ label: 'Steps per octave', value: String(params.stepsPerOctave) })
  if (typeof params.loadOhms === 'number') rows.push({ label: 'Load', value: `${formatNumber(params.loadOhms)} Ω` })
  if (typeof params.stopThdPercent === 'number') {
    rows.push({ label: 'Stop when THD exceeds', value: `${formatNumber(params.stopThdPercent)} %` })
  }
  rows.push(...commonRows(params))
  push(rows, captureDelayRow(params))
  return rows
}

function imdVsAmplitudeRows(params: Partial<ImdVsAmplitudeParameters>): ReportRow[] {
  const rows: ReportRow[] = []
  if (typeof params.preset === 'string') {
    rows.push({ label: 'IMD preset', value: IMD_PRESETS.find((p) => p.value === params.preset)?.label ?? params.preset })
  }
  if (typeof params.toneLowHz === 'number' && typeof params.toneHighHz === 'number') {
    const ratio = typeof params.lowToHighRatio === 'number' ? `, ${formatNumber(params.lowToHighRatio)}:1` : ''
    rows.push({ label: 'Tones', value: `${formatHz(params.toneLowHz)} + ${formatHz(params.toneHighHz)}${ratio}` })
  }
  if (typeof params.method === 'string') rows.push({ label: 'Method', value: IMD_METHOD_LABELS[params.method] ?? params.method })
  if (typeof params.maxOrder === 'number') rows.push({ label: 'Max order', value: String(params.maxOrder) })
  if (typeof params.startLevelVolt === 'number' && typeof params.endLevelVolt === 'number') {
    rows.push({ label: 'Level', value: `${formatVolt(params.startLevelVolt)} – ${formatVolt(params.endLevelVolt)} sine-equivalent` })
  }
  if (typeof params.stepsPerOctave === 'number') rows.push({ label: 'Steps per octave', value: String(params.stepsPerOctave) })
  if (typeof params.loadOhms === 'number') rows.push({ label: 'Load', value: `${formatNumber(params.loadOhms)} Ω` })
  rows.push(...commonRows(params))
  if (typeof params.maxProductFreqHz === 'number') rows.push({ label: 'Product band limit', value: formatHz(params.maxProductFreqHz) })
  push(rows, captureDelayRow(params))
  return rows
}

/** The Settings section's rows for any of the three sweep kinds, labelled the way its config panel labels them. */
export function sweepSettingsRows(kind: string, paramsJson: string): ReportRow[] {
  if (isImdSweepKind(kind)) {
    const params = parse<ImdVsAmplitudeParameters>(paramsJson)
    return params ? imdVsAmplitudeRows(params) : []
  }
  if (isAmplitudeSweepKind(kind)) {
    const params = parse<ThdVsAmplitudeParameters>(paramsJson)
    return params ? thdVsAmplitudeRows(params) : []
  }
  const params = parse<ThdVsFrequencyParameters>(paramsJson)
  return params ? thdVsFrequencyRows(params) : []
}

/** The load a sweep's Power figures were computed against, off its as-run `params_json`; 8 Ω when it does not say. */
export function sweepLoadOhmsFromParams(paramsJson: string): number {
  const params = parse<{ loadOhms?: unknown }>(paramsJson)
  return typeof params?.loadOhms === 'number' && params.loadOhms > 0 ? params.loadOhms : 8
}

/** What `sweepMeasurementSections` reads: the sweep's points and how the chart shows them. */
export type SweepReportData = Extract<ReportSubjectData, { kind: 'sweep' }>

/**
 * The Measurements element: per channel measured, the worst and best total distortion and where
 * each sits, then every point's metrics in the chart's own columns — one table per channel, since
 * the two channels of an output-referred amplitude sweep sit on different x values and share no row.
 */
export function sweepMeasurementSections(data: SweepReportData, kind: string, loadOhms: number): ReportSection[] {
  const { xAxis } = sweepChartWindow(data.display, kind)
  const unit: SweepUnit = data.display.unit
  const sections: ReportSection[] = []

  for (const channel of [0, 1]) {
    const points = data.points.filter((p) => p.channel === channel).sort((a, b) => sweepXValue(a, xAxis) - sweepXValue(b, xAxis))
    if (points.length === 0) continue
    const label = CHANNEL_LABELS[channel]
    sections.push(
      { kind: 'keyValues', title: `Measurements — ${label.toLowerCase()} channel`, rows: summaryRows(points, data.method, data.family, xAxis, unit) },
      {
        kind: 'table',
        headers: sweepMetricColumns(data.method, data.family, xAxis),
        cells: points.map((p) => sweepMetricRow(p, data.method, xAxis, unit, loadOhms)),
      },
    )
  }

  return sections.length > 0 ? sections : [{ kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' }]
}

function summaryRows(points: ThdSweepPoint[], method: SweepReportData['method'], family: SweepTraceFamily, xAxis: SweepXAxis, unit: SweepUnit): ReportRow[] {
  const summary = sweepSummary(points, method, xAxis)
  const rows: ReportRow[] = [{ label: 'Points', value: String(points.length) }]
  if (!summary) return rows
  const total = sweepTraceLabel('thd', method, family)
  rows.push(
    { label: `Worst ${total}`, value: `${formatDistortion(summary.worst.percent, unit)} at ${formatSweepX(summary.worst.x, xAxis)}` },
    { label: `Best ${total}`, value: `${formatDistortion(summary.best.percent, unit)} at ${formatSweepX(summary.best.x, xAxis)}` },
  )
  return rows
}

/** Every section of a sweep chapter except the chart, in report order, for the chosen elements. */
export function sweepSections(subject: ReportSubject, elements: readonly ReportElementId[]): ReportSection[] {
  const sections: ReportSection[] = []

  if (elements.includes('description')) sections.push(...descriptionSections(subject))

  if (elements.includes('settings')) {
    const rows = sweepSettingsRows(subject.kind, subject.paramsJson)
    sections.push(rows.length > 0 ? { kind: 'keyValues', title: 'Settings', rows } : genericSettingsSection(subject.paramsJson))
  }

  if (elements.includes('measurements')) {
    sections.push(
      ...(subject.data.kind === 'sweep'
        ? sweepMeasurementSections(subject.data, subject.kind, sweepLoadOhmsFromParams(subject.paramsJson))
        : [{ kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' } satisfies ReportSection]),
    )
  }

  return sections
}
