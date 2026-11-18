import type { OscilloscopeParameters, ReportRow, ReportSection, ScopeRecordResponse } from '../../api/client'
import { mathReadings, scopeChannelReadings } from '../../scope/readings'
import { mathAxisLabel, type ScopeMathOperation } from '../../scope/math'
import { xyReadings, xyTruncationNote } from '../../scope/xy'
import { formatScopeTime, toChartMs } from '../../scope/timeAxis'
import type { ReportElementId } from '../elements'
import type { ReportSubject } from '../types'
import { descriptionSections, genericSettingsSection } from './generic'

/**
 * The oscilloscope chapter. The chart is a PNG the orchestrator renders; the settings rows are
 * labelled the way the two config sections label them, and the readings are the Measurements
 * panel's own — <code>sliceExtremes</code> and the slice's server-measured mean — so the paper and
 * the screen quote the same numbers.
 */

const MODE_LABELS: Record<string, string> = {
  Auto: 'Auto — takes a record anyway if nothing crosses',
  Normal: 'Normal — waits for a crossing',
  Single: 'Single — one record, then hold',
  Roll: 'Roll — no trigger',
}

const SOURCE_LABELS: Record<string, string> = { ChannelA: 'Left', ChannelB: 'Right' }
const SLOPE_LABELS: Record<string, string> = { Rising: 'Rising', Falling: 'Falling', Either: 'Either' }

function formatNumber(value: number): string {
  if (!Number.isFinite(value)) return '—'
  return Number.isInteger(value) ? String(value) : String(Number(value.toFixed(4)))
}

/** A time in seconds as the screen writes it — milliseconds or microseconds, by its own size. */
function time(seconds: number): string {
  const ms = toChartMs(seconds)
  return formatScopeTime(ms, Math.abs(ms) || 1)
}

/** The Settings section's rows for an oscilloscope run. */
export function scopeSettingsRows(paramsJson: string): ReportRow[] {
  let params: Partial<OscilloscopeParameters>
  try {
    params = JSON.parse(paramsJson) as Partial<OscilloscopeParameters>
  } catch {
    return []
  }
  if (!params || typeof params !== 'object') return []

  const rows: ReportRow[] = []
  if (typeof params.sampleRateHz === 'number') rows.push({ label: 'Sample rate', value: `${formatNumber(params.sampleRateHz / 1000)} kS/s` })
  if (typeof params.recordSeconds === 'number') {
    const samples = typeof params.sampleRateHz === 'number' ? Math.round(params.recordSeconds * params.sampleRateHz) : null
    rows.push({
      label: 'Record',
      value: samples === null ? time(params.recordSeconds) : `${time(params.recordSeconds)} (${samples.toLocaleString()} samples)`,
    })
  }
  if (typeof params.preTriggerFraction === 'number') {
    rows.push({ label: 'Pre-trigger', value: `${formatNumber(params.preTriggerFraction * 100)} %` })
  }
  if (typeof params.processLeft === 'boolean' || typeof params.processRight === 'boolean') {
    const captured = [params.processLeft ? 'Left' : null, params.processRight ? 'Right' : null].filter(Boolean)
    rows.push({ label: 'Channels', value: captured.length > 0 ? captured.join(' and ') : 'None' })
  }
  if (typeof params.mode === 'string') rows.push({ label: 'Mode', value: MODE_LABELS[params.mode] ?? params.mode })
  if (typeof params.triggerSource === 'string' || typeof params.triggerSlope === 'string') {
    const source = SOURCE_LABELS[params.triggerSource ?? ''] ?? params.triggerSource ?? '—'
    const slope = SLOPE_LABELS[params.triggerSlope ?? ''] ?? params.triggerSlope ?? '—'
    rows.push({ label: 'Trigger', value: `${slope} edge on ${source}` })
  }
  if (typeof params.triggerLevelV === 'number') {
    const level = `${formatNumber(params.triggerLevelV)} V`
    rows.push({
      label: 'Level',
      value:
        typeof params.triggerHysteresisV === 'number' && params.triggerHysteresisV > 0
          ? `${level} ± ${formatNumber(params.triggerHysteresisV)} V hysteresis`
          : level,
    })
  }
  if (typeof params.holdoffSeconds === 'number' && params.holdoffSeconds > 0) {
    rows.push({ label: 'Holdoff', value: time(params.holdoffSeconds) })
  }
  if (typeof params.captureDelayMs === 'number' && params.captureDelayMs > 0) {
    rows.push({ label: 'Capture delay', value: `${formatNumber(params.captureDelayMs)} ms` })
  }
  return rows
}

/**
 * The Measurements panel as one table, plus the record's own facts.
 *
 * <p>A <b>row per reading and a column per channel</b>, which is the shape that reads on paper:
 * the figures a reader compares are the two channels' own, side by side. The rows are
 * <code>scope/readings.ts</code>'s — the same function the panel prints from, so the paper and the
 * screen cannot quote different numbers or call them different things — and a reading no channel
 * has is not a row at all. A channel the display rail is hiding is left out, as it is on
 * screen.</p>
 */
export function scopeMeasurementSections(
  record: ScopeRecordResponse | null,
  voltsPerDivision: number,
  showLeft: boolean,
  showRight: boolean,
): ReportSection[] {
  if (!record || record.channels.length === 0) {
    return [{ kind: 'paragraph', title: 'Measurements', text: 'Nothing captured.' }]
  }

  const blocks = scopeChannelReadings(record.channels, voltsPerDivision, showLeft, showRight)

  const sections: ReportSection[] = []
  if (blocks.length === 0) {
    sections.push({ kind: 'paragraph', title: 'Measurements', text: 'No channel is drawn.' })
  } else {
    // The union of the labels, in the order the first channel that has them puts them: the two
    // channels can legitimately differ about which readings exist -- one may hold two settled
    // levels where the other holds noise -- and a row missing from one of them is a blank cell
    // rather than a missing row.
    const labels: string[] = []
    for (const block of blocks) {
      for (const reading of block.readings) {
        if (!labels.includes(reading.label)) labels.push(reading.label)
      }
    }

    sections.push({
      kind: 'table',
      title: 'Measurements',
      headers: ['Reading', ...blocks.map((b) => b.label)],
      cells: labels.map((label) => [
        label,
        ...blocks.map((b) => b.readings.find((r) => r.label === label)?.value ?? '—'),
      ]),
    })
  }

  // The math channel is a table of its own rather than a column beside the channels', because its
  // numbers are frequently not in the same unit: A × B is V² and a derivative V/s, so a shared
  // "Reading" column would put three units in one row. Its heading carries the expression and the
  // unit, which is where a reader of the paper finds that out.
  const math = record.math
  if (math) {
    const readings = mathReadings(math)
    if (readings.length > 0) {
      sections.push({
        kind: 'table',
        title: `Math — ${mathAxisLabel(math.operation as ScopeMathOperation)}`,
        headers: ['Reading', 'Value'],
        cells: readings.map((reading) => [reading.label, reading.value]),
      })
      if (math.clampedSamples > 0) {
        sections.push({
          kind: 'paragraph',
          text: `${math.clampedSamples.toLocaleString()} samples of the ratio railed at ±1000, where the denominator passes through zero — so every figure in that table is a reading of the clamp as much as of the ratio.`,
        })
      }
    }
  }

  // The X-Y figure's readings, when one was taken. Its own table for the math channel's reason:
  // a correlation, a phase and a ratio are not the channels' own readings and would put three
  // units into one "Reading" column.
  const xy = record.xy
  if (xy) {
    const readings = xyReadings(xy)
    if (readings.length > 0) {
      sections.push({
        kind: 'table',
        title: 'X–Y figure',
        headers: ['Reading', 'Value'],
        cells: readings.map((reading) => [reading.label, reading.value]),
      })
      const truncation = xyTruncationNote(xy)
      if (truncation) sections.push({ kind: 'paragraph', text: truncation })
    }
  }

  const facts: ReportRow[] = [
    { label: 'Record', value: `${record.frameCount.toLocaleString()} samples at ${formatNumber(record.sampleRateHz / 1000)} kS/s` },
    { label: 'Length', value: time(record.toS - record.fromS) },
    { label: 'Trigger at', value: `${time(record.triggerOffsetS)} into the record` },
  ]
  if (record.forced) {
    facts.push({ label: 'Untriggered', value: "Auto's timeout took this record; nothing crossed the level, so it is aligned to nothing." })
  }
  if (record.spliced) {
    facts.push({ label: 'Spliced', value: 'A chunk or block was dropped while this record was filling; two non-adjacent pieces sit side by side in it.' })
  }
  sections.push({ kind: 'keyValues', title: 'The record', rows: facts })

  // Said on paper because paper has no tooltip, and because it is the one reading here that is
  // routinely misread: both the QA403 and a sound card are AC-coupled.
  sections.push({
    kind: 'paragraph',
    text: "The mean is measured on the samples themselves. Both this analyzer and a sound card are AC-coupled, so on a quiet input it is the input's own residual offset rather than the DUT's DC.",
  })

  return sections
}

/** Every section of an oscilloscope chapter except the chart, in report order, for the chosen elements. */
export function scopeSections(subject: ReportSubject, elements: readonly ReportElementId[]): ReportSection[] {
  const sections: ReportSection[] = []

  if (elements.includes('description')) sections.push(...descriptionSections(subject))

  if (elements.includes('settings')) {
    const rows = scopeSettingsRows(subject.paramsJson)
    sections.push(rows.length > 0 ? { kind: 'keyValues', title: 'Settings', rows } : genericSettingsSection(subject.paramsJson))
  }

  if (elements.includes('measurements') && subject.data.kind === 'scope') {
    const { record, display } = subject.data
    sections.push(...scopeMeasurementSections(record, display.voltsPerDivision, display.showLeft, display.showRight))
  }

  return sections
}
