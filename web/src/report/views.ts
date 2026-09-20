import {
  FFT_SPECTRUM_KIND,
  isCrosstalkKind,
  isFrequencyResponseKind,
  isImpedanceKind,
  isImpulseResponseKind,
  isOutputImpedanceKind,
  isSweepKind,
} from '../measurements/kinds'

/**
 * The chart views a report of a kind can carry — the screen's own projections and views, by the
 * names the screen uses. A kind with one view has no choice to make and the dialog shows none.
 *
 * `combinable` says whether several measurements can share one chart of this view: the charts that
 * take history overlays can (the Bode plots, the spectrum, the sweep, the polar projections), the
 * ones that draw a single measurement cannot (the impulse, the harmonics, the decay surfaces, the
 * impedance locus) and get one chart per measurement in a combined report.
 */
export interface ReportView {
  id: string
  label: string
  combinable: boolean
}

const SPECTRUM_VIEWS: ReportView[] = [{ id: 'Spectrum', label: 'Spectrum', combinable: true }]
const SWEEP_VIEWS: ReportView[] = [{ id: 'Sweep', label: 'Distortion', combinable: true }]
const RESPONSE_VIEWS: ReportView[] = [
  { id: 'Bode', label: 'Bode', combinable: true },
  { id: 'Nyquist', label: 'Nyquist', combinable: true },
  { id: 'Nichols', label: 'Nichols', combinable: true },
]
const IMPULSE_VIEWS: ReportView[] = [
  { id: 'Impulse', label: 'Impulse', combinable: false },
  { id: 'Response', label: 'Response', combinable: true },
  { id: 'Harmonics', label: 'Harmonics', combinable: false },
  { id: 'Csd', label: 'Cumulative spectral decay', combinable: false },
  { id: 'BurstDecay', label: 'Burst decay', combinable: false },
]
const IMPEDANCE_VIEWS: ReportView[] = [
  { id: 'Bode', label: 'Bode', combinable: true },
  { id: 'Nyquist', label: 'Nyquist', combinable: false },
]
const CROSSTALK_VIEWS: ReportView[] = [{ id: 'Crosstalk', label: 'Crosstalk', combinable: true }]
const OUTPUT_IMPEDANCE_VIEWS: ReportView[] = [{ id: 'Bode', label: 'Output impedance', combinable: true }]

export function reportViewsForKind(kind: string | null | undefined): readonly ReportView[] {
  if (kind === FFT_SPECTRUM_KIND) return SPECTRUM_VIEWS
  if (isSweepKind(kind)) return SWEEP_VIEWS
  if (isFrequencyResponseKind(kind)) return RESPONSE_VIEWS
  if (isImpulseResponseKind(kind)) return IMPULSE_VIEWS
  if (isImpedanceKind(kind)) return IMPEDANCE_VIEWS
  if (isCrosstalkKind(kind)) return CROSSTALK_VIEWS
  if (isOutputImpedanceKind(kind)) return OUTPUT_IMPEDANCE_VIEWS
  return []
}

/** What a report includes until the user chooses: the kind's first view, which is the screen's own default. */
export function defaultReportViews(kind: string | null | undefined): string[] {
  const first = reportViewsForKind(kind)[0]
  return first ? [first.id] : []
}
