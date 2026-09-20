import {
  RESPONSE_RESOLUTIONS,
  RESPONSE_SMOOTHING,
  type ImpedanceCalibrationJson,
  type ImpedanceParameters,
  type ImpedanceStimulusName,
  type ReportRow,
  type ReportSection,
  type SweepLevelUnit,
} from '../../api/client'
import type { ImpedanceTrace } from '../../composables/useImpedance'
import type { DutType } from '../../impedance/component'
import { formatCurrent, formatInductance, formatOhms } from '../../impedance/equivalent'
import { componentReading, impedanceReadings } from '../../impedance/readings'
import { formatHz } from '../../response/format'
import type { ReportElementId } from '../elements'
import type { ReportSubject } from '../types'
import { descriptionSections, genericSettingsSection } from './generic'

/**
 * The impedance chapter. The chart is a PNG the orchestrator renders (the Bode and/or Nyquist
 * views); the settings rows are labelled the way the config panel labels them, and the readings —
 * the Measurements block and the component fit for the DUT the type names — come from
 * impedance/readings.ts, which the readout panel reads too, so the paper and the screen quote the
 * same numbers.
 */

const STIMULUS_LABELS: Record<ImpedanceStimulusName, string> = {
  Chirp: 'Chirp sweep',
  SteppedSine: 'Stepped sine',
  PinkNoise: 'Pink noise',
}

const CABLE_MODE_LABELS: Record<string, string> = {
  None: 'Nothing subtracted',
  Measured: 'Measured short subtracted',
  Fixed: 'Fixed value subtracted',
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

/** A stored standard counts when it has points — the same test the start request applies before sending it. */
function hasStandard(cal: ImpedanceCalibrationJson | null | undefined): boolean {
  return !!cal && Array.isArray(cal.freqHz) && cal.freqHz.length > 0
}

/** The Settings section's rows for an impedance run, in the config panel's order: fixture, calibration, measurement, advanced. */
export function impedanceSettingsRows(paramsJson: string): ReportRow[] {
  let params: Partial<ImpedanceParameters>
  try {
    params = JSON.parse(paramsJson) as Partial<ImpedanceParameters>
  } catch {
    return []
  }
  if (!params || typeof params !== 'object') return []

  const rows: ReportRow[] = []
  const stimulus = params.stimulus
  const calibrationStimulus = params.calibrationStimulus ?? stimulus
  const broadband = (s: ImpedanceStimulusName | null | undefined) => s === 'Chirp' || s === 'PinkNoise'

  if (typeof params.referenceOhms === 'number') {
    // A type on the global fixture runs with the fixture's resistor, not its own field; a stored
    // result records the one actually used and carries no flag, so only the live chapter says so.
    const global = params.useGlobalFixture === true ? ' (global fixture — the Measurements block has the resistor used)' : ''
    rows.push({ label: 'Reference resistor', value: `${formatOhms(params.referenceOhms)}${global}` })
  }
  if (typeof params.rightChannelIsReference === 'boolean') {
    rows.push({ label: 'Reference channel', value: params.rightChannelIsReference ? 'Right (generator side)' : 'Left (generator side)' })
  }
  if (stimulus) rows.push({ label: 'Stimulus', value: STIMULUS_LABELS[stimulus] ?? stimulus })
  if (typeof params.levelValue === 'number' && params.levelUnit) rows.push({ label: 'Drive level', value: formatLevel(params.levelValue, params.levelUnit) })

  // The broadband settings: the measurement's when it is a chirp or noise, else the calibration's.
  if (broadband(stimulus) || broadband(calibrationStimulus)) {
    const suffix = broadband(stimulus) ? '' : ' (calibration)'
    if (typeof params.frequencySpanHz === 'number') rows.push({ label: `Frequency span${suffix}`, value: formatHz(params.frequencySpanHz) })
    if (typeof params.resolutionMultiplier === 'number') {
      const option = RESPONSE_RESOLUTIONS.find((r) => r.multiplier === params.resolutionMultiplier)
      rows.push({ label: `Resolution${suffix}`, value: option ? `${option.label} Hz` : `×${params.resolutionMultiplier}` })
    }
    if ((stimulus === 'Chirp' || calibrationStimulus === 'Chirp') && typeof params.chirpStartHz === 'number') {
      rows.push({ label: `Chirp start${stimulus === 'Chirp' ? '' : ' (calibration)'}`, value: formatHz(params.chirpStartHz) })
    }
  }
  if (broadband(stimulus) && typeof params.smoothingDenominator === 'number') {
    const option = RESPONSE_SMOOTHING.find((s) => s.denominator === params.smoothingDenominator)
    rows.push({
      label: 'Smoothing',
      value: option ? option.label : params.smoothingDenominator > 0 ? `1/${params.smoothingDenominator} octave` : 'None',
    })
  }
  if (stimulus === 'SteppedSine') {
    if (typeof params.startFreqHz === 'number' && typeof params.endFreqHz === 'number') {
      rows.push({ label: 'Sweep', value: `${formatHz(params.startFreqHz)} – ${formatHz(params.endFreqHz)}` })
    }
    if (typeof params.stepsPerOctave === 'number') rows.push({ label: 'Steps per octave', value: String(params.stepsPerOctave) })
    if (typeof params.adaptiveResolution === 'boolean') {
      rows.push({ label: 'Resolution', value: params.adaptiveResolution ? 'Adaptive per point' : 'Fixed' })
    }
  }
  if (typeof params.averageCount === 'number') rows.push({ label: 'Averages', value: String(params.averageCount) })

  // Calibration: the standards this run was corrected with, the stimulus and level they were taken at.
  const open = hasStandard(params.openCal)
  const short = hasStandard(params.shortCal)
  if (params.openCal !== undefined || params.shortCal !== undefined) {
    const standards = [open ? 'open' : null, short ? 'short' : null].filter(Boolean)
    let value = standards.length > 0 ? `${standards.join(' and ')} standard${standards.length > 1 ? 's' : ''}` : 'None'
    if (standards.length > 0 && calibrationStimulus && calibrationStimulus !== stimulus) value += `, by ${STIMULUS_LABELS[calibrationStimulus].toLowerCase()}`
    if (params.useGlobalFixture === true) value += ' (global fixture)'
    rows.push({ label: 'Calibration', value: value.charAt(0).toUpperCase() + value.slice(1) })
  }
  if (typeof params.calibrationLevelValue === 'number' && params.levelUnit && (open || short)) {
    rows.push({ label: 'Calibration drive level', value: formatLevel(params.calibrationLevelValue, params.levelUnit) })
  }
  if (params.cableMode) {
    let value = CABLE_MODE_LABELS[params.cableMode] ?? params.cableMode
    if (params.cableMode === 'Fixed' && typeof params.fixedCableOhms === 'number' && typeof params.fixedCableMillihenry === 'number') {
      value += `: ${formatOhms(params.fixedCableOhms)} + ${formatInductance(params.fixedCableMillihenry / 1000)}`
    }
    rows.push({ label: 'Leads', value })
  }
  if (typeof params.analyzerInputOhms === 'number') rows.push({ label: 'Analyzer input impedance', value: formatOhms(params.analyzerInputOhms) })
  if (stimulus === 'Chirp' && params.applySweepGate === true) rows.push({ label: 'Sweep gate', value: 'Harmonic packets gated' })
  if (typeof params.generatorAmpDbv === 'number') rows.push({ label: 'Generator level', value: `${params.generatorAmpDbv.toFixed(2)} dBV` })
  const captureDelayMs = (params as { captureDelayMs?: unknown }).captureDelayMs
  if (typeof captureDelayMs === 'number' && captureDelayMs > 0) rows.push({ label: 'Capture delay', value: `${formatNumber(captureDelayMs)} ms` })
  return rows
}

/**
 * The Measurements panel as sections: the peak, minimum and median |Z|, the drive current and the
 * reference resistor, then — for the DUT the type names — its component fit and the notes that
 * apply to it.
 */
export function impedanceMeasurementSections(
  traces: ImpedanceTrace[],
  dutType: DutType,
  referenceOhms: number,
  generatorAmpDbv: number | null,
): ReportSection[] {
  const trace = traces.find((t) => t.kind === 'Impedance') ?? null
  const readings = trace ? impedanceReadings(trace, referenceOhms, generatorAmpDbv) : null
  if (!trace || !readings) return [{ kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' }]

  const sections: ReportSection[] = [
    {
      kind: 'keyValues',
      title: 'Measurements',
      rows: [
        { label: 'Peak |Z|', value: `${formatOhms(readings.peak.ohms)} at ${formatHz(readings.peak.freqHz)}` },
        { label: 'Minimum |Z|', value: `${formatOhms(readings.min.ohms)} at ${formatHz(readings.min.freqHz)}` },
        { label: 'Median |Z| (curve)', value: formatOhms(readings.medianOhms) },
        { label: 'Drive current', value: formatCurrent(readings.driveCurrentAmps) },
        { label: 'Reference R', value: formatOhms(referenceOhms) },
      ],
    },
  ]

  const component = componentReading(trace, dutType)
  if (component) {
    if (component.rows.length > 0) {
      sections.push({ kind: 'keyValues', title: component.label, rows: component.rows.map((r) => ({ label: r.label, value: r.value })) })
    }
    if (component.notes.length > 0) {
      sections.push({ kind: 'paragraph', title: component.rows.length > 0 ? null : component.label, text: component.notes.join('\n') })
    }
  }

  return sections
}

/** Every section of an impedance chapter except the chart, in report order, for the chosen elements. */
export function impedanceSections(subject: ReportSubject, elements: readonly ReportElementId[]): ReportSection[] {
  const sections: ReportSection[] = []

  if (elements.includes('description')) sections.push(...descriptionSections(subject))

  if (elements.includes('settings')) {
    const rows = impedanceSettingsRows(subject.paramsJson)
    sections.push(rows.length > 0 ? { kind: 'keyValues', title: 'Settings', rows } : genericSettingsSection(subject.paramsJson))
  }

  if (elements.includes('measurements')) {
    const data = subject.data
    sections.push(
      ...(data.kind === 'impedance'
        ? impedanceMeasurementSections(data.traces, data.dutType, data.referenceOhms, data.generatorAmpDbv)
        : impedanceMeasurementSections([], 'None', 0, null)),
    )
  }

  return sections
}
