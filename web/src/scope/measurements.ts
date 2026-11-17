/**
 * The readings that can honestly be taken from a record <em>slice</em> — which in this build is all
 * the client has.
 *
 * <p><b>Why this is a short list and must stay one.</b> What comes over the wire for a wide window
 * is a min/max pair per display column, not the waveform: the decimation keeps both extremes of
 * every column precisely so that nothing narrower than a column can hide (the rule the spectrum's
 * own decimator carries), so <b>Vmax, Vmin and Vpp survive it exactly</b> and are the same figures
 * a full-resolution pass would give. Nothing else does. An RMS over column maxima is not an RMS of
 * anything, a period counted off columns is a period of the decimation, and a rise time read off a
 * min/max pair is a reading of the column width — which is the measurement a scope is most often
 * pointed at, and the one it would be worst to get plausibly wrong.</p>
 *
 * <p>So the rest of the measurement set is computed <b>server-side on the committed record</b> and
 * arrives with the measurements phase. These three are here because two controls need them
 * now and cannot wait for it: <b>Autofit</b>, which frames the trace, and the trigger's
 * <b>50 %</b> button, which needs the midpoint of the extremes. Both are about the picture rather
 * than about the DUT, which is exactly what a decimated extreme can answer for.</p>
 */

import type { ScopeChannelSlice } from '../api/client'

/** The extremes of a slice, and how many of its points carried a reading at all. */
export interface SliceExtremes {
  maxV: number
  minV: number
  /** Peak to peak. */
  ppV: number
}

/**
 * The extremes of one channel's slice, exact whether it came back as columns or as raw samples.
 *
 * <p>Null for a slice with nothing in it — a channel that was not captured, a record that has not
 * arrived — rather than a zero, which would frame the chart on a trace that does not exist and
 * would hand the 50 % button a level of zero as though it had measured one.</p>
 */
export function sliceExtremes(slice: ScopeChannelSlice | null | undefined): SliceExtremes | null {
  if (!slice) return null

  let maxV = Number.NEGATIVE_INFINITY
  let minV = Number.POSITIVE_INFINITY

  const consider = (value: number) => {
    if (!Number.isFinite(value)) return
    if (value > maxV) maxV = value
    if (value < minV) minV = value
  }

  if (slice.raw && slice.samples) {
    for (const sample of slice.samples) consider(sample)
  } else {
    // Both arrays, never one: the maxima alone read several dB quiet at a noise floor and would
    // frame the chart on half the trace.
    for (const value of slice.max ?? []) consider(value)
    for (const value of slice.min ?? []) consider(value)
  }

  if (!Number.isFinite(maxV) || !Number.isFinite(minV)) return null
  return { maxV, minV, ppV: maxV - minV }
}

/**
 * The extremes across every channel of a record — what Autofit frames and what the trigger's 50 %
 * button reads.
 *
 * <p>Across all of them rather than per channel, because phase 1 has <b>one</b> vertical setting
 * for the whole plot (see <code>voltAxis.ts</code>): fitting to the left channel alone would push
 * a louder right channel off the top of a window that claims to show both. Per-channel scales and
 * offsets are a later phase, and that is when this grows a channel argument.</p>
 */
export function recordExtremes(slices: readonly ScopeChannelSlice[] | null | undefined): SliceExtremes | null {
  if (!slices?.length) return null

  let maxV = Number.NEGATIVE_INFINITY
  let minV = Number.POSITIVE_INFINITY
  for (const slice of slices) {
    const extremes = sliceExtremes(slice)
    if (!extremes) continue
    maxV = Math.max(maxV, extremes.maxV)
    minV = Math.min(minV, extremes.minV)
  }

  if (!Number.isFinite(maxV) || !Number.isFinite(minV)) return null
  return { maxV, minV, ppV: maxV - minV }
}
