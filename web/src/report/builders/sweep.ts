import type {
  ImdVsAmplitudeParameters,
  ReportFigure,
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
import { bandwidthLabel } from '../../weighting'

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
    case 'MilliWatt':
      return `${formatNumber(value)} mW`
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
  measurementLoFreqHz?: number | null
  measurementHiFreqHz?: number | null
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
  // Only when the row records it: a sweep saved before the band was one of its own settings ran on
  // whatever the live pipeline held, and printing the default there would assert something the row
  // does not say.
  if (typeof params.measurementLoFreqHz === 'number' && typeof params.measurementHiFreqHz === 'number') {
    rows.push({ label: 'Measurement band', value: bandwidthLabel(params.measurementLoFreqHz, params.measurementHiFreqHz) })
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
/**
 * The Settings rows of a sweep that was *imported* from a `.step`ped transient run, or null when it
 * was measured.
 *
 * <p>Tried before everything below, because almost none of those rows exist here: there was no
 * generator, so no level mode and no drive level; no load, so no power; no averaging and no noise
 * pass. Printing them would describe a bench run that never happened — the same arm the response
 * and impedance chapters have.</p>
 */
function simulatedSweepRows(paramsJson: string): ReportRow[] | null {
  const params = parse<Record<string, unknown>>(paramsJson)
  const simulation = params?.['simulation'] as Record<string, unknown> | null | undefined
  const source = simulation?.['source'] as Record<string, unknown> | null | undefined
  if (!params || !simulation || !source) return null

  const str = (value: unknown): string | null => (typeof value === 'string' && value ? value : null)
  const num = (value: unknown): number | null =>
    typeof value === 'number' && Number.isFinite(value) ? value : null

  const rows: ReportRow[] = [{ label: 'Source', value: 'Simulated (stepped .tran run)' }]
  const path = str(source['path'])
  if (path) rows.push({ label: 'File', value: path })

  const trace = str(source['leftTrace'])
  if (trace) rows.push({ label: 'Trace', value: trace })

  const steps = num(source['sweepCount'])
  if (steps !== null) rows.push({ label: 'Steps', value: `${steps} runs, one point each` })

  const lowHz = num(params['startFreqHz'])
  const highHz = num(params['endFreqHz'])
  if (lowHz !== null && highHz !== null) {
    rows.push({ label: 'Sweep', value: `${formatHz(lowHz)} – ${formatHz(highHz)}` })
  }

  const fftSize = num(params['fftSize'])
  if (fftSize !== null) rows.push({ label: 'FFT size', value: fftSize.toLocaleString() })

  const window = str(params['window'])
  if (window) rows.push({ label: 'Window', value: window })

  const rateHz = num(params['sampleRateHz'])
  if (rateHz !== null) rows.push({ label: 'Analysed at', value: `${(rateHz / 1e3).toFixed(1)} kHz` })

  const startS = num(simulation['startTimeS'])
  if (startS !== null) rows.push({ label: 'Each run analysed from', value: `${(startS * 1e3).toFixed(3)} ms` })

  return rows
}

export function sweepSettingsRows(kind: string, paramsJson: string): ReportRow[] {
  const simulated = simulatedSweepRows(paramsJson)
  if (simulated) return simulated

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
      { kind: 'figures', title: `Measurements — ${label.toLowerCase()} channel`, figures: summaryFigures(points, data.method, data.family, xAxis, unit) },
      {
        kind: 'table',
        headers: sweepMetricColumns(data.method, data.family, xAxis),
        cells: points.map((p) => sweepMetricRow(p, data.method, xAxis, unit, loadOhms, data.family)),
      },
    )
  }

  return sections.length > 0 ? sections : [{ kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' }]
}

/**
 * The four cards over a channel's table: how many points, the worst and best reading with where each
 * fell, and what was swept. The same figures `sweepSummary` already produced as rows — the value and
 * the "at 50.0 Hz" are split here because a card sets the number large and its qualifier under it.
 */
function summaryFigures(points: ThdSweepPoint[], method: SweepReportData['method'], family: SweepTraceFamily, xAxis: SweepXAxis, unit: SweepUnit): ReportFigure[] {
  const figures: ReportFigure[] = [{ label: 'Points', value: String(points.length) }]
  const summary = sweepSummary(points, method, xAxis)
  if (summary) {
    const total = sweepTraceLabel('thd', method, family)
    figures.push(
      { label: `Worst ${total}`, value: formatDistortion(summary.worst.percent, unit, summary.worst.gainDb), sub: `at ${formatSweepX(summary.worst.x, xAxis)}` },
      { label: `Best ${total}`, value: formatDistortion(summary.best.percent, unit, summary.best.gainDb), sub: `at ${formatSweepX(summary.best.x, xAxis)}` },
    )
  }
  const first = points[0]
  const last = points[points.length - 1]
  if (first && last) {
    figures.push({
      label: 'Swept',
      value: `${formatSweepX(sweepXValue(first, xAxis), xAxis)} – ${formatSweepX(sweepXValue(last, xAxis), xAxis)}`,
    })
  }
  return figures
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
