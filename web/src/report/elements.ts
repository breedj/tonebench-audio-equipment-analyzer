import { isFrequencyResponseKind } from '../measurements/kinds'

/**
 * The elements a report of a measurement can include, by measurement kind.
 *
 * The ids are what `report.settings` stores (see useReportSettings) and what the builders switch on;
 * the labels are what the Report dialog shows. Every kind offers its chart, its description and its
 * settings; readings (the panel's numbers) are added per kind as a builder under report/builders —
 * the frequency response has them, the others so far show the generic settings.
 */
export type ReportElementId = 'chart' | 'description' | 'settings' | 'measurements'

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

/** What every kind offers. */
export const GENERIC_REPORT_ELEMENTS: readonly ReportElement[] = [CHART, DESCRIPTION, SETTINGS]

export function reportElementsForKind(kind: string | null | undefined): readonly ReportElement[] {
  if (isFrequencyResponseKind(kind)) return [CHART, DESCRIPTION, SETTINGS, MEASUREMENTS]
  return GENERIC_REPORT_ELEMENTS
}

/** Whether a kind's report can carry its chart — i.e. its chart component exposes `exportPng`. Every kind's does. */
export function reportHasChart(kind: string | null | undefined): boolean {
  return reportElementsForKind(kind).some((element) => element.id === 'chart')
}
