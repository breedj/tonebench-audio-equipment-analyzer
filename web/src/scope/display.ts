import { parseAnnotations, type ChartAnnotation } from '../measurements/chartAnnotations'
import {
  SCOPE_INTENSITY_MODES,
  SCOPE_PERSISTENCE_RECORDS,
  type ScopeIntensityMode,
  type ScopePersistence,
} from './persistence'
import { SCOPE_MATH_OPERATIONS, type ScopeMathOperation } from './math'
import { SCOPE_VIEWS, type ScopeView } from './xy'
import { SCOPE_DRAWINGS, type ScopeDrawing } from './interpolation'
import {
  DEFAULT_SCOPE_FILTER,
  MAX_FILTER_Q,
  MIN_FILTER_Q,
  SCOPE_COUPLINGS,
  SCOPE_FILTER_KINDS,
  SCOPE_FILTER_ORDERS,
  type ScopeCoupling,
  type ScopeFilterKind,
  type ScopeFilterSettings,
} from './filters'
import { PALETTE_NAMES } from '../measurements/decay'
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
  /**
   * Whether each captured input is drawn.
   *
   * <p>Separate from the Acquisition panel's own Left and Right boxes, which decide what is
   * <em>captured</em> — a channel that is not captured has no samples to hide. This pair is a
   * scope's per-channel display button: a second trace can be taken out of the way while it goes
   * on being captured, and while it goes on being the trigger source.</p>
   */
  showLeft: boolean
  showRight: boolean
  /**
   * Whether the THD residual is drawn as a second trace — the window with its fitted fundamental
   * subtracted, so what is left is the distortion and the noise.
   *
   * <p>Off by default, and asked for rather than always sent: it costs a least-squares sine fit per
   * channel per fetch, and on a record with no fundamental there is nothing to remove.</p>
   */
  showResidual: boolean
  /**
   * How long the chart keeps each record's trace — the digital-phosphor picture, where how often
   * the trace passes through a pixel decides how it is painted. See scope/persistence.ts.
   *
   * <p>The <em>setting</em> is a display preference and is stored with the measurement; what it
   * accumulates is not — that is a history of a display rather than of a capture, so it is thrown
   * away whenever a pixel would stop meaning the same time and the same level.</p>
   */
  persistence: ScopePersistence
  /** Whether that picture is painted in the channel's own colour or through a colour map. */
  intensityMode: ScopeIntensityMode
  /** Which colour map, when it is one — the decay surfaces' own palettes, so a colour means one thing app-wide. */
  intensityPalette: import('../api/client').DecayPalette
  /**
   * Which math channel is drawn — a waveform derived from the two captured ones. See scope/math.ts.
   *
   * <p>A display preference rather than part of the measurement, so it is stored with the type and
   * the derived trace is recomputed whenever the record is drawn: that is what lets the operation
   * be changed on a reopened capture, where a stored derived waveform could not be.</p>
   */
  mathOperation: ScopeMathOperation
  /**
   * Which picture the chart draws — the waveforms against time, or one channel against the other.
   *
   * <p>A scope's own mode button rather than two charts side by side: the two are the same record
   * drawn two ways and only one of them can be read at a time, so a toggle keeps the chart its
   * full height. See scope/xy.ts.</p>
   */
  chartView: ScopeView
  /**
   * The display path the trace is read through — the coupling and the in-path filter.
   *
   * <p>A display preference, so it is stored with the measurement and the <b>stored samples are
   * never filtered</b>: a filter is a way of looking at a capture rather than a property of it, and
   * a saved result can be reopened and read through a different one, or none. See
   * scope/filters.ts.</p>
   */
  filter: ScopeFilterSettings
  /**
   * How the chart joins one sample to the next — lines, the band-limited reconstruction, or
   * nothing at all. See scope/interpolation.ts.
   *
   * <p>Only reaches the picture where the window is tight enough for the analyzer to have sent the
   * samples themselves: which values fell where inside a column is exactly what decimation throws
   * away, so on a wide window the other two fall back to lines.</p>
   */
  drawing: ScopeDrawing
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
  showLeft: true,
  showRight: true,
  showResidual: false,
  /**
   * Off by default. Persistence is what a reader turns on while hunting something intermittent; on
   * a steady signal it draws the same trace over itself and buys nothing but a slower redraw.
   */
  persistence: 'Off',
  intensityMode: 'Single',
  intensityPalette: 'Jet',
  /**
   * Off by default, and asked for rather than always sent: a math trace is a pass over the window
   * on every fetch, and most of what a scope is pointed at wants the two waveforms themselves.
   */
  mathOperation: 'Off',
  /** The waveform, which is what a scope is for; X–Y is a view a reader switches to on purpose. */
  chartView: 'Waveform',
  /** DC and no filter: a scope shows what arrived until somebody asks it not to. */
  filter: { ...DEFAULT_SCOPE_FILTER },
  /**
   * Lines, which is what a scope has always drawn — and the reconstruction is what a reader turns
   * on when they are looking at something near the top of the band, where lines stop being true.
   */
  drawing: 'Linear',
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
  const display: ScopeDisplay = { ...base, annotations: [...base.annotations], filter: { ...base.filter } }
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
  if (typeof stored.chartShowLeft === 'boolean') display.showLeft = stored.chartShowLeft
  if (typeof stored.chartShowRight === 'boolean') display.showRight = stored.chartShowRight
  if (typeof stored.chartShowResidual === 'boolean') display.showResidual = stored.chartShowResidual

  // Validated against the tables rather than cast: a stored string from a newer build, or a
  // hand-edited one, would otherwise reach the painter as a palette that does not exist.
  if (typeof stored.chartPersistence === 'string' && stored.chartPersistence in SCOPE_PERSISTENCE_RECORDS)
    display.persistence = stored.chartPersistence as ScopePersistence
  if (
    typeof stored.chartIntensityMode === 'string' &&
    SCOPE_INTENSITY_MODES.some((m) => m.value === stored.chartIntensityMode)
  )
    display.intensityMode = stored.chartIntensityMode as ScopeIntensityMode
  if (
    typeof stored.chartIntensityPalette === 'string' &&
    (PALETTE_NAMES as string[]).includes(stored.chartIntensityPalette)
  )
    display.intensityPalette = stored.chartIntensityPalette as import('../api/client').DecayPalette
  if (
    typeof stored.chartMathOperation === 'string' &&
    SCOPE_MATH_OPERATIONS.some((o) => o.value === stored.chartMathOperation)
  )
    display.mathOperation = stored.chartMathOperation as ScopeMathOperation
  if (typeof stored.chartView === 'string' && SCOPE_VIEWS.some((v) => v.value === stored.chartView))
    display.chartView = stored.chartView as ScopeView

  // Validated against the tables rather than cast, like the palettes: a name from a newer build,
  // or a hand-edited one, would otherwise reach the analyzer as a filter that does not exist.
  if (typeof stored.chartCoupling === 'string' && SCOPE_COUPLINGS.some((c) => c.value === stored.chartCoupling))
    display.filter.coupling = stored.chartCoupling as ScopeCoupling
  if (typeof stored.chartFilterKind === 'string' && SCOPE_FILTER_KINDS.some((k) => k.value === stored.chartFilterKind))
    display.filter.kind = stored.chartFilterKind as ScopeFilterKind
  if (typeof stored.chartFilterHz === 'number' && stored.chartFilterHz > 0)
    display.filter.frequencyHz = stored.chartFilterHz
  if (typeof stored.chartFilterQ === 'number' && stored.chartFilterQ >= MIN_FILTER_Q && stored.chartFilterQ <= MAX_FILTER_Q)
    display.filter.q = stored.chartFilterQ
  if (typeof stored.chartFilterOrder === 'number' && SCOPE_FILTER_ORDERS.includes(stored.chartFilterOrder))
    display.filter.order = stored.chartFilterOrder
  if (typeof stored.chartDrawing === 'string' && SCOPE_DRAWINGS.some((d) => d.value === stored.chartDrawing))
    display.drawing = stored.chartDrawing as ScopeDrawing

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
    chartShowLeft: display.showLeft,
    chartShowRight: display.showRight,
    chartShowResidual: display.showResidual,
    chartPersistence: display.persistence,
    chartIntensityMode: display.intensityMode,
    chartIntensityPalette: display.intensityPalette,
    chartMathOperation: display.mathOperation,
    chartView: display.chartView,
    chartCoupling: display.filter.coupling,
    chartFilterKind: display.filter.kind,
    chartFilterHz: display.filter.frequencyHz,
    chartFilterQ: display.filter.q,
    chartFilterOrder: display.filter.order,
    chartDrawing: display.drawing,
  }
}
