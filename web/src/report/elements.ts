import {
  FFT_SPECTRUM_KIND,
  isCrosstalkKind,
  isFrequencyResponseKind,
  isImpedanceKind,
  isOutputImpedanceKind,
  isSweepKind,
} from '../measurements/kinds'

/**
 * The elements a report of a measurement can include, by measurement kind.
 *
 * The ids are what `report.settings` stores (see useReportSettings) and what the builders switch on;
 * the labels are what the Report dialog shows. Every kind offers its chart, its description and its
 * settings; readings (the panel's numbers) are added per kind as a builder under report/builders —
 * the frequency response has its Measurements panel, the FFT its metrics and harmonics tables and
 * the two secondary charts under its spectrum.
 */
export type ReportElementId =
  | 'chart'
  | 'description'
  | 'settings'
  | 'measurements'
  | 'metrics'
  | 'harmonics'
  | 'oscilloscope'
  | 'waterfall'

export interface ReportElement {
  id: ReportElementId
  label: string
  hint: string
}

const CHART: ReportElement = {
  id: 'chart',
  label: 'Chart',
  hint: "The main chart, rendered at a fixed size in the light colour scheme with the display panel's view and ranges.",
}

const DESCRIPTION: ReportElement = {
  id: 'description',
  label: 'Description',
  hint: 'The notes on the measurement result and on the measurement itself.',
}

const SETTINGS: ReportElement = {
  id: 'settings',
  label: 'Settings',
  hint: 'The settings the measurement ran with.',
}

const MEASUREMENTS: ReportElement = {
  id: 'measurements',
  label: 'Measurements',
  hint: 'The ±1 dB and −3 dB corners, the peak and the reference — the Measurements panel.',
}

const CROSSTALK_MEASUREMENTS: ReportElement = {
  id: 'measurements',
  label: 'Measurements',
  hint: 'Each direction at 1 kHz, its worst point and the band ends — the Measurements panel.',
}

const OUTPUT_IMPEDANCE_MEASUREMENTS: ReportElement = {
  id: 'measurements',
  label: 'Measurements',
  hint: 'Zout and damping factor at 20 Hz, 1 kHz and 20 kHz, the worst point and the median — the Measurements panel.',
}

const SWEEP_MEASUREMENTS: ReportElement = {
  id: 'measurements',
  label: 'Measurements',
  hint: "Per channel: the worst and best THD or IMD and where they occur, then every point's metrics — the chart's own table.",
}

const IMPEDANCE_MEASUREMENTS: ReportElement = {
  id: 'measurements',
  label: 'Measurements',
  hint: 'Peak, minimum and median |Z|, the drive current and, for the Component the screen selects, its capacitor, inductor or Thiele-Small fit.',
}

const METRICS: ReportElement = {
  id: 'metrics',
  label: 'Metrics',
  hint: 'The metric tiles above the chart, per channel — the ones chosen with the Metrics button.',
}

const HARMONICS: ReportElement = {
  id: 'harmonics',
  label: 'Harmonics table',
  hint: 'The fundamental and each harmonic with its frequency, level and dBc, per channel.',
}

const OSCILLOSCOPE: ReportElement = {
  id: 'oscilloscope',
  label: 'Oscilloscope',
  hint: 'The waveform under the spectrum. Only for the measurement on screen — a stored result keeps no waveform.',
}

const WATERFALL: ReportElement = {
  id: 'waterfall',
  label: 'Waterfall',
  hint: 'The waterfall under the spectrum, with the history it has collected. Only for the measurement on screen, and only while the waterfall is shown.',
}

/** What every kind offers. */
export const GENERIC_REPORT_ELEMENTS: readonly ReportElement[] = [CHART, DESCRIPTION, SETTINGS]

export function reportElementsForKind(kind: string | null | undefined): readonly ReportElement[] {
  if (isFrequencyResponseKind(kind)) return [CHART, DESCRIPTION, SETTINGS, MEASUREMENTS]
  if (isCrosstalkKind(kind)) return [CHART, DESCRIPTION, SETTINGS, CROSSTALK_MEASUREMENTS]
  if (isOutputImpedanceKind(kind)) return [CHART, DESCRIPTION, SETTINGS, OUTPUT_IMPEDANCE_MEASUREMENTS]
  if (isSweepKind(kind)) return [CHART, DESCRIPTION, SETTINGS, SWEEP_MEASUREMENTS]
  if (isImpedanceKind(kind)) return [CHART, DESCRIPTION, SETTINGS, IMPEDANCE_MEASUREMENTS]
  if (kind === FFT_SPECTRUM_KIND) return [CHART, DESCRIPTION, SETTINGS, METRICS, HARMONICS, OSCILLOSCOPE, WATERFALL]
  return GENERIC_REPORT_ELEMENTS
}

/**
 * What a report of a kind includes until the user chooses: everything the kind offers except the
 * settings — a list of every as-run parameter is the one element most reports are better without,
 * and the one a reader asks for least.
 */
export function defaultReportElements(kind: string | null | undefined): ReportElementId[] {
  return reportElementsForKind(kind)
    .map((element) => element.id)
    .filter((id) => id !== 'settings')
}

/** Whether a kind's report can carry its chart — i.e. its chart component exposes `exportPng`. Every kind's does. */
export function reportHasChart(kind: string | null | undefined): boolean {
  return reportElementsForKind(kind).some((element) => element.id === 'chart')
}
