import { parseAnnotations, type ChartAnnotation } from '../measurements/chartAnnotations'
import { OSCILLOSCOPE_ANNOTATION_VIEW } from '../charts/annotationViews'
import type { OscilloscopeParameters } from '../api/client'
import { recordWindow } from './timeAxis'

/**
 * The oscilloscope chart's display state — every prop the chart takes that is a preference rather
 * than data. App.vue holds these as refs; a report builds one from a stored measurement type's
 * <code>params_json</code> to render the same chart headless, and both go through
 * <code>scopeDisplayFromParams</code>.
 *
 * <p><b>The vertical pair is V/div and a centre, not a top and a bottom</b> — see
 * <code>voltAxis.ts</code> for why a scope's window is derived rather than stored. The horizontal
 * pair is the two ends, in <b>seconds relative to the trigger</b> and negative to its left, which is
 * the convention the decay surfaces' head start already uses and the one the server's
 * <code>fromS</code>/<code>toS</code> carry.</p>
 */
export interface ScopeDisplay {
  voltsPerDivision: number
  voltOffsetV: number
  timeFromS: number
  timeToS: number
  thickLines: boolean
  showDataPoints: boolean
  /** Whether the draggable trigger line and its hysteresis band are drawn over the trace. */
  showTriggerLevel: boolean
  /** Whether a finished capture fits both axes to itself. */
  autofitOnDone: boolean
  /** The notes drawn on this measurement's chart, as stored. See measurements/chartAnnotations.ts. */
  annotations: ChartAnnotation[]
}

/**
 * The window a chart's "Set axis" hands up - in the seconds and volts the display panel's boxes
 * hold, never in the milliseconds the chart happens to plot.
 *
 * <p>Here rather than in the chart because a `<script setup>` block may not carry ES exports, and
 * because the conversion belongs to the one side that knows about both units.</p>
 */
export interface ScopeAxisWindow {
  fromS: number
  toS: number
  topV: number
  bottomV: number
}

/**
 * What a fresh oscilloscope measurement shows before anything is stored.
 *
 * <p>The time window is the 20 ms record at 25 % pre-trigger that
 * <code>DefaultOscilloscopeParametersJson</code> seeds, so a brand-new measurement's axis is
 * already the one its first capture will land on. 500 mV/div frames a line-level signal across
 * about five of the eight divisions. <b>Autofit is on</b>, unlike the FFT screen's: a scope is
 * pointed at an unknown signal, and a window left over from the last one draws a flat line or an
 * empty plot, which reads as a dead probe.</p>
 */
const DEFAULT_WINDOW = recordWindow(0.02, 0.25)

export const DEFAULT_SCOPE_DISPLAY: Readonly<ScopeDisplay> = Object.freeze({
  voltsPerDivision: 0.5,
  voltOffsetV: 0,
  timeFromS: DEFAULT_WINDOW.fromS,
  timeToS: DEFAULT_WINDOW.toS,
  thickLines: false,
  showDataPoints: false,
  showTriggerLevel: true,
  autofitOnDone: true,
  annotations: [],
})

/**
 * The display state an oscilloscope type's <code>params_json</code> restores, over
 * <paramref name="base"/>.
 *
 * <p><b>One argument, and the caller passes nothing else.</b> <paramref name="base"/> defaults to
 * this kind's own frozen default and App.vue must leave it alone: handing it the state currently on
 * screen is how a new measurement comes to open wearing the outgoing one's view, which the dirty
 * watcher then saves under it. <code>storedDisplayDefaults.test.ts</code> reads App.vue as text to
 * enforce exactly that.</p>
 *
 * <p>Every field is guarded, so a type saved before one existed keeps the default rather than a
 * <code>NaN</code> or an <code>undefined</code> — and the time pair is only taken as a pair, since a
 * half-restored window is a window with no span.</p>
 */
export function scopeDisplayFromParams(
  paramsJson: string | null | undefined,
  base: Readonly<ScopeDisplay> = DEFAULT_SCOPE_DISPLAY,
): ScopeDisplay {
  const display: ScopeDisplay = { ...base, annotations: [...base.annotations] }
  if (!paramsJson) return display

  let stored: Partial<OscilloscopeParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<OscilloscopeParameters>
  } catch {
    return display
  }
  if (!stored || typeof stored !== 'object') return display

  if (typeof stored.chartVoltsPerDivision === 'number' && stored.chartVoltsPerDivision > 0)
    display.voltsPerDivision = stored.chartVoltsPerDivision
  if (typeof stored.chartVoltOffsetV === 'number' && Number.isFinite(stored.chartVoltOffsetV))
    display.voltOffsetV = stored.chartVoltOffsetV

  // The pair or neither: a stored "from" with no "to" would be read against this build's default
  // "to", which is a window the measurement never had.
  if (
    typeof stored.chartTimeFromS === 'number' &&
    typeof stored.chartTimeToS === 'number' &&
    Number.isFinite(stored.chartTimeFromS) &&
    Number.isFinite(stored.chartTimeToS) &&
    stored.chartTimeToS > stored.chartTimeFromS
  ) {
    display.timeFromS = stored.chartTimeFromS
    display.timeToS = stored.chartTimeToS
  } else if (typeof stored.recordSeconds === 'number' && typeof stored.preTriggerFraction === 'number') {
    // No window stored, but the record the measurement takes is: draw the axis that capture lands
    // on rather than this build's default 20 ms, which would be the wrong axis for the right data.
    const window = recordWindow(stored.recordSeconds, stored.preTriggerFraction)
    display.timeFromS = window.fromS
    display.timeToS = window.toS
  }

  if (typeof stored.chartThickLines === 'boolean') display.thickLines = stored.chartThickLines
  if (typeof stored.chartShowDataPoints === 'boolean') display.showDataPoints = stored.chartShowDataPoints
  if (typeof stored.chartShowTriggerLevel === 'boolean') display.showTriggerLevel = stored.chartShowTriggerLevel
  if (typeof stored.chartAutofitOnDone === 'boolean') display.autofitOnDone = stored.chartAutofitOnDone

  display.annotations = parseAnnotations((stored as { chartAnnotations?: unknown }).chartAnnotations, {
    view: OSCILLOSCOPE_ANNOTATION_VIEW,
    xQuantity: 'ms',
    yQuantity: 'volt',
  })

  return display
}

/** The display half of an oscilloscope type's <code>params_json</code>, as the save path writes it. */
export function scopeDisplayToParams(display: ScopeDisplay): Partial<OscilloscopeParameters> {
  return {
    chartVoltsPerDivision: display.voltsPerDivision,
    chartVoltOffsetV: display.voltOffsetV,
    chartTimeFromS: display.timeFromS,
    chartTimeToS: display.timeToS,
    chartThickLines: display.thickLines,
    chartShowDataPoints: display.showDataPoints,
    chartShowTriggerLevel: display.showTriggerLevel,
    chartAutofitOnDone: display.autofitOnDone,
  }
}
