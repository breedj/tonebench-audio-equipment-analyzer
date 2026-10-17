/**
 * Which chart of a measurement an annotation belongs to, by id.
 *
 * <p>A measurement keeps one flat list of annotations across all of its charts and each one names
 * its own view (see ChartAnnotation.view), so these ids are the join between a note and a picture.
 * They are the <b>report's</b> view ids, because that vocabulary already existed and is already
 * shared by the report dialog, the chart renderer and the stored report choice — a second set of
 * names for the same five charts is how a note comes to be drawn on screen and missing from the
 * page.</p>
 *
 * <p>Its own module rather than part of report/views.ts so that a chart component can import the
 * ids without importing the report, and so that report/views.ts can be built <i>from</i> them: the
 * ids are declared here once and that file spells no string of its own.</p>
 */

import type { AnnotationFrame } from '../measurements/chartAnnotations'
import {
  FFT_SPECTRUM_KIND,
  isCrosstalkKind,
  isFrequencyResponseKind,
  isImpedanceKind,
  isImpulseResponseKind,
  isOutputImpedanceKind,
  isSweepKind,
} from '../measurements/kinds'

export const SPECTRUM_ANNOTATION_VIEW = 'Spectrum'
export const SPECTROGRAM_ANNOTATION_VIEW = 'Spectrogram'
export const OSCILLOSCOPE_ANNOTATION_VIEW = 'Oscilloscope'
export const SWEEP_ANNOTATION_VIEW = 'Sweep'
export const BODE_ANNOTATION_VIEW = 'Bode'
export const NYQUIST_ANNOTATION_VIEW = 'Nyquist'
export const NICHOLS_ANNOTATION_VIEW = 'Nichols'
export const POLE_ZERO_ANNOTATION_VIEW = 'PoleZero'
export const SPLANE_ANNOTATION_VIEW = 'SPlane'
export const IMPULSE_ANNOTATION_VIEW = 'Impulse'
export const RESPONSE_ANNOTATION_VIEW = 'Response'
export const HARMONICS_ANNOTATION_VIEW = 'Harmonics'
export const CSD_ANNOTATION_VIEW = 'Csd'
export const BURST_DECAY_ANNOTATION_VIEW = 'BurstDecay'
export const CROSSTALK_ANNOTATION_VIEW = 'Crosstalk'

/**
 * The views of a kind that can carry annotations, in the order their panels and report tables list
 * them.
 *
 * <p>Not simply `reportViewsForKind`, for two reasons that pull in opposite directions. The FFT's
 * <b>spectrogram and oscilloscope</b> are charts the report prints as <i>elements</i> rather than as
 * views, so they have no entry there and still need one here. And three views are deliberately
 * <b>left out</b> because a two-coordinate annotation cannot name a place on them: the decay
 * surface's cumulative and burst projections, which are 3-D (across, depth <i>and</i> level) and
 * whose axes swap with the display mode, so a stored pair would mean a different point after a
 * toggle; and the s-plane's relief, for the same reason. The s-plane's heat map is a plain σ/jω plane
 * and is included.</p>
 */
export function annotationViewsForKind(kind: string | null | undefined): string[] {
  if (kind === FFT_SPECTRUM_KIND) {
    return [SPECTRUM_ANNOTATION_VIEW, SPECTROGRAM_ANNOTATION_VIEW, OSCILLOSCOPE_ANNOTATION_VIEW]
  }
  if (isSweepKind(kind)) return [SWEEP_ANNOTATION_VIEW]
  if (isFrequencyResponseKind(kind)) {
    return [
      BODE_ANNOTATION_VIEW,
      NYQUIST_ANNOTATION_VIEW,
      NICHOLS_ANNOTATION_VIEW,
      POLE_ZERO_ANNOTATION_VIEW,
      SPLANE_ANNOTATION_VIEW,
    ]
  }
  // No Csd and no BurstDecay: see the remarks above.
  if (isImpulseResponseKind(kind)) {
    return [IMPULSE_ANNOTATION_VIEW, RESPONSE_ANNOTATION_VIEW, HARMONICS_ANNOTATION_VIEW]
  }
  if (isImpedanceKind(kind)) return [BODE_ANNOTATION_VIEW, NYQUIST_ANNOTATION_VIEW]
  if (isCrosstalkKind(kind)) return [CROSSTALK_ANNOTATION_VIEW]
  if (isOutputImpedanceKind(kind)) return [BODE_ANNOTATION_VIEW]
  return []
}

/** The view a kind's annotations belong to when a stored row does not name one: its first chart. */
export function defaultAnnotationView(kind: string | null | undefined): string {
  return annotationViewsForKind(kind)[0] ?? SPECTRUM_ANNOTATION_VIEW
}

/**
 * A human name for a view id, for the Edit measurement dialog's group headings — the one place a
 * reader sees several views' annotations at once and has to tell which chart each was drawn on.
 *
 * <p>Deliberately one flat map rather than the per-kind labels in report/views.ts, where `Bode`
 * reads as "Bode" for a frequency response and "Output impedance" for an output-impedance
 * measurement. A heading in that dialog already sits under the measurement's own name and its kind
 * chip, so the generic word is the one that is not redundant.</p>
 */
const VIEW_LABELS: Readonly<Record<string, string>> = Object.freeze({
  [SPECTRUM_ANNOTATION_VIEW]: 'Spectrum',
  [SPECTROGRAM_ANNOTATION_VIEW]: 'Spectrogram',
  [OSCILLOSCOPE_ANNOTATION_VIEW]: 'Oscilloscope',
  [SWEEP_ANNOTATION_VIEW]: 'Distortion',
  [BODE_ANNOTATION_VIEW]: 'Bode',
  [NYQUIST_ANNOTATION_VIEW]: 'Nyquist',
  [NICHOLS_ANNOTATION_VIEW]: 'Nichols',
  [POLE_ZERO_ANNOTATION_VIEW]: 'Poles and zeros',
  [SPLANE_ANNOTATION_VIEW]: '|H(s)| surface',
  [IMPULSE_ANNOTATION_VIEW]: 'Impulse',
  [RESPONSE_ANNOTATION_VIEW]: 'Response',
  [HARMONICS_ANNOTATION_VIEW]: 'Harmonics',
  [CSD_ANNOTATION_VIEW]: 'Cumulative spectral decay',
  [BURST_DECAY_ANNOTATION_VIEW]: 'Burst decay',
  [CROSSTALK_ANNOTATION_VIEW]: 'Crosstalk',
})

/** The name of a view, or the id itself for one this build does not know. */
export function annotationViewLabel(view: string): string {
  return VIEW_LABELS[view] ?? view
}

/**
 * What a stored annotation means when its row does not say — the view and the quantities of the
 * kind's <b>first</b> chart.
 *
 * <p>Only ever needed for rows written before annotations carried either: in practice that is the
 * FFT's, since no other kind could be annotated until now. The other arms are not speculation
 * though — they are what a hand-edited `params_json` is read as, and defaulting every kind to hertz
 * and dBV would read a sweep's note as a frequency it never named.</p>
 */
export function storedAnnotationFrame(kind: string | null | undefined): AnnotationFrame {
  const view = defaultAnnotationView(kind)
  if (kind === FFT_SPECTRUM_KIND) return { view, xQuantity: 'hz', yQuantity: 'dbv' }
  if (isSweepKind(kind)) return { view, xQuantity: 'hz', yQuantity: 'pct' }
  if (isFrequencyResponseKind(kind)) return { view, xQuantity: 'hz', yQuantity: 'dbv' }
  if (isImpulseResponseKind(kind)) return { view, xQuantity: 'ms', yQuantity: 'volt' }
  if (isImpedanceKind(kind) || isOutputImpedanceKind(kind)) return { view, xQuantity: 'hz', yQuantity: 'ohm' }
  if (isCrosstalkKind(kind)) return { view, xQuantity: 'hz', yQuantity: 'db' }
  return { view, xQuantity: 'hz', yQuantity: 'dbv' }
}
