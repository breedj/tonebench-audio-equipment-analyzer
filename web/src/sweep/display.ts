import type { ThdVsAmplitudeParameters, ThdVsFrequencyParameters } from '../api/client'
import { isAmplitudeSweepKind } from '../measurements/kinds'
import type { SweepXAxis } from './xAxis'

/**
 * The THD sweep chart's display state — every prop `ThdSweepChart` takes that is a preference
 * rather than data, plus the amplitude screen's axis choice and its per-axis windows. App.vue holds
 * these as refs for the screen; the report builds one from a stored measurement type's `params_json`
 * to render the same chart headless, and both go through `sweepDisplayFromParams`.
 */
export interface SweepDisplay {
  unit: 'percent' | 'db'
  showNoiseFloor: boolean
  showMagnitude: boolean
  showThd: boolean
  showD2: boolean
  showD3: boolean
  showD4: boolean
  showD5: boolean
  showD6Plus: boolean
  showLeft: boolean
  showRight: boolean
  thickLines: boolean
  showDataPoints: boolean
  yMaxPercent: number
  yMinPercent: number
  yTopDb: number
  yBottomDb: number
  /** The frequency sweep's x window, in hertz. */
  xFromHz: number
  xToHz: number
  /** The amplitude sweep's axis, and one window per axis in that axis' own unit. */
  amplitudeXAxis: SweepXAxis
  amplitudeBounds: Record<string, { from: number; to: number }>
}

/** What a fresh sweep measurement shows before anything is stored — the literals App.vue starts from. */
export function defaultSweepDisplay(): SweepDisplay {
  return {
    unit: 'percent',
    showNoiseFloor: false,
    showMagnitude: false,
    showThd: true,
    showD2: true,
    showD3: true,
    showD4: false,
    showD5: false,
    showD6Plus: false,
    showLeft: true,
    showRight: false,
    thickLines: true,
    showDataPoints: false,
    yMaxPercent: 10,
    yMinPercent: 0.00001,
    yTopDb: 20,
    yBottomDb: -160,
    xFromHz: 10,
    xToHz: 20000,
    amplitudeXAxis: 'outputVolt',
    amplitudeBounds: {
      outputVolt: { from: 0.001, to: 10 },
      outputWatt: { from: 0.000001, to: 100 },
      inputVolt: { from: 0.001, to: 10 },
    },
  }
}

type StoredDisplay = Partial<ThdVsFrequencyParameters> & Partial<ThdVsAmplitudeParameters>

/**
 * The display state a sweep measurement type's `params_json` restores, over `base`. The two records
 * share every chart field but the x pair: the frequency sweep stores chartXFromHz/chartXToHz in
 * hertz, the amplitude one chartXFrom/chartXTo in the selected axis' own unit, and each lands on the
 * state that owns it. Every field is guarded, so a type saved before one existed keeps `base`'s.
 */
export function sweepDisplayFromParams(paramsJson: string | null | undefined, base: SweepDisplay = defaultSweepDisplay()): SweepDisplay {
  const display: SweepDisplay = {
    ...base,
    amplitudeBounds: Object.fromEntries(Object.entries(base.amplitudeBounds).map(([k, v]) => [k, { ...v }])),
  }
  if (!paramsJson) return display

  let stored: StoredDisplay
  try {
    stored = JSON.parse(paramsJson) as StoredDisplay
  } catch {
    return display
  }
  if (!stored || typeof stored !== 'object') return display

  if (stored.chartXAxis) display.amplitudeXAxis = stored.chartXAxis
  const bounds = (display.amplitudeBounds[display.amplitudeXAxis] ??= { from: 0.001, to: 10 })
  if (typeof stored.chartXFrom === 'number' && stored.chartXFrom > 0) bounds.from = stored.chartXFrom
  if (typeof stored.chartXTo === 'number' && stored.chartXTo > 0) bounds.to = stored.chartXTo
  if (stored.chartUnit === 'percent' || stored.chartUnit === 'db') display.unit = stored.chartUnit
  if (typeof stored.chartShowNoiseFloor === 'boolean') display.showNoiseFloor = stored.chartShowNoiseFloor
  if (typeof stored.chartShowMagnitude === 'boolean') display.showMagnitude = stored.chartShowMagnitude
  if (typeof stored.chartShowThd === 'boolean') display.showThd = stored.chartShowThd
  if (typeof stored.chartShowD2 === 'boolean') display.showD2 = stored.chartShowD2
  if (typeof stored.chartShowD3 === 'boolean') display.showD3 = stored.chartShowD3
  if (typeof stored.chartShowD4 === 'boolean') display.showD4 = stored.chartShowD4
  if (typeof stored.chartShowD5 === 'boolean') display.showD5 = stored.chartShowD5
  if (typeof stored.chartShowD6Plus === 'boolean') display.showD6Plus = stored.chartShowD6Plus
  if (typeof stored.chartShowLeft === 'boolean') display.showLeft = stored.chartShowLeft
  if (typeof stored.chartShowRight === 'boolean') display.showRight = stored.chartShowRight
  if (typeof stored.chartThickLines === 'boolean') display.thickLines = stored.chartThickLines
  if (typeof stored.chartShowDataPoints === 'boolean') display.showDataPoints = stored.chartShowDataPoints
  if (typeof stored.chartYMaxPercent === 'number') display.yMaxPercent = stored.chartYMaxPercent
  if (typeof stored.chartYMinPercent === 'number') display.yMinPercent = stored.chartYMinPercent
  if (typeof stored.chartXFromHz === 'number') display.xFromHz = stored.chartXFromHz
  if (typeof stored.chartXToHz === 'number') display.xToHz = stored.chartXToHz
  if (typeof stored.chartYTopDb === 'number') display.yTopDb = stored.chartYTopDb
  if (typeof stored.chartYBottomDb === 'number') display.yBottomDb = stored.chartYBottomDb

  return display
}

/** The chart's x axis and window for a kind: the amplitude screen's choice, or plain frequency in hertz. */
export function sweepChartWindow(display: SweepDisplay, kind: string): { xAxis: SweepXAxis; xFrom: number; xTo: number } {
  if (isAmplitudeSweepKind(kind)) {
    const bounds = display.amplitudeBounds[display.amplitudeXAxis] ?? { from: 0.001, to: 10 }
    return { xAxis: display.amplitudeXAxis, xFrom: bounds.from, xTo: bounds.to }
  }
  return { xAxis: 'frequency', xFrom: display.xFromHz, xTo: display.xToHz }
}
