import {
  IMD_VS_AMPLITUDE_KIND,
  THD_VS_AMPLITUDE_KIND,
  THD_VS_FREQUENCY_KIND,
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

/** A screen whose overlays are stored sweep points — one per sweep kind, see `sweepOverlayScreen`. */
export type SweepOverlayScreen =
  | typeof THD_VS_FREQUENCY_KIND
  | typeof THD_VS_AMPLITUDE_KIND
  | typeof IMD_VS_AMPLITUDE_KIND

/**
 * Which sweep screen a stored result may be overlaid on, or null for a kind that stores no sweep
 * points at all.
 *
 * <p><b>The same trap as `curveOverlayScreen`, one family further along.</b> All three sweep kinds
 * read back into `sweepPoints` — an IMD run is mapped onto the very same point shape, see
 * imd/points.ts — so "does this overlay carry what my chart draws" is true for results of
 * measurements that have nothing to do with each other. Overlays deliberately stay loaded when you
 * move between measurements, so a THD-vs-frequency result checked on for its own screen then drew
 * itself onto the next THD-vs-amplitude chart, and into that screen's cursor readings and its two
 * preview charts with it (reported 2026-10-17). Its points carry a frequency that means nothing on
 * a volts axis, so every one of them landed at an arbitrary place.</p>
 *
 * <p><b>Exact kind equality, and no two kinds share a screen</b> — unlike the chirp and the
 * stepped sine above, which are two ways of measuring one curve. These three are three different
 * measurements: a frequency sweep's x axis is hertz and an amplitude sweep's is volts, so one
 * cannot be drawn against the other at all; and a THD run under an IMD run would be labelled with
 * the IMD family's names (IMD, IM2…), since the labels follow the screen and not the run. The cap
 * `useMeasurements.visibleOverlayCount` applies is already per exact kind, so this is the same
 * partition the checkbox limit has always used.</p>
 */
export function sweepOverlayScreen(kind: string | null | undefined): SweepOverlayScreen | null {
  return kind === THD_VS_FREQUENCY_KIND || kind === THD_VS_AMPLITUDE_KIND || kind === IMD_VS_AMPLITUDE_KIND
    ? kind
    : null
}
