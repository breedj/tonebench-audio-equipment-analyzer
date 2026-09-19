import type { ReportRow, ReportSection } from '../../api/client'
import { channelTiles, GEN_METRIC_LABEL } from '../../metrics'
import type { MetersFrame } from '../../streaming/protocol'
import type { ReportElementId } from '../elements'
import type { ReportSubject } from '../types'
import { descriptionSections, genericSettingsSection } from './generic'

/**
 * The FFT chapter's text sections: the metric tiles and the harmonics table, per channel, from the
 * same formulas the screen's MetricsPanel and HarmonicsTable use (channelTiles, and the table's own
 * dBV = fundamental + dBc), so the paper and the screen cannot disagree. The oscilloscope and the
 * waterfall are images the orchestrator renders, not sections built here.
 */

const CHANNEL_LABELS = ['L', 'R'] as const

function channelsOf(meters: Record<number, MetersFrame>): [number, MetersFrame][] {
  return Object.entries(meters)
    .map(([channel, frame]) => [Number(channel), frame] as [number, MetersFrame])
    .filter(([channel]) => channel === 0 || channel === 1)
    .sort(([a], [b]) => a - b)
}

/** One key/value section per channel with the selected tiles — what the tiles above the chart show. */
export function metricsSections(subject: ReportSubject): ReportSection[] {
  if (subject.data.kind !== 'spectrum') return []
  const { meters, selectedMetrics, generatorTones, loadOhms } = subject.data
  const sections: ReportSection[] = []

  // The Gen tile is one tile for both channels on screen (GeneratorMetricsPanel); here it heads the
  // first channel's rows, as the stimulus the numbers below were measured against.
  const genRow: ReportRow | null =
    selectedMetrics.includes(GEN_METRIC_LABEL) && generatorTones.length > 0
      ? { label: 'Gen', value: generatorTones.map((t) => `${t.freqHz.toFixed(3)} Hz, ${t.ampDbv.toFixed(2)} dBV`).join(', ') }
      : null

  for (const [channel, frame] of channelsOf(meters)) {
    const byLabel = new Map(channelTiles(frame, generatorTones, loadOhms).map((tile) => [tile.label, tile]))
    const rows: ReportRow[] = []
    if (genRow && sections.length === 0) rows.push(genRow)
    for (const label of selectedMetrics) {
      const tile = byLabel.get(label)
      if (tile) rows.push({ label: tile.label, value: tile.value.replace(/\n/g, ', ') })
    }
    if (rows.length > 0) sections.push({ kind: 'keyValues', title: `Metrics — ${CHANNEL_LABELS[channel]}`, rows })
  }

  if (sections.length === 0) {
    sections.push({
      kind: 'paragraph',
      title: 'Metrics',
      text: Object.keys(meters).length === 0 ? 'No fundamental was found, so there are no metrics.' : 'No metrics are selected.',
    })
  }
  return sections
}

/** The harmonics table per channel: order, frequency, level and dBc, the fundamental first. */
export function harmonicsSections(subject: ReportSubject): ReportSection[] {
  if (subject.data.kind !== 'spectrum') return []
  const sections: ReportSection[] = []

  for (const [channel, frame] of channelsOf(subject.data.meters)) {
    if (frame.harmonics.length === 0) continue
    sections.push({
      kind: 'table',
      title: `Harmonics — ${CHANNEL_LABELS[channel]}`,
      headers: ['Order', 'Frequency', 'Level', 'dBc'],
      cells: frame.harmonics.map((h, i) => [
        i === 0 ? 'Fundamental' : `H${i + 1}`,
        `${h.freqHz.toFixed(1)} Hz`,
        `${(frame.fundamentalDbv + h.dbc).toFixed(2)} dBV`,
        i === 0 ? '—' : `${h.dbc.toFixed(1)} dB`,
      ]),
    })
  }

  if (sections.length === 0) {
    sections.push({ kind: 'paragraph', title: 'Harmonics', text: 'No fundamental was found, so there are no harmonics.' })
  }
  return sections
}

export function spectrumSections(subject: ReportSubject, elements: readonly ReportElementId[]): ReportSection[] {
  const sections: ReportSection[] = []
  if (elements.includes('metrics')) sections.push(...metricsSections(subject))
  if (elements.includes('harmonics')) sections.push(...harmonicsSections(subject))
  if (elements.includes('description')) sections.push(...descriptionSections(subject))
  if (elements.includes('settings')) sections.push(genericSettingsSection(subject.paramsJson))
  return sections
}
