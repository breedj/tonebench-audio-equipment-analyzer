import type { SpectrumWindow } from './overlayZoom'

export interface Span {
  lo: number
  hi: number
}

/**
 * The frequency span the spectrum chart treats as "fully zoomed out".
 *
 * <p>Only an <em>overview</em> source describes the whole spectrum; one fetched for a window
 * describes that window. Letting the latter set the span would redefine "fully zoomed out" — and
 * on this chart that is not cosmetic, because the chart decides whether it is zoomed by comparing
 * its axis against this span, and reports that decision to the server, which is what produces the
 * windowed data in the first place. The output feeds its own input.</p>
 *
 * <p><b>Measured, before this existed:</b> panel window 10–20 kHz, one stored overlay reaching
 * 43 kHz, nothing streaming. 20 kHz falls short of 43 kHz, so the chart reported a zoom and the
 * overlay was re-fetched for 10–20 kHz; the new columns stopped at 20 kHz, so the chart read itself
 * as fully zoomed out and reported "no zoom"; the full-span re-fetch restored the 43 kHz span, and
 * round it went — one request per second indefinitely, repainting each time. It read as a flicker,
 * and it made dragging a zoom impossible because every gesture was overwritten within the second. A
 * panel window that already covered the data (10–100 kHz) never entered the loop, which is why it
 * appeared only at some settings.</p>
 *
 * <p>The live path has always had this rule, keyed on its frames' <c>STREAM_FLAG_ZOOMED</c>; the
 * overlay fallback did not, and that is the hole this closes.</p>
 *
 * @param previous the span established so far; `hi <= 0` means none yet
 * @param columns the source's columns, lowest frequency first
 * @param fetchedWindow non-null when those columns were fetched for a window rather than in full
 */
export function overviewSpan(
  previous: Span,
  columns: readonly { freqHz: number }[] | null | undefined,
  fetchedWindow: SpectrumWindow | null | undefined,
): Span {
  if (!columns || columns.length === 0) return previous

  return spanFrom(
    previous,
    { lo: columns[0]!.freqHz, hi: columns[columns.length - 1]!.freqHz },
    fetchedWindow != null,
  )
}

/**
 * {@link overviewSpan}'s rule for a source that states its own edges rather than listing columns —
 * a live spectrum frame, whose header carries the span and whose `STREAM_FLAG_ZOOMED` says whether
 * that span is the spectrum's or only the window it was cut for.
 *
 * <p><b>The exception is the whole of this.</b> A windowed source keeps whatever span is already
 * known, but on first render it is the only source there is, and a slightly narrow span beats none
 * at all. The live path did not have that exception until 2026-10-14 and simply left the span
 * unset, which the chart reads as "nothing to draw": it rendered its empty state over a capture
 * that was sitting right there in the store, and `minMaxBandPlugin` — which draws straight from the
 * frames rather than from the plot's data — went on painting the band across the empty axes.
 * Reported as "the FFT data is gone except for the min/max data" after switching measurement away
 * and back, which is exactly when it bites: `fullLoFreqHz`/`fullHiFreqHz` live in the chart
 * component, so a remount starts with no span, and whether the store holds an overview frame at
 * that moment is a matter of what the server last re-sent. Screenshot and Report went with it —
 * `exportPng` throws "There is no spectrum to export" off the same answer.</p>
 */
export function spanFrom(previous: Span, span: Span, windowed: boolean): Span {
  if (windowed && previous.hi > 0) return previous
  return span
}
