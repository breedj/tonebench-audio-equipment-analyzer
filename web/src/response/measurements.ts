import type { ResponsePhaseKindName, ResponseView } from '../api/client'
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

/**
 * The curve the delay readings and the chart's delay series come from. Delay belongs to the
 * measurement, not to the view: a chirp estimated per trace (`PerTrace`) carries one on whichever
 * curve is primary, while a stepped sine or a right-referenced chirp only ever measured it on the
 * gain curve — the same rule as the chart's phase series, so the panel never quotes a different
 * curve from the one drawn.
 */
export function delayResponseTrace(
  traces: ResponseTrace[],
  view: ResponseView,
  showLeft: boolean,
  phaseKind: ResponsePhaseKindName,
): ResponseTrace | null {
  if (phaseKind === 'None') return null
  if (phaseKind === 'PerTrace') return primaryResponseTrace(traces, view, showLeft)
  return traces.find((t) => t.kind === 'Gain') ?? null
}

export interface DelayExtreme {
  s: number
  freqHz: number
}

export interface DelayReadings {
  /** False when the trace carries no delay curves — a result saved before they were recorded. */
  recorded: boolean
  /** Median over the reference band's columns, or the column nearest the peak under a peak reference. Seconds. */
  groupDelayAtReferenceS: number | null
  phaseDelayAtReferenceS: number | null
  /** Over every column of the trace. */
  groupDelayMin: DelayExtreme | null
  groupDelayMax: DelayExtreme | null
}

const NO_DELAY_READINGS: DelayReadings = {
  recorded: false,
  groupDelayAtReferenceS: null,
  phaseDelayAtReferenceS: null,
  groupDelayMin: null,
  groupDelayMax: null,
}

/**
 * The Measurements panel's delay rows.
 *
 * The reference reading is taken where the corners are: the median over the bandwidth reference
 * band under a passband reference, the single column nearest the peak (in log f) under a peak
 * reference or where the band fell back to it. A median for the same reason the level reference is
 * one — a mean is dragged by any single wild column. The band comes from the caller because the
 * trace does not carry its edges, only the mode that was applied.
 */
export function delayReadings(trace: ResponseTrace, band: { lowHz: number; highHz: number }): DelayReadings {
  const group = trace.groupDelayS
  const phase = trace.phaseDelayS
  if (!group || !phase || group.length === 0 || group.length !== trace.freqHz.length || phase.length !== trace.freqHz.length)
    return NO_DELAY_READINGS

  let columns: number[] = []
  const usePeak = trace.referenceMode === 'Peak' || trace.referenceFellBackToPeak
  if (!usePeak) {
    for (let i = 0; i < trace.freqHz.length; i++) {
      if (trace.freqHz[i] >= band.lowHz && trace.freqHz[i] <= band.highHz) columns.push(i)
    }
  }
  if (columns.length === 0) columns = [nearestColumn(trace.freqHz, trace.peakFreqHz)]

  let min: DelayExtreme | null = null
  let max: DelayExtreme | null = null
  for (let i = 0; i < group.length; i++) {
    const s = group[i]
    if (!Number.isFinite(s)) continue
    if (min === null || s < min.s) min = { s, freqHz: trace.freqHz[i] }
    if (max === null || s > max.s) max = { s, freqHz: trace.freqHz[i] }
  }

  return {
    recorded: true,
    groupDelayAtReferenceS: median(columns.map((i) => group[i])),
    phaseDelayAtReferenceS: median(columns.map((i) => phase[i])),
    groupDelayMin: min,
    groupDelayMax: max,
  }
}

function nearestColumn(freqHz: number[], targetHz: number): number {
  let best = 0
  let bestDistance = Number.POSITIVE_INFINITY
  const target = Math.log(Math.max(targetHz, Number.MIN_VALUE))
  for (let i = 0; i < freqHz.length; i++) {
    const distance = Math.abs(Math.log(Math.max(freqHz[i], Number.MIN_VALUE)) - target)
    if (distance < bestDistance) {
      bestDistance = distance
      best = i
    }
  }
  return best
}

function median(values: number[]): number | null {
  const finite = values.filter((v) => Number.isFinite(v)).sort((a, b) => a - b)
  if (finite.length === 0) return null
  const mid = finite.length >> 1
  return finite.length % 2 === 1 ? finite[mid] : (finite[mid - 1] + finite[mid]) / 2
}
