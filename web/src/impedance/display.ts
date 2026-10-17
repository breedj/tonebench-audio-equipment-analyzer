import { parseAnnotations, type ChartAnnotation } from '../measurements/chartAnnotations'
import type { ImpedanceParameters, ImpedanceView, ImpedanceZScale } from '../api/client'

/**
 * The impedance chart's display state — every prop `ImpedanceChart` takes that is a preference
 * rather than data, plus which projection is on screen. App.vue holds these as refs; the report
 * builds one from a stored measurement type's `params_json` to render the same chart headless, and
 * both go through `impedanceDisplayFromParams`.
 */
export interface ImpedanceDisplay {
  view: ImpedanceView
  showPhase: boolean
  showCoherence: boolean
  showShortCal: boolean
  thickLines: boolean
  showDataPoints: boolean
  zScale: ImpedanceZScale
  zTopOhms: number
  zBottomOhms: number
  xFromHz: number
  xToHz: number
  phaseTopDeg: number
  phaseBottomDeg: number
  /**
   * The notes drawn on this measurement's charts, as stored -- across every view, so one report
   * render can print a table per picture. See measurements/chartAnnotations.ts.
   */
  annotations: ChartAnnotation[]
}

/** What a fresh impedance measurement shows before anything is stored — the literals App.vue starts from. */
export const DEFAULT_IMPEDANCE_DISPLAY: Readonly<ImpedanceDisplay> = Object.freeze({
  annotations: [],
  view: 'Bode',
  showPhase: true,
  showCoherence: false,
  showShortCal: false,
  thickLines: false,
  showDataPoints: false,
  zScale: 'Linear',
  zTopOhms: 100,
  zBottomOhms: 1,
  xFromHz: 10,
  xToHz: 20000,
  phaseTopDeg: 90,
  phaseBottomDeg: -90,
})

/**
 * The display state an impedance type's `params_json` restores, over `base`. Every field is
 * guarded, so a type saved before one existed keeps `base`'s; the ohm bounds are checked against the
 * scale, since a log axis cannot show zero.
 */
export function impedanceDisplayFromParams(
  paramsJson: string | null | undefined,
  base: Readonly<ImpedanceDisplay> = DEFAULT_IMPEDANCE_DISPLAY,
): ImpedanceDisplay {
  const display: ImpedanceDisplay = { ...base }
  if (!paramsJson) return display

  let stored: Partial<ImpedanceParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<ImpedanceParameters>
  } catch {
    return display
  }
  if (!stored || typeof stored !== 'object') return display

  if (stored.chartView === 'Bode' || stored.chartView === 'Nyquist') display.view = stored.chartView
  if (typeof stored.chartShowPhase === 'boolean') display.showPhase = stored.chartShowPhase
  if (typeof stored.chartShowCoherence === 'boolean') display.showCoherence = stored.chartShowCoherence
  if (typeof stored.chartShowShortCal === 'boolean') display.showShortCal = stored.chartShowShortCal
  if (typeof stored.chartThickLines === 'boolean') display.thickLines = stored.chartThickLines
  if (typeof stored.chartShowDataPoints === 'boolean') display.showDataPoints = stored.chartShowDataPoints
  display.zScale = stored.chartZScale === 'Log' ? 'Log' : 'Linear'
  // The top must stay above zero on either scale; the bottom may sit on zero only on the linear one.
  if (typeof stored.chartZTopOhms === 'number' && stored.chartZTopOhms > 0) display.zTopOhms = stored.chartZTopOhms
  if (
    typeof stored.chartZBottomOhms === 'number' &&
    (display.zScale === 'Linear' ? stored.chartZBottomOhms >= 0 : stored.chartZBottomOhms > 0)
  )
    display.zBottomOhms = stored.chartZBottomOhms
  if (typeof stored.chartXFromHz === 'number' && stored.chartXFromHz > 0) display.xFromHz = stored.chartXFromHz
  if (typeof stored.chartXToHz === 'number' && stored.chartXToHz > 0) display.xToHz = stored.chartXToHz
  if (typeof stored.chartPhaseTopDeg === 'number') display.phaseTopDeg = stored.chartPhaseTopDeg
  if (typeof stored.chartPhaseBottomDeg === 'number') display.phaseBottomDeg = stored.chartPhaseBottomDeg

  display.annotations = parseAnnotations(
    (stored as { chartAnnotations?: unknown }).chartAnnotations,
    { view: 'Bode', xQuantity: 'hz', yQuantity: 'ohm' },
  )

  return display
}
