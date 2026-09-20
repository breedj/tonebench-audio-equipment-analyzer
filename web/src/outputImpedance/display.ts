import type { ImpedanceZScale, OutputImpedanceParameters } from '../api/client'

/**
 * The output-impedance chart's display state — the subset of ImpedanceChart's props this screen is
 * a preference over. App.vue holds these as refs; the report builds one from a stored type's
 * `params_json` to render the same chart headless, and both go through
 * `outputImpedanceDisplayFromParams` so a type restores the same way on screen and on paper.
 */
export interface OutputImpedanceDisplay {
  zTopOhms: number
  zBottomOhms: number
  zScale: ImpedanceZScale
  showDampingFactor: boolean
  /** Meaningful only for a referenced run; the panel disables the toggle otherwise. */
  showPhase: boolean
  xFromHz: number
  xToHz: number
  phaseTopDeg: number
  phaseBottomDeg: number
  thickLines: boolean
  showDataPoints: boolean
  autofitOnDone: boolean
}

/** A fresh type's window: 10 mΩ–10 Ω on a log axis — a solid-state amplifier's coil and a tube amplifier's transformer both fit — with the damping factor beside it. */
export const DEFAULT_OUTPUT_IMPEDANCE_DISPLAY: Readonly<OutputImpedanceDisplay> = Object.freeze({
  zTopOhms: 10,
  zBottomOhms: 0.01,
  zScale: 'Log' as ImpedanceZScale,
  showDampingFactor: true,
  showPhase: false,
  xFromHz: 10,
  xToHz: 50000,
  phaseTopDeg: 90,
  phaseBottomDeg: -90,
  thickLines: true,
  showDataPoints: true,
  autofitOnDone: true,
})

type Stored = Partial<OutputImpedanceParameters>

/** The display state an output-impedance type's `params_json` restores, over `base`; every field guarded, unparsable JSON restores nothing. */
export function outputImpedanceDisplayFromParams(
  paramsJson: string | null | undefined,
  base: Readonly<OutputImpedanceDisplay> = DEFAULT_OUTPUT_IMPEDANCE_DISPLAY,
): OutputImpedanceDisplay {
  const display: OutputImpedanceDisplay = { ...base }
  if (!paramsJson) return display

  let stored: Stored
  try {
    stored = JSON.parse(paramsJson) as Stored
  } catch {
    return display
  }
  if (!stored || typeof stored !== 'object') return display

  if (stored.chartZScale === 'Linear' || stored.chartZScale === 'Log') display.zScale = stored.chartZScale
  if (typeof stored.chartZTopOhms === 'number' && stored.chartZTopOhms > 0) display.zTopOhms = stored.chartZTopOhms
  if (typeof stored.chartZBottomOhms === 'number' && stored.chartZBottomOhms >= 0) display.zBottomOhms = stored.chartZBottomOhms
  // A log axis cannot start at zero; a stored linear window switched to log gets a floor instead of a broken scale.
  if (display.zScale === 'Log' && !(display.zBottomOhms > 0)) display.zBottomOhms = base.zBottomOhms > 0 ? base.zBottomOhms : 0.01
  if (typeof stored.chartShowDampingFactor === 'boolean') display.showDampingFactor = stored.chartShowDampingFactor
  if (typeof stored.chartShowPhase === 'boolean') display.showPhase = stored.chartShowPhase
  if (typeof stored.chartXFromHz === 'number' && stored.chartXFromHz > 0) display.xFromHz = stored.chartXFromHz
  if (typeof stored.chartXToHz === 'number' && stored.chartXToHz > 0) display.xToHz = stored.chartXToHz
  if (typeof stored.chartPhaseTopDeg === 'number') display.phaseTopDeg = stored.chartPhaseTopDeg
  if (typeof stored.chartPhaseBottomDeg === 'number') display.phaseBottomDeg = stored.chartPhaseBottomDeg
  if (typeof stored.chartThickLines === 'boolean') display.thickLines = stored.chartThickLines
  if (typeof stored.chartShowDataPoints === 'boolean') display.showDataPoints = stored.chartShowDataPoints
  if (typeof stored.chartAutofitOnDone === 'boolean') display.autofitOnDone = stored.chartAutofitOnDone

  return display
}

/** The `chart*` fields an output-impedance type's `params_json` carries for `display`, to merge over the panel's run settings. */
export function outputImpedanceDisplayToParams(display: Readonly<OutputImpedanceDisplay>): Pick<
  OutputImpedanceParameters,
  | 'chartZTopOhms'
  | 'chartZBottomOhms'
  | 'chartZScale'
  | 'chartShowDampingFactor'
  | 'chartShowPhase'
  | 'chartXFromHz'
  | 'chartXToHz'
  | 'chartPhaseTopDeg'
  | 'chartPhaseBottomDeg'
  | 'chartThickLines'
  | 'chartShowDataPoints'
  | 'chartAutofitOnDone'
> {
  return {
    chartZTopOhms: display.zTopOhms,
    chartZBottomOhms: display.zBottomOhms,
    chartZScale: display.zScale,
    chartShowDampingFactor: display.showDampingFactor,
    chartShowPhase: display.showPhase,
    chartXFromHz: display.xFromHz,
    chartXToHz: display.xToHz,
    chartPhaseTopDeg: display.phaseTopDeg,
    chartPhaseBottomDeg: display.phaseBottomDeg,
    chartThickLines: display.thickLines,
    chartShowDataPoints: display.showDataPoints,
    chartAutofitOnDone: display.autofitOnDone,
  }
}
