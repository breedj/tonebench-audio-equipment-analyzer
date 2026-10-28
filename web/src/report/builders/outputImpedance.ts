import type { OutputImpedanceParameters, ReportRow, ReportSection, SweepLevelMode, SweepLevelUnit } from '../../api/client'
import type { ImpedanceTrace } from '../../composables/useImpedance'
import { formatInductance, formatOhms } from '../../impedance/equivalent'
import { formatDampingFactor, MIN_MEASURABLE_OHMS, zoutReadings } from '../../outputImpedance/readings'
import { formatHz } from '../../response/format'
import type { ReportElementId } from '../elements'
import type { ReportSubject } from '../types'
import { descriptionSections, genericSettingsSection } from './generic'

/**
 * The output-impedance chapter. The chart is a PNG the orchestrator renders (the impedance chart
 * with its damping-factor axis); the settings rows are labelled the way the config panel labels
 * them, and the readings come from outputImpedance/readings.ts — the helper the readout panel
 * reads — so the paper and the screen quote the same numbers.
 */

const LEVEL_MODE_LABELS: Record<SweepLevelMode, string> = {
  GeneratorVoltage: 'Input voltage (generator)',
  OutputVoltage: 'DUT output voltage (first pass)',
  OutputPower: 'DUT output power (first pass)',
}

/** The on-screen names, so a report and the panel call one stimulus the same thing. */
const STIMULUS_LABELS: Record<string, string> = {
  Chirp: 'Chirp sweep',
  PinkNoise: 'Pink noise',
  SteppedSine: 'Stepped sine',
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

/** The Settings section's rows for an output-impedance run. */
export function outputImpedanceSettingsRows(paramsJson: string): ReportRow[] {
  let params: Partial<OutputImpedanceParameters>
  try {
    params = JSON.parse(paramsJson) as Partial<OutputImpedanceParameters>
  } catch {
    return []
  }
  if (!params || typeof params !== 'object') return []

  const rows: ReportRow[] = []

  // Absent means the stepped sine — a measurement saved before the broadband pair existed.
  const stimulus = params.stimulus ?? 'SteppedSine'
  const broadband = stimulus === 'Chirp' || stimulus === 'PinkNoise'
  rows.push({ label: 'Stimulus', value: STIMULUS_LABELS[stimulus] ?? String(stimulus) })

  if (broadband) {
    if (typeof params.frequencySpanHz === 'number') {
      rows.push({ label: 'Span', value: formatHz(params.frequencySpanHz) })
    }
    if (typeof params.resolutionMultiplier === 'number' && params.resolutionMultiplier > 1) {
      rows.push({ label: 'Resolution', value: `×${params.resolutionMultiplier}` })
    }
    if (stimulus === 'Chirp' && typeof params.chirpStartHz === 'number') {
      rows.push({ label: 'Chirp start', value: formatHz(params.chirpStartHz) })
    }
    if (stimulus === 'PinkNoise' && typeof params.noiseCutoffHz === 'number') {
      rows.push({ label: 'Pink noise cut-off', value: formatHz(params.noiseCutoffHz) })
    }
    if (typeof params.smoothingDenominator === 'number') {
      rows.push({ label: 'Smoothing', value: `1/${params.smoothingDenominator} octave` })
    }
  } else if (typeof params.startFreqHz === 'number' && typeof params.endFreqHz === 'number') {
    rows.push({ label: 'Sweep', value: `${formatHz(params.startFreqHz)} – ${formatHz(params.endFreqHz)}` })
  }
  if (!broadband && typeof params.stepsPerOctave === 'number') {
    rows.push({ label: 'Steps per octave', value: String(params.stepsPerOctave) })
  }
  if (typeof params.rightChannelIsReference === 'boolean') {
    rows.push({
      label: 'Method',
      value: params.rightChannelIsReference
        ? 'Complex — right channel is the reference, Zout with phase'
        : 'Magnitude only — level ratio, Zout assumed in phase with the load',
    })
  }
  if (params.levelMode !== undefined && params.levelValue !== undefined && params.levelUnit !== undefined) {
    let value = `${formatLevel(params.levelValue, params.levelUnit)} — ${LEVEL_MODE_LABELS[params.levelMode]}`
    if (params.levelMode === 'OutputPower' && typeof params.loadOhms === 'number') value += ` into ${formatNumber(params.loadOhms)} Ω`
    rows.push({ label: 'Level', value })
  }
  if (typeof params.loadOhms === 'number') {
    const second = params.secondLoadOhms === null ? 'open' : typeof params.secondLoadOhms === 'number' ? `${formatNumber(params.secondLoadOhms)} Ω` : '?'
    rows.push({ label: 'Loads', value: `${formatNumber(params.loadOhms)} Ω, then ${second}` })
  }
  if (typeof params.nominalOhms === 'number') rows.push({ label: 'Damping factor against', value: `${formatNumber(params.nominalOhms)} Ω` })
  if (broadband) {
    // Its span and smoothing are already above; the stepped grid's FFT and window say nothing here.
  } else if (params.adaptiveResolution === true) {
    rows.push({ label: 'Resolution', value: 'Adaptive per point' })
  } else if (typeof params.fftSize === 'number' || typeof params.sampleRateHz === 'number' || params.window) {
    const parts = [
      typeof params.fftSize === 'number' ? `${params.fftSize}-point` : null,
      params.window ?? null,
      typeof params.sampleRateHz === 'number' ? `at ${formatHz(params.sampleRateHz)}` : null,
    ].filter(Boolean)
    rows.push({ label: 'Resolution', value: parts.join(' ') })
  }
  if (typeof params.averageCount === 'number') {
    rows.push({ label: broadband ? 'Records per pass' : 'Averages', value: String(params.averageCount) })
  }
  if (typeof params.generatorAmpDbv === 'number') rows.push({ label: 'Generator level', value: `${params.generatorAmpDbv.toFixed(2)} dBV` })
  if (typeof params.inputRangeDbv === 'number') rows.push({ label: 'Input range (both passes)', value: `${params.inputRangeDbv.toFixed(0)} dBV` })
  if (typeof params.captureDelayMs === 'number' && params.captureDelayMs > 0) {
    rows.push({ label: 'Capture delay', value: `${formatNumber(params.captureDelayMs)} ms` })
  }
  return rows
}

/** The Measurements panel as sections: Zout and DF at the three quoted frequencies, the worst point and the median, then the coil when a phase was measured. */
export function outputImpedanceMeasurementSections(
  trace: ImpedanceTrace | null,
  nominalOhms: number,
  referenced: boolean,
): ReportSection[] {
  const readings = trace ? zoutReadings(trace, nominalOhms, referenced) : null
  if (!readings) return [{ kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' }]

  const sections: ReportSection[] = [
    {
      kind: 'table',
      title: 'Measurements',
      headers: ['', 'Zout', `Damping factor (${formatNumber(nominalOhms)} Ω)`],
      cells: [
        [formatHz(readings.low.freqHz), formatOhms(readings.low.ohms), formatDampingFactor(readings.low.dampingFactor)],
        [formatHz(readings.at1k.freqHz), formatOhms(readings.at1k.ohms), formatDampingFactor(readings.at1k.dampingFactor)],
        [formatHz(readings.high.freqHz), formatOhms(readings.high.ohms), formatDampingFactor(readings.high.dampingFactor)],
        [
          `Worst, at ${formatHz(readings.worst.freqHz)}`,
          formatOhms(readings.worst.ohms),
          formatDampingFactor(readings.worst.dampingFactor),
        ],
        ['Median', formatOhms(readings.medianOhms), formatDampingFactor(readings.medianOhms > MIN_MEASURABLE_OHMS ? nominalOhms / readings.medianOhms : null)],
      ],
    },
  ]

  if (readings.topEquivalent) {
    sections.push({
      kind: 'keyValues',
      rows: [
        {
          label: `Series equivalent at ${formatHz(readings.high.freqHz)}`,
          value: `R ${formatOhms(readings.topEquivalent.resistanceOhms)} · L ${formatInductance(readings.topEquivalent.inductanceH)}`,
        },
      ],
    })
  }

  return sections
}

/** Every section of an output-impedance chapter except the chart, in report order, for the chosen elements. */
export function outputImpedanceSections(subject: ReportSubject, elements: readonly ReportElementId[]): ReportSection[] {
  const sections: ReportSection[] = []

  if (elements.includes('description')) sections.push(...descriptionSections(subject))

  if (elements.includes('settings')) {
    const rows = outputImpedanceSettingsRows(subject.paramsJson)
    sections.push(rows.length > 0 ? { kind: 'keyValues', title: 'Settings', rows } : genericSettingsSection(subject.paramsJson))
  }

  if (elements.includes('measurements')) {
    const data = subject.data
    sections.push(
      ...(data.kind === 'outputImpedance'
        ? outputImpedanceMeasurementSections(data.trace, data.nominalOhms, data.rightChannelIsReference)
        : outputImpedanceMeasurementSections(null, 8, false)),
    )
  }

  return sections
}
