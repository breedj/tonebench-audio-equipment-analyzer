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
