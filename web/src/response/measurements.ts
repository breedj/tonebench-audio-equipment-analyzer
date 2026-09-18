import type { ResponseView } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'

/**
 * The curve the Measurements panel's numbers describe — the same one the chart draws its bandwidth
 * markers for. In gain view that is the gain curve; in the dBV view the shown input, left first.
 * Shared by the panel and the report so the two never quote different curves.
 */
export function primaryResponseTrace(traces: ResponseTrace[], view: ResponseView, showLeft: boolean): ResponseTrace | null {
  if (view === 'Gain') return traces.find((t) => t.kind === 'Gain') ?? null
  return (showLeft ? traces.find((t) => t.kind === 'Left') : null)
    ?? traces.find((t) => t.kind === 'Right')
    ?? traces.find((t) => t.kind === 'Left')
    ?? null
}

/**
 * The previous app re-captioned the peak with the view (`frmFrequencyResponse.cs:746-781`), and the
 * distinction is real: in the dBV view the peak is an output level, in the gain view it is a gain.
 */
export function peakLabel(view: ResponseView): string {
  return view === 'Gain' ? 'Max. gain' : 'Max. amplitude'
}

/**
 * What the two bandwidth rows were measured down from, spelled out — because the same table reads
 * completely differently depending on it, and a reader comparing two saved runs has no other way to
 * tell.
 *
 * Absent (null) on a result saved before the reference was recorded. Those were peak-referenced, but
 * the row is left blank rather than back-filled: it says what the stored result says and no more.
 */
export function referenceLabel(trace: ResponseTrace): string | null {
  if (trace.referenceMode === null) return null
  if (trace.referenceMode === 'Peak') return 'Peak'
  return trace.referenceFellBackToPeak ? 'Peak (band not measured)' : 'Passband median'
}

/** The two explanatory notes the panel shows under the table, when they apply. */
export const REFERENCE_FELL_BACK_NOTE =
  'The reference band lies outside the measured range, so these corners are referenced to the peak instead.'

export const CLAMPED_CORNER_NOTE =
  'A corner marked < or > was never reached inside the measured band — that is where the search stopped, not where the response rolls off.'
