import {
  isCrosstalkKind,
  isFrequencyResponseKind,
  isImpedanceKind,
  isImpulseResponseKind,
  isOutputImpedanceKind,
} from './kinds'

/**
 * Which measurement a loaded overlay came from, when that is not the one on screen.
 *
 * <p><b>Why this exists.</b> The two lists on a measurement screen are scoped differently, and
 * nothing said so. The <b>History</b> checklist lists results of the <i>open measurement</i>, since
 * its items are server-filtered to it; the <b>Overlays</b> list filters the loaded overlays by
 * <i>kind</i>, so it also shows results checked on while a different measurement of the same kind
 * was open. Two FFT measurements in one project therefore read as "6 overlays but only 4 in the
 * History" — reported on 2026-09-24 against a project holding "Loopback 1 kHz" and "Amp 1 kHz at
 * 1 W", four results each.</p>
 *
 * <p>Filtering the Overlays list to the open measurement instead would delete the comparison the
 * list is most useful for — the analyzer's own loopback floor drawn under the amplifier's spectrum
 * — so the foreign rows stay and are named instead. Naming them also gives the user the only clue
 * available about where to uncheck one: a foreign overlay has no row in the History checklist, so
 * this list is the only place it can be hidden or removed.</p>
 */
export interface OverlayOrigin {
  measurementTypeId: number
  measurementTypeName: string
}

/**
 * The measurement's name when this overlay came from a different one, or null when it belongs to
 * the one on screen.
 *
 * <p>Null while nothing is open (`currentTypeId` null) as well: with no open measurement there is
 * nothing for an overlay to be foreign <i>to</i>, and marking every row would be noise rather than
 * information.</p>
 */
export function foreignMeasurementName(origin: OverlayOrigin, currentTypeId: number | null): string | null {
  if (currentTypeId === null) return null
  return origin.measurementTypeId === currentTypeId ? null : origin.measurementTypeName
}

/** A screen whose overlays are curves read back out of the shared `response_trace` rows. */
export type CurveOverlayScreen =
  | 'Response'
  | 'ImpulseResponse'
  | 'Crosstalk'
  | 'Impedance'
  | 'OutputImpedance'

/**
 * Which screen a stored result may be overlaid on, for the five kinds whose curves all come out of
 * `response_trace` — or null for a kind that stores no curve there at all.
 *
 * <p><b>Why this is a function and not a filter on the overlay's shape.</b> Overlays deliberately
 * stay loaded when you move between measurements, and five kinds read back through one reader into
 * two fields (`responseTraces`, `impedanceTraces`). Several of them then use the <i>same trace
 * kinds</i>: an impulse response's gated response is a `Left` and a `Gain` curve, which is exactly
 * what the chirp screen draws. So "does this overlay carry the curve my chart asks for" is true for
 * results of measurements that have nothing to do with each other, and it drew an impulse result's
 * gated response and group delay onto the next frequency-response chart — legend and all, with no
 * row in that screen's History to uncheck it from (reported 2026-09-25), exactly as an amplifier's
 * source impedance had appeared on a loudspeaker's impedance chart the day before. The kind is the
 * only thing that can tell them apart, and answering it in one place is what lets a test assert the
 * screens partition rather than overlap.</p>
 *
 * <p>The chirp and the stepped sine share <b>one</b> screen on purpose: they are two ways of
 * measuring the same curve, stored identically, and drawing one under the other is a comparison
 * worth having.</p>
 */
export function curveOverlayScreen(kind: string | null | undefined): CurveOverlayScreen | null {
  if (isFrequencyResponseKind(kind)) return 'Response'
  if (isImpulseResponseKind(kind)) return 'ImpulseResponse'
  if (isCrosstalkKind(kind)) return 'Crosstalk'
  if (isImpedanceKind(kind)) return 'Impedance'
  if (isOutputImpedanceKind(kind)) return 'OutputImpedance'
  return null
}
