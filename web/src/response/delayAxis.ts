import type { ResponseDelayView } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'

/**
 * The delay axis' arithmetic — which unit it reads in, which curve it draws, what grid an autofit
 * snaps to — kept out of the chart and the panel because nothing in a `.vue` file is testable under
 * the node environment vitest runs in, and an axis that silently lands on the wrong unit reads as a
 * DUT a thousand times slower than it is.
 *
 * The window is held in **microseconds** everywhere (props, params_json, the panel's inputs) and
 * only converted for display: one stored unit means a saved window cannot be reloaded in the other.
 */
export type DelayUnit = 'µs' | 'ms'

/** The 1-2-5 series an autofit step and the axis ticks are drawn from, in the display unit. */
export const DELAY_INCREMENTS = [
  0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500,
  1000, 2000, 5000, 10000, 20000, 50000, 100000, 200000, 500000,
]

export const DELAY_MIN_US = -1e6
export const DELAY_MAX_US = 1e6

/**
 * Milliseconds once either edge of the window reaches a millisecond. Decided by the window rather
 * than by the data so the unit cannot flip mid-run as a curve settles, and so the Top/Bottom inputs
 * agree with the axis they set.
 */
export function chooseDelayUnit(topUs: number, bottomUs: number): DelayUnit {
  return Math.max(Math.abs(topUs), Math.abs(bottomUs)) >= 1000 ? 'ms' : 'µs'
}

/** Seconds → the display unit. */
export function delayScale(unit: DelayUnit): number {
  return unit === 'ms' ? 1e3 : 1e6
}

/** Microseconds → the display unit. */
export function usToUnit(us: number, unit: DelayUnit): number {
  return unit === 'ms' ? us / 1000 : us
}

export function unitToUs(value: number, unit: DelayUnit): number {
  return unit === 'ms' ? value * 1000 : value
}

/**
 * The grid step an autofit snaps a window of `spanUs` to: the largest 1-2-5 step that still gives
 * at least four divisions, so a fitted window shows four to ten ticks whatever its size.
 */
export function delayFitStepUs(spanUs: number): number {
  if (!Number.isFinite(spanUs) || spanUs <= 0) return 1
  let step = DELAY_INCREMENTS[0]
  for (const candidate of DELAY_INCREMENTS) {
    if (spanUs / candidate >= 4) step = candidate
    else break
  }
  return step
}

export function delayLabel(view: ResponseDelayView, phaseIsDifference = false): string {
  if (view === 'Group') return phaseIsDifference ? 'Group delay difference' : 'Group delay'
  if (view === 'Phase') return phaseIsDifference ? 'Phase delay difference' : 'Phase delay'
  return 'Delay'
}

/** The selected curve of a trace in seconds, or null when the trace never recorded it. */
export function delayValuesOf(trace: ResponseTrace | null, view: ResponseDelayView): number[] | null {
  if (!trace || view === 'None') return null
  return view === 'Group' ? trace.groupDelayS : trace.phaseDelayS
}

/** Whether a stored value names a delay view; anything else (an older params_json) reads as 'None'. */
export function isDelayView(value: unknown): value is ResponseDelayView {
  return value === 'None' || value === 'Group' || value === 'Phase'
}
