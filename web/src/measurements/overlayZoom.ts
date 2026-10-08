/**
 * Which overlays need their spectrum re-fetched at the chart's current zoom window, and which
 * answers are still wanted by the time they arrive.
 *
 * <p>A saved measurement is drawn from columns the server decimated once, over the whole span, at
 * the display budget — so zooming in used to magnify those columns rather than resolve more of the
 * bins behind them, even though every bin is in the database. Re-fetching at the window fixes that
 * (see `MeasurementsEndpoints.DecimateForDisplay`), and the only hard part is the bookkeeping: a
 * drag produces a series of windows, overlays come and go while requests are in flight, and the
 * answer to a window nobody is looking at any more must not be allowed to land.</p>
 *
 * <p>Pure, and its own module, because nothing in a `.vue` or a composable is testable under the
 * node environment vitest runs in — and every one of those races is invisible by eye until it is
 * the one drawing yesterday's zoom.</p>
 */

/** A chart window, or null for the whole span. Matches the chart's own `viewportChange` payload. */
export interface SpectrumWindow {
  loFreqHz: number
  hiFreqHz: number
}

/** What an overlay is currently showing, as far as this module is concerned. */
export interface OverlayZoomState {
  id: number
  visible: boolean
  /** The window its columns were fetched at — null for the whole span, which is how it starts. */
  fetchedWindow: SpectrumWindow | null
}

/**
 * Two windows that would produce the same columns. Compared by value rather than by identity
 * because every `viewportChange` builds a fresh object, so an identity test would re-fetch on
 * every emit, including the ones that report the same window twice.
 */
export function sameWindow(a: SpectrumWindow | null, b: SpectrumWindow | null): boolean {
  if (a === null || b === null) return a === b
  return a.loFreqHz === b.loFreqHz && a.hiFreqHz === b.hiFreqHz
}

/**
 * The overlays to re-fetch for `window`.
 *
 * <p>Hidden overlays are skipped: unchecking one leaves it in the list, and fetching columns for a
 * trace nobody is looking at would make a zoom cost requests in proportion to how many results had
 * ever been ticked. They re-fetch when shown again, because their `fetchedWindow` still says what
 * they hold.</p>
 *
 * <p>Zooming back out is a re-fetch like any other, and must be: the zoomed columns cover only the
 * window, and `resample` clamps outside its source range, so keeping them would draw the overlay
 * as two flat lines across the rest of the chart.</p>
 */
export function overlaysNeedingFetch(
  overlays: readonly OverlayZoomState[],
  window: SpectrumWindow | null,
): number[] {
  return overlays.filter((o) => o.visible && !sameWindow(o.fetchedWindow, window)).map((o) => o.id)
}

/**
 * Whether a response that has just arrived should still be applied.
 *
 * <p>Three ways it should not, and the first is the one that matters: a drag emits several windows
 * and their requests can finish out of order, so the only safe rule is that a result is wanted
 * only while it answers the window the chart is on *now*. The other two are the overlay having
 * been unchecked or removed while the request was in flight.</p>
 */
export function shouldApplyFetch(
  overlays: readonly OverlayZoomState[],
  overlayId: number,
  fetchedFor: SpectrumWindow | null,
  currentWindow: SpectrumWindow | null,
): boolean {
  if (!sameWindow(fetchedFor, currentWindow)) return false
  const overlay = overlays.find((o) => o.id === overlayId)
  return overlay !== undefined && overlay.visible
}
