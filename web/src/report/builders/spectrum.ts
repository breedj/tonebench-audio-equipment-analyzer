import type { ReportRow, ReportSection } from '../../api/client'
import { channelTiles, GEN_METRIC_LABEL, NOT_APPLICABLE } from '../../metrics'
import type { IntermodulationFrame, MetersFrame, MultitoneFrame } from '../../streaming/protocol'
import { imdProductLabel } from '../../imd/products'
import type { ReportElementId } from '../elements'
import type { ReportSubject } from '../types'
import { descriptionSections, genericSettingsSection } from './generic'

/**
 * The FFT chapter's text sections: the metric tiles and the harmonics table, per channel, from the
 * same formulas the screen's MetricsPanel and HarmonicsTable use (channelTiles, and the table's own
 * dBV = fundamental + dBc), so the paper and the screen cannot disagree. The oscilloscope and the
 * spectrogram are images the orchestrator renders, not sections built here.
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
  const { meters, selectedMetrics, generatorTones, loadOhms, noise, display, stimulus } = subject.data
  const sections: ReportSection[] = []

  // The Gen tile is one tile for both channels on screen (GeneratorMetricsPanel); here it heads the
  // first channel's rows, as the stimulus the numbers below were measured against.
  const genRow: ReportRow | null =
    selectedMetrics.includes(GEN_METRIC_LABEL) && generatorTones.length > 0
      ? { label: 'Gen', value: generatorTones.map((t) => `${t.freqHz.toFixed(3)} Hz, ${t.ampDbv.toFixed(2)} dBV`).join(', ') }
      : null

  // Every channel that has meters or a noise frame: a noise measurement with the generator off has
  // the latter and not the former, and its three noise tiles are the whole point.
  const channels = [...new Set([...Object.keys(meters), ...Object.keys(noise)].map(Number))].filter((c) => c === 0 || c === 1).sort((a, b) => a - b)
  for (const channel of channels) {
    const tiles = channelTiles(
      meters[channel] ?? null, generatorTones, loadOhms, subject.data.intermodulation[channel] ?? null, false,
      noise[channel] ?? null, display.dutGainDb.value, subject.data.multitone?.[channel] ?? null, stimulus ?? null,
    )
    const byLabel = new Map(tiles.map((tile) => [tile.label, tile]))
    const rows: ReportRow[] = []
    if (genRow && sections.length === 0) rows.push(genRow)
    let caveat: string | null = null
    let band: string | null = null
    for (const label of selectedMetrics) {
      const tile = byLabel.get(label)
      // The weighting qualifier travels with the label, as it does on the tile.
      if (tile) rows.push({ label: tile.qualifier ? `${tile.label} (${tile.qualifier})` : tile.label, value: tile.value.replace(/\n/g, ', ') })
      if (tile?.caveat) caveat = tile.caveat
      if (tile?.band) band = tile.band
    }
    // The band those figures were integrated over, once per channel for the same reason the caveat
    // is once per channel: on screen it is a footnote under each tile, and thirteen copies of the
    // same two numbers down a printed column is noise. Only when a printed row actually carries it.
    if (band) rows.push({ label: 'Measurement band', value: band })
    // Paper has no tooltip, so the reason those rows read "n/a" is spelled out once per channel
    // rather than repeated on each of the thirteen that carry it.
    if (caveat) rows.push({ label: NOT_APPLICABLE, value: `${caveat.charAt(0).toUpperCase()}${caveat.slice(1)}.` })
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
  sections.push(...intermodulationSections(subject.data.intermodulation))
  sections.push(...multitoneSections(subject.data.multitone ?? {}))
  return sections
}

/**
 * The Multitone table per channel, when the measurement had one: every tone with the highest line
 * and the D+N of the gap above it, and the total, as the screen's MultitoneTable lays them out.
 * Nothing at all without a multitone figure.
 */
export function multitoneSections(multitone: Record<number, MultitoneFrame>): ReportSection[] {
  const sections: ReportSection[] = []
  const entries = Object.entries(multitone)
    .map(([channel, frame]) => [Number(channel), frame] as [number, MultitoneFrame])
    .filter(([channel]) => channel === 0 || channel === 1)
    .sort(([a], [b]) => a - b)

  const hz = (v: number) => (v >= 1000 ? `${(v / 1000).toFixed(v % 1000 === 0 ? 0 : 2)} kHz` : `${v.toFixed(1)} Hz`)
  for (const [channel, f] of entries) {
    sections.push({
      kind: 'table',
      title: `Multitone — ${CHANNEL_LABELS[channel]}`,
      headers: ['Tone', 'Level', 'Highest above', 'dBr', 'D+N dBr'],
      cells: f.tones.map((tone, i) => {
        const band = f.bands[i]
        if (!band) return [hz(tone.freqHz), `${tone.dbv.toFixed(2)} dBV`, '—', '—', '—']
        if (!band.resolved) return [hz(tone.freqHz), `${tone.dbv.toFixed(2)} dBV`, 'unresolved', '—', '—']
        return [
          hz(tone.freqHz),
          `${tone.dbv.toFixed(2)} dBV`,
          `${hz(band.highestFreqHz)} · ${band.highestDbv.toFixed(1)} dBV`,
          `${band.highestDbr.toFixed(1)} dBr`,
          `${band.dnDbr.toFixed(1)} dBr`,
        ]
      }),
    })
    const tdnDb = f.tdn > 0 ? `, ${(20 * Math.log10(f.tdn)).toFixed(2)} dB` : ''
    sections.push({
      kind: 'keyValues',
      title: `TD+N — ${CHANNEL_LABELS[channel]}`,
      rows: [
        { label: 'TD+N', value: `${(f.tdn * 100).toFixed(5)} %${tdnDb}` },
        { label: 'Gaps resolved', value: `${f.resolvedBands} of ${f.bands.length}` },
        { label: 'Comb level (RMS)', value: `${f.combDbv.toFixed(2)} dBV` },
      ],
    })
  }
  return sections
}

/**
 * The Intermodulation table per channel, when the measurement had one: both tones, every product
 * with its combination, and the totals (IMD, per order, both level conventions) as the screen's
 * ImdProductsTable lays them out. Nothing at all without an IMD figure — a single-tone measurement
 * has no "no intermodulation" paragraph to print.
 */
export function intermodulationSections(intermodulation: Record<number, IntermodulationFrame>): ReportSection[] {
  const sections: ReportSection[] = []
  const entries = Object.entries(intermodulation)
    .map(([channel, frame]) => [Number(channel), frame] as [number, IntermodulationFrame])
    .filter(([channel]) => channel === 0 || channel === 1)
    .sort(([a], [b]) => a - b)

  for (const [channel, f] of entries) {
    const method = f.method === 'Smpte' ? 'SMPTE' : 'CCIF'
    const reference = f.method === 'Smpte' ? 'the HF carrier' : 'the RMS sum of both tones'
    const db = (percent: number) => (20 * Math.log10(Math.max(percent, Number.EPSILON) / 100)).toFixed(2)
    sections.push({
      kind: 'table',
      title: `Intermodulation (${method}) — ${CHANNEL_LABELS[channel]}`,
      headers: ['Product', 'Frequency', 'Level', `dBc re ${reference}`],
      cells: [
        ['Tone f1', `${f.toneLowHz.toFixed(1)} Hz`, `${f.toneLowDbv.toFixed(2)} dBV`, '—'],
        ['Tone f2', `${f.toneHighHz.toFixed(1)} Hz`, `${f.toneHighDbv.toFixed(2)} dBV`, '—'],
        ...f.products.map((p) => [
          `IM${p.order} (${imdProductLabel(f.method, p, f)})`,
          `${p.freqHz.toFixed(1)} Hz`,
          `${p.dbv.toFixed(2)} dBV`,
          `${p.dbc.toFixed(1)} dB`,
        ]),
      ],
    })
    sections.push({
      kind: 'keyValues',
      title: `IMD — ${CHANNEL_LABELS[channel]}`,
      rows: [
        { label: `IMD (${method})`, value: `${f.imdPercent.toFixed(5)} %, ${db(f.imdPercent)} dB` },
        ...f.perOrder.map((o) => ({ label: `IM${o.order}`, value: `${o.percent.toFixed(5)} %, ${db(o.percent)} dB` })),
        { label: 'Sine-equivalent level', value: `${f.sineEquivalentDbv.toFixed(2)} dBV` },
        { label: 'Combined RMS', value: `${f.combinedRmsDbv.toFixed(2)} dBV` },
      ],
    })
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
