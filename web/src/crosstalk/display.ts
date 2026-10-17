import { parseAnnotations, type ChartAnnotation } from '../measurements/chartAnnotations'
import type { CrosstalkParameters } from '../api/client'

/**
 * The crosstalk chart's display state — the subset of FrequencyResponseChart's props a crosstalk
 * plot is a preference over. App.vue holds these as refs for the screen; the report builds one
 * from a stored type's `params_json` to render the same chart headless, and both go through
 * `crosstalkDisplayFromParams` so a type restores the same way on screen and on paper.
 */
export interface CrosstalkDisplay {
  thickLines: boolean
  showDataPoints: boolean
  yTopDb: number
  yBottomDb: number
  xFromHz: number
  xToHz: number
  autofitOnDone: boolean
  /**
   * The notes drawn on this measurement's charts, as stored -- across every view, so one report
   * render can print a table per picture. See measurements/chartAnnotations.ts.
   */
  annotations: ChartAnnotation[]
}

/** A fresh crosstalk type's window: 0 down to −120 dB — where any amplifier's crosstalk lives — over the audio band and a bit. */
export const DEFAULT_CROSSTALK_DISPLAY: Readonly<CrosstalkDisplay> = Object.freeze({
  annotations: [],
  thickLines: true,
  showDataPoints: true,
  yTopDb: 0,
  yBottomDb: -120,
  xFromHz: 10,
  xToHz: 100000,
  // Off, like every other screen's: a chart that refits its own axes the moment a run ends
  // moves the window out from under whoever set it (Joost, 2026-10-17). The checkbox is still there.
  autofitOnDone: false,
})

type Stored = Partial<CrosstalkParameters>

/** The display state a crosstalk type's `params_json` restores, over `base`; every field guarded, unparsable JSON restores nothing. */
export function crosstalkDisplayFromParams(
  paramsJson: string | null | undefined,
  base: Readonly<CrosstalkDisplay> = DEFAULT_CROSSTALK_DISPLAY,
): CrosstalkDisplay {
  const display: CrosstalkDisplay = { ...base }
  if (!paramsJson) return display

  let stored: Stored
  try {
    stored = JSON.parse(paramsJson) as Stored
  } catch {
    return display
  }
  if (!stored || typeof stored !== 'object') return display

  if (typeof stored.chartThickLines === 'boolean') display.thickLines = stored.chartThickLines
  if (typeof stored.chartShowDataPoints === 'boolean') display.showDataPoints = stored.chartShowDataPoints
  if (typeof stored.chartYTopDb === 'number') display.yTopDb = stored.chartYTopDb
  if (typeof stored.chartYBottomDb === 'number') display.yBottomDb = stored.chartYBottomDb
  if (typeof stored.chartXFromHz === 'number' && stored.chartXFromHz > 0) display.xFromHz = stored.chartXFromHz
  if (typeof stored.chartXToHz === 'number' && stored.chartXToHz > 0) display.xToHz = stored.chartXToHz
  if (typeof stored.chartAutofitOnDone === 'boolean') display.autofitOnDone = stored.chartAutofitOnDone

  display.annotations = parseAnnotations(
    (stored as { chartAnnotations?: unknown }).chartAnnotations,
    { view: 'Crosstalk', xQuantity: 'hz', yQuantity: 'db' },
  )

  return display
}

/** The `chart*` fields a crosstalk type's `params_json` carries for `display`, to merge over the panel's run settings. */
export function crosstalkDisplayToParams(display: Readonly<CrosstalkDisplay>): Pick<
  CrosstalkParameters,
  'chartThickLines' | 'chartShowDataPoints' | 'chartYTopDb' | 'chartYBottomDb' | 'chartXFromHz' | 'chartXToHz' | 'chartAutofitOnDone'
> {
  return {
    chartThickLines: display.thickLines,
    chartShowDataPoints: display.showDataPoints,
    chartYTopDb: display.yTopDb,
    chartYBottomDb: display.yBottomDb,
    chartXFromHz: display.xFromHz,
    chartXToHz: display.xToHz,
    chartAutofitOnDone: display.autofitOnDone,
  }
}
