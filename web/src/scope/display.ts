import { parseAnnotations, type ChartAnnotation } from '../measurements/chartAnnotations'
import {
  SCOPE_INTENSITY_MODES,
  SCOPE_PERSISTENCE_RECORDS,
  type ScopeIntensityMode,
  type ScopePersistence,
} from './persistence'
import { DEFAULT_SCOPE_CURSOR_STYLE, isScopeCursorStyle, type ScopeCursorStyle } from './cursorOverlay'
import {
  DEFAULT_SCOPE_MATH_SPEC,
  SCOPE_MATH_OPERATIONS,
  mathSpecFromStored,
  type ScopeMathOperation,
  type ScopeMathSpec,
} from './math'
import { SCOPE_VIEWS, type ScopeView } from './xy'
import {
  DEFAULT_SCOPE_MATH_AXIS,
  snapMathPerDivision,
  type ScopeMathAxis,
} from './mathAxis'
import { SCOPE_DRAWINGS, type ScopeDrawing } from './interpolation'
import {
  DEFAULT_SCOPE_PATH,
  SCOPE_COUPLINGS,
  type ScopeCoupling,
  type ScopeSignalPathSettings,
} from './signalPath'
import { PALETTE_NAMES } from '../measurements/decay'
import { OSCILLOSCOPE_ANNOTATION_VIEW } from '../charts/annotationViews'
import type { OscilloscopeParameters } from '../api/client'
import { recordWindow } from './timeAxis'
import { DEFAULT_SCOPE_MEASUREMENTS, parseScopeMeasurementIds } from './measurementCatalogue'

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
/** The two pictures the plot under the measurement table draws. */
export type ScopeTrendView = 'Trend' | 'Distribution'

export const SCOPE_TREND_VIEWS: readonly ScopeTrendView[] = Object.freeze(['Trend', 'Distribution'])

export interface ScopeDisplay {
  voltsPerDivision: number
  voltOffsetV: number
  /**
   * Whether the V/div knob keeps itself fitted to what is drawn, rather than waiting to be pressed.
   *
   * <p>A mode of a control, stored like any other preference — see <code>scope/autoscale.ts</code>
   * for the band that decides when it actually moves, which is the whole of what a continuous
   * automatic needs over a press.</p>
   */
  autoscaleVolts: boolean
  timeFromS: number
  timeToS: number
  thickLines: boolean
  showDataPoints: boolean
  /** Whether the draggable trigger line and its hysteresis band are drawn over the trace. */
  showTriggerLevel: boolean
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
   * Which style the cursors are read in — one box of figures, or a value on each line.
   *
   * <p><b>A preference, where the cursors' own positions are not.</b> Where somebody happened to
   * leave a cursor while reading a measurement is a fact about that session; how they like their
   * readouts written is a setting like the trace weight, and resetting it on every measurement
   * switch would be the one of the two that is merely annoying.</p>
   */
  cursorStyle: ScopeCursorStyle
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
  /**
   * The two math channels — what each computes and on which inputs. See scope/math.ts.
   *
   * <p>A display preference rather than part of the measurement, so each derived trace is
   * recomputed whenever the record is drawn: that is what lets an operation be changed on a
   * reopened capture, where a stored derived waveform could not be.</p>
   */
  maths: [ScopeMathSpec, ScopeMathSpec]
  /**
   * Each math channel's own vertical axis — a per-division figure, a centre and an Autoscale, the
   * same three the volts axis has. See scope/mathAxis.ts, which says why the old auto-ranging
   * scale had to become a setting.
   *
   * <p>One per slot and not one shared, for the reason the two scales were separate to begin with:
   * two derived quantities are routinely in different units, so a division of one says nothing
   * about a division of the other.</p>
   */
  mathAxes: [ScopeMathAxis, ScopeMathAxis]
  /**
   * Which picture the chart draws — the waveforms against time, or one channel against the other.
   *
   * <p>A scope's own mode button rather than two charts side by side: the two are the same record
   * drawn two ways and only one of them can be read at a time, so a toggle keeps the chart its
   * full height. See scope/xy.ts.</p>
   */
  chartView: ScopeView
  /**
   * The display path the trace is read through.
   *
   * <p>A display preference, so it is stored with the measurement and the <b>stored samples are
   * never processed</b>: the path is a way of looking at a capture rather than a property of it,
   * and a saved result can be reopened and read a different way, or raw. See
   * scope/signalPath.ts, which also records what the removed in-path filter got wrong.</p>
   */
  path: ScopeSignalPathSettings
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
  /**
   * Which readings the readout under the chart shows, by their catalogue ids. See
   * scope/measurementCatalogue.ts.
   *
   * <p>A display preference like the pair of channel boxes: it is <em>which figures a reader is
   * watching</em>, which is part of how a measurement is read and so belongs with it. What those
   * figures have averaged to over the last ninety records is not stored and never could be — a
   * history of a run rather than of a capture, the persistence picture's own rule.</p>
   */
  measurements: string[]
  /** Whether that readout keeps statistics across records beside each value. */
  measurementStatistics: boolean
  /**
   * Which of those readings the plot under the table is of, by its row key — the catalogue id and
   * the trace it was read from, as <code>pkpk:ch0</code> or <code>phase:pair</code>.
   *
   * <p>A key naming a row that is no longer on offer falls back to the first one and is written
   * back, so the dropdown and the plot cannot disagree. Empty means "whatever is first", which is
   * what a measurement saved before the plot existed asks for.</p>
   */
  trendRow: string
  /** Whether that plot is the trend or the distribution. See scope/trend.ts and scope/distribution.ts. */
  trendView: ScopeTrendView
  /**
   * Whether the live trace is judged against a pass/fail mask built from the reference on the
   * chart. See scope/mask.ts.
   *
   * <p>Which reference is <b>not</b> stored: the mask is built from the first checked history item,
   * so there is no id to go stale against a deleted result or to name a different run after an
   * import. What is stored is the tolerance, which is a specification.</p>
   */
  maskOn: boolean
  /** The band's level tolerance, either side of the reference, in volts. */
  maskToleranceV: number
  /** And its time tolerance, in seconds. */
  maskToleranceS: number
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
  /**
   * Off. A knob that moves on its own is a thing a reader turns on when they want it: left on by
   * default it would overwrite a vertical somebody had just set, on a screen whose other two ways
   * of framing a trace — Autofit and Autoset — are both deliberate presses.
   */
  autoscaleVolts: false,
  timeFromS: DEFAULT_WINDOW.fromS,
  timeToS: DEFAULT_WINDOW.toS,
  /**
   * On. A one-pixel trace is a hairline on a dense modern panel, and the thing a reader is
   * following across the graticule is the trace -- this is the one display preference whose cost
   * is nothing and whose benefit is on every screen.
   */
  thickLines: true,
  showDataPoints: false,
  showTriggerLevel: true,
  showLeft: true,
  showRight: true,
  showResidual: false,
  /** Fixed, which is the Siglent's own default and never lands on the trace unless it is put there. */
  cursorStyle: DEFAULT_SCOPE_CURSOR_STYLE,
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
  /** Both off: a math channel is a pass over the window on every fetch, and most of what a scope
   *  is pointed at wants the two waveforms themselves. */
  maths: [{ ...DEFAULT_SCOPE_MATH_SPEC }, { ...DEFAULT_SCOPE_MATH_SPEC }] as [ScopeMathSpec, ScopeMathSpec],
  /** Both following the trace, which is what this axis has always done. See DEFAULT_SCOPE_MATH_AXIS. */
  mathAxes: [{ ...DEFAULT_SCOPE_MATH_AXIS }, { ...DEFAULT_SCOPE_MATH_AXIS }] as [
    ScopeMathAxis,
    ScopeMathAxis,
  ],
  /** The waveform, which is what a scope is for; X–Y is a view a reader switches to on purpose. */
  chartView: 'Waveform',
  /** DC: a scope shows what arrived until somebody asks it not to. */
  path: { ...DEFAULT_SCOPE_PATH },
  /**
   * Lines, which is what a scope has always drawn — and the reconstruction is what a reader turns
   * on when they are looking at something near the top of the band, where lines stop being true.
   */
  drawing: 'Linear',
  annotations: [],
  /** The three a scope is read for at a glance; the picker is one press away for the rest. */
  measurements: [...DEFAULT_SCOPE_MEASUREMENTS],
  /**
   * On. The argument for off was that five more columns are clutter for a reader who has not asked
   * — but the question they answer, "is this steady", is one a reader of a live trace is asking
   * whether or not they thought to tick a box, and the columns sit in a table that is already
   * there rather than taking anything off the chart.
   */
  measurementStatistics: true,
  /** Whatever the table's first row is, until a reader picks one. */
  trendRow: '',
  /**
   * The trend. It is the one of the two that answers a question a reader did not have to think of
   * first — "has this moved" — where a distribution is reached for once something has.
   */
  trendView: 'Trend',
  /** Off: a mask needs a golden run to compare against, which is a thing a reader sets up. */
  maskOn: false,
  /**
   * 50 mV and nothing in time.
   *
   * <p>A level tolerance is the half everybody expects and is safe to default; a time tolerance
   * opens the band to the whole height of every edge it reaches across, so a default above zero
   * would quietly make the mask pass things near an edge before anybody had asked it to.</p>
   */
  maskToleranceV: 0.05,
  maskToleranceS: 0,
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
/**
 * One stored math axis read back, field by field.
 *
 * <p>Each is guarded on its own rather than the three being taken together, so a document that
 * carries some of them — one written by a build between this change and a later one, or edited by
 * hand — keeps what it says and takes the default for the rest. The per-division figure is snapped
 * on the way in: a hand-edited 0.37 is a graticule nobody can count, which is the whole reason the
 * stored pair is a rung and a centre rather than two ends.</p>
 */
function readMathAxis(
  axis: ScopeMathAxis,
  perDivision: unknown,
  centre: unknown,
  autoscale: unknown,
): void {
  if (typeof perDivision === 'number' && Number.isFinite(perDivision) && perDivision > 0)
    axis.perDivision = snapMathPerDivision(perDivision)
  if (typeof centre === 'number' && Number.isFinite(centre)) axis.centre = centre
  if (typeof autoscale === 'boolean') axis.autoscale = autoscale
}

export function scopeDisplayFromParams(
  paramsJson: string | null | undefined,
  base: Readonly<ScopeDisplay> = DEFAULT_SCOPE_DISPLAY,
): ScopeDisplay {
  const display: ScopeDisplay = {
    ...base,
    annotations: [...base.annotations],
    path: { ...base.path },
    // Copied, not shared: the base is frozen and the restore below writes into this pair.
    maths: [{ ...base.maths[0] }, { ...base.maths[1] }],
    mathAxes: [{ ...base.mathAxes[0] }, { ...base.mathAxes[1] }],
  }
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
  if (typeof stored.autoscaleVolts === 'boolean') display.autoscaleVolts = stored.autoscaleVolts

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
  if (typeof stored.chartShowLeft === 'boolean') display.showLeft = stored.chartShowLeft
  if (typeof stored.chartShowRight === 'boolean') display.showRight = stored.chartShowRight
  if (typeof stored.chartShowResidual === 'boolean') display.showResidual = stored.chartShowResidual
  if (isScopeCursorStyle(stored.chartCursorStyle)) display.cursorStyle = stored.chartCursorStyle

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
  // The two math channels. Each is three stored fields, and a measurement saved before the sources
  // could be chosen carries only the operation -- which read A as the left input and B as the
  // right, so that is what an absent pair means. An operation whose operands were the other way
  // round (BMinusA, NegateB, dB/dt, integral B) comes back as its own tile with the sources
  // exchanged, so the trace is unchanged and nothing saved loses its meaning.
  if (
    typeof stored.chartMathOperation === 'string' &&
    SCOPE_MATH_OPERATIONS.some((o) => o.value === stored.chartMathOperation)
  )
    display.maths[0] = mathSpecFromStored(
      stored.chartMathOperation as ScopeMathOperation,
      stored.chartMathSourceA,
      stored.chartMathSourceB,
    )
  if (
    typeof stored.chartMath2Operation === 'string' &&
    SCOPE_MATH_OPERATIONS.some((o) => o.value === stored.chartMath2Operation)
  )
    display.maths[1] = mathSpecFromStored(
      stored.chartMath2Operation as ScopeMathOperation,
      stored.chartMath2SourceA,
      stored.chartMath2SourceB,
    )
  // The two math axes. Every field is read on its own, so a measurement saved before any of them
  // existed -- which is every one saved before 2026-11-22 -- comes back on the default, i.e. still
  // following its trace, and nothing that was on screen changes by being reopened.
  readMathAxis(display.mathAxes[0], stored.chartMathPerDivision, stored.chartMathCentre, stored.chartMathAutoscale)
  readMathAxis(
    display.mathAxes[1],
    stored.chartMath2PerDivision,
    stored.chartMath2Centre,
    stored.chartMath2Autoscale,
  )

  if (typeof stored.chartView === 'string' && SCOPE_VIEWS.some((v) => v.value === stored.chartView))
    display.chartView = stored.chartView as ScopeView

  // Validated against the table rather than cast, like the palettes: a name from a newer build,
  // or a hand-edited one, would otherwise reach the analyzer as a coupling that does not exist.
  if (typeof stored.chartCoupling === 'string' && SCOPE_COUPLINGS.some((c) => c.value === stored.chartCoupling))
    display.path.coupling = stored.chartCoupling as ScopeCoupling
  if (typeof stored.chartDrawing === 'string' && SCOPE_DRAWINGS.some((d) => d.value === stored.chartDrawing))
    display.drawing = stored.chartDrawing as ScopeDrawing

  display.annotations = parseAnnotations((stored as { chartAnnotations?: unknown }).chartAnnotations, {
    view: OSCILLOSCOPE_ANNOTATION_VIEW,
    xQuantity: 'ms',
    yQuantity: 'volt',
  })

  // Absent keeps the base's set -- a measurement saved before the readout existed opens on this
  // build's default rather than on an empty table. An empty list is kept empty, because that is a
  // reader who cleared it and not a measurement that never had one. Unknown ids are dropped by the
  // parser: a reading from a newer build has nothing here that can take it, and a row that is
  // always blank reads as a fault.
  const measurements = parseScopeMeasurementIds(stored.chartMeasurements)
  if (measurements) display.measurements = measurements
  if (typeof stored.chartMeasurementStatistics === 'boolean')
    display.measurementStatistics = stored.chartMeasurementStatistics

  // Not validated against the catalogue, unlike every other stored string here: a row key is an id
  // and a source ("pkpk:ch0"), and which sources exist depends on what the capture carries rather
  // than on any table. The panel's own fallback is what makes an unknown key harmless.
  if (typeof stored.chartTrendRow === 'string') display.trendRow = stored.chartTrendRow
  if (typeof stored.chartTrendView === 'string' && (SCOPE_TREND_VIEWS as string[]).includes(stored.chartTrendView))
    display.trendView = stored.chartTrendView as ScopeTrendView

  if (typeof stored.chartMaskOn === 'boolean') display.maskOn = stored.chartMaskOn
  // A negative tolerance is not a narrower band, it is no band — buildScopeMask reads it as zero
  // either way, and keeping it out of the stored display means the box cannot show one.
  if (typeof stored.chartMaskToleranceV === 'number' && stored.chartMaskToleranceV >= 0)
    display.maskToleranceV = stored.chartMaskToleranceV
  if (typeof stored.chartMaskToleranceS === 'number' && stored.chartMaskToleranceS >= 0)
    display.maskToleranceS = stored.chartMaskToleranceS

  return display
}

/** The display half of an oscilloscope type's <code>params_json</code>, as the save path writes it. */
export function scopeDisplayToParams(display: ScopeDisplay): Partial<OscilloscopeParameters> {
  return {
    chartVoltsPerDivision: display.voltsPerDivision,
    chartVoltOffsetV: display.voltOffsetV,
    autoscaleVolts: display.autoscaleVolts,
    chartTimeFromS: display.timeFromS,
    chartTimeToS: display.timeToS,
    chartThickLines: display.thickLines,
    chartShowDataPoints: display.showDataPoints,
    chartShowTriggerLevel: display.showTriggerLevel,
    chartShowLeft: display.showLeft,
    chartShowRight: display.showRight,
    chartShowResidual: display.showResidual,
    chartCursorStyle: display.cursorStyle,
    chartPersistence: display.persistence,
    chartIntensityMode: display.intensityMode,
    chartIntensityPalette: display.intensityPalette,
    chartMathOperation: display.maths[0].operation,
    chartMathSourceA: display.maths[0].sourceA,
    chartMathSourceB: display.maths[0].sourceB,
    chartMath2Operation: display.maths[1].operation,
    chartMath2SourceA: display.maths[1].sourceA,
    chartMath2SourceB: display.maths[1].sourceB,
    chartMathPerDivision: display.mathAxes[0].perDivision,
    chartMathCentre: display.mathAxes[0].centre,
    chartMathAutoscale: display.mathAxes[0].autoscale,
    chartMath2PerDivision: display.mathAxes[1].perDivision,
    chartMath2Centre: display.mathAxes[1].centre,
    chartMath2Autoscale: display.mathAxes[1].autoscale,
    chartView: display.chartView,
    chartCoupling: display.path.coupling,
    chartDrawing: display.drawing,
    chartMeasurements: [...display.measurements],
    chartMeasurementStatistics: display.measurementStatistics,
    chartTrendRow: display.trendRow,
    chartTrendView: display.trendView,
    chartMaskOn: display.maskOn,
    chartMaskToleranceV: display.maskToleranceV,
    chartMaskToleranceS: display.maskToleranceS,
  }
}

/** A per-channel pair — the display rail's two boxes, or the two inputs a record carries. */
export interface ScopeChannelPair {
  left: boolean
  right: boolean
}

/**
 * Which inputs a record carries — which is to say what the measurement is capturing.
 *
 * <p>The record endpoint answers for the channels the measurement asked for and no others, so a
 * record's channel list <i>is</i> the Acquisition panel's pair as everything downstream sees it.
 * Before the first capture there is no record to ask and both are offered, which is what lets a
 * reference drawn on an empty chart show everything it has.</p>
 */
export function capturedScopeChannels(
  channels: readonly { channel: number }[] | null | undefined,
): ScopeChannelPair {
  if (!channels?.length) return { left: true, right: true }
  return {
    left: channels.some((c) => c.channel === 0),
    right: channels.some((c) => c.channel === 1),
  }
}

/**
 * Whether a channel is drawn: the display rail's box <b>and</b> the measurement capturing it.
 *
 * <p><b>Both halves, and the second is the one a reference needs.</b> A live slice can only be a
 * channel that was captured, so for the live traces the second test is a no-op — but a
 * <i>reference</i> is somebody else's capture, and a stored two-channel one carries a right input
 * whatever this measurement is doing. The rail's Right box stays <b>ticked</b> while a left-only
 * measurement runs (it is disabled rather than cleared, a channel that is not captured having
 * nothing to hide), so gating a reference on the rail alone draws that stored right channel beside
 * a live left one — an orange trace with no live counterpart, and no control on the screen that
 * turns it off, since the one box that would is the disabled one. Reported 2026-11-18.</p>
 *
 * <p>One function for both loops so the two cannot drift: the live filter and the reference filter
 * were two spellings of one rule, and only one of them was corrected.</p>
 */
export function scopeChannelDrawn(
  channel: number,
  shown: ScopeChannelPair,
  captured: ScopeChannelPair,
): boolean {
  return channel === 1 ? shown.right && captured.right : shown.left && captured.left
}
