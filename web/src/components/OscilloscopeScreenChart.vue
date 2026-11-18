<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { ScopeChannelSlice, ScopeRecordResponse, ScopeTriggerKind, ScopeTriggerSource } from '../api/client'
import type { OverlayEntry } from '../composables/useMeasurements'
import { overlayRunIndex, runDash } from '../measurements/sweepOverlayStyles'
import { runTint } from '../charts/runTint'
import { onScopeGrid } from '../scope/references'
import { accumulationBadge, type ScopeAccumulation } from '../scope/acquisition'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { CHART_EXPORT_HEADER_PX, composeChartPng, nextPaint, type ChartExportOptions } from '../charts/exportChart'
import ExpandToggle from './ExpandToggle.vue'
import ChartZoomControls from './ChartZoomControls.vue'
import { useAppTheme } from '../composables/useAppTheme'
import { useZoomBounds } from '../composables/useZoomBounds'
import type { ZoomWindow } from '../charts/zoomHistory'
import type { AnnotationStyle, ChartAnnotation } from '../measurements/chartAnnotations'
import { useAnnotationOverlay } from '../charts/useAnnotationOverlay'
import { annotationTheme } from '../charts/annotationPainter'
import {
  annotationOverlayRect,
  annotationsPlugin,
  attachAnnotationListeners,
  cssAnnotationPlot,
  finishBoxGesture,
  syncBoxDragMode,
  type BoxDragResting,
  type UplotAnnotationAxes,
} from '../charts/uplotAnnotations'
import ChartAnnotationOverlay from './ChartAnnotationOverlay.vue'
import { exportGridTheme } from '../charts/palette'
import { OSCILLOSCOPE_ANNOTATION_VIEW } from '../charts/annotationViews'
import {
  formatPerDivision,
  formatScopeTime,
  recordWindow,
  scopeTimeScale,
  toChartMs,
  fromChartMs,
} from '../scope/timeAxis'
import { formatVolts, formatVoltsPerDivision, roundVoltsForWindow, voltWindow } from '../scope/voltAxis'
import type { ScopeAxisWindow } from '../scope/display'
import { triggerChannel, triggerIndicator, windowIndicator } from '../scope/triggerSpec'
import {
  hexRgb,
  intensityPixels,
  intensitySignature,
  persistenceDecay,
  persistenceDraws,
  ScopeIntensityBuffer,
  type ScopeIntensityMode,
  type ScopePersistence,
} from '../scope/persistence'
import type { DecayPalette } from '../api/client'
import {
  drawingJoins,
  drawingNeedsSamples,
  sincResample,
  sincResampleX,
  type ScopeDrawing,
} from '../scope/interpolation'
import { DEFAULT_SCOPE_CURSORS, type ScopeCursors } from '../scope/cursors'
import {
  formatMathValue,
  mathAxisLabel,
  mathDraws,
  mathLabel,
  mathUnitOf,
  type ScopeMathOperation,
} from '../scope/math'

/**
 * The oscilloscope screen's chart — a waveform on a <b>linear</b> time axis with the trigger at
 * t = 0 and negative time to its left, over a graticule of eight vertical and ten horizontal
 * divisions.
 *
 * <p><b>This is a second chart and not a change to <code>OscilloscopeChart.vue</code></b>, which
 * stays exactly as it is as the FFT screen's glance at the waveform behind a spectrum. The two have
 * almost nothing in common beyond drawing a trace against time: that one is handed a fixed 2048
 * samples and triggers in the browser on a zero crossing; this one draws a server-triggered record
 * of up to a second, with a window into it, a trigger level that is dragged rather than typed, and
 * the min/max band a decimated record has to be drawn with.</p>
 *
 * <p><b>Both extremes of every column are drawn</b> — the max as the line, the min as a band under
 * it — for the reason the spectrum and impulse charts do it, which bites hardest here: a glitch can
 * be one sample wide, and a column showing one side of the waveform would hide exactly the thing a
 * scope is being pointed at. Once a window is tight enough the server sends the samples themselves
 * instead, and then there is no band to draw because there is nothing left to summarise.</p>
 */

const annotationArmed = defineModel<AnnotationStyle | null>('annotationArmed', { default: null })
/** The measurement's notes. See FrequencyResponseChart for how one list serves a kind's charts. */
const annotationList = defineModel<ChartAnnotation[]>('annotations', { default: () => [] })

/**
 * The trigger level, in DUT volts — <b>a model, because the chart writes it</b>.
 *
 * <p>Dragging the line is how a trigger level is set on a bench instrument; the number box in the
 * trigger panel is the precise fallback, and the two are one value rather than two that have to be
 * kept in step.</p>
 */
const triggerLevelV = defineModel<number>('triggerLevelV', { default: 0 })

/**
 * The cursors — <b>a model, for the same reason the trigger level is one</b>: they are dragged on
 * the chart, and the panel's number boxes are the precise fallback rather than the other way round.
 *
 * <p>One object rather than four numbers, so the chart and the panel can watch them as a unit and
 * so a replacement is one write. Replaced, never mutated: a mutated model does not emit.</p>
 */
const cursors = defineModel<ScopeCursors>('cursors', { default: () => ({ ...DEFAULT_SCOPE_CURSORS }) })

// `annotationNumbers` is defaulted rather than left absent: Vue casts an absent Boolean prop with
// no default to `false`, which silently turned the ordinal badges off on every chart but the FFT's.
const props = withDefaults(defineProps<{
  annotationNumbers?: boolean
  /** The slice on screen, or null before anything has been captured. */
  record: ScopeRecordResponse | null
  voltsPerDivision: number
  voltOffsetV: number
  /** The window, in seconds relative to the trigger. Negative to its left. */
  timeFromS: number
  timeToS: number
  thickLines: boolean
  showDataPoints: boolean
  showTriggerLevel: boolean
  triggerHysteresisV: number
  triggerSource: ScopeTriggerSource
  /**
   * The staged trigger kind, and the window trigger's other bound.
   *
   * <p>The chart needs the kind to know <em>what</em> to draw at the level: an edge trigger's line
   * with its hysteresis band around it, or a window trigger's band between its two bounds. Both
   * default, so a caller that has no trigger to describe — a report of a measurement saved before
   * the advanced kinds existed — still draws the line it always did.</p>
   */
  triggerKind?: ScopeTriggerKind
  triggerLevelLowV?: number
  /** The record the settings ask for, so an empty chart can draw the axis the next capture lands on. */
  recordSeconds: number
  preTriggerFraction: number
  /** True while the run is armed or capturing — what makes the trace read as live rather than held. */
  running?: boolean
  /** Whether each captured input is drawn. The display rail's pair; see scope/display.ts. */
  showLeft?: boolean
  showRight?: boolean
  /** How long the chart keeps each record's trace. See scope/persistence.ts. */
  persistence?: ScopePersistence
  intensityMode?: ScopeIntensityMode
  intensityPalette?: DecayPalette
  /**
   * Which math channel to draw, and the derived trace itself. See scope/math.ts.
   *
   * <p>The operation decides the axis label and how the readings are written; the trace arrives on
   * the record, built server-side on the same geometry as the waveforms so every series on the
   * chart shares one x array.</p>
   */
  mathOperation?: ScopeMathOperation
  /**
   * What the display path is doing to the trace, in the words the rail uses — or null for a bare
   * record. Shown as a badge, because a filter changes every reading beside it.
   */
  filterNote?: string | null
  /** How one sample is joined to the next. See scope/interpolation.ts. */
  drawing?: ScopeDrawing
  /** Which accumulation the run asked for — what the badge is about. See scope/acquisition.ts. */
  accumulation?: ScopeAccumulation
  /** How many records it is working toward, for the badge while an average fills. */
  averageCount?: number
  /**
   * Checked history items drawn as reference traces — stored captures, cut to this same window by
   * the analyzer so they arrive in the live record's own shape.
   *
   * <p>Which is the point of the arrangement: a reference is drawn by the code that draws the
   * trace, so the two cannot disagree about decimation, about which channels are shown, or about
   * what the display path did to them.</p>
   */
  references?: OverlayEntry[]
  /**
   * Anything outside this chart that makes an accumulated picture stop meaning what it meant — in
   * practice the open measurement. It joins the signature rather than being watched for, so a
   * switch cannot be forgotten at a call site.
   */
  persistenceKey?: string | number
}>(), {
  annotationNumbers: true,
  running: false,
  showLeft: true,
  showRight: true,
  persistence: 'Off',
  intensityMode: 'Single',
  intensityPalette: 'Jet',
  mathOperation: 'Off',
  filterNote: null,
  drawing: 'Linear',
  accumulation: 'Normal',
  averageCount: 1,
  references: () => [],
  persistenceKey: 0,
})

const emit = defineEmits<{
  axisWindow: [window: ScopeAxisWindow]
  /** The level drag has ended: the parent re-arms once, rather than once per pointer move. */
  triggerLevelCommitted: []
  /**
   * A cursor drag has ended, or a box was typed into: the parent gates the measurements once.
   *
   * <p>Not watched upstream, deliberately. The positions are written live so the lines and the
   * readout cannot disagree mid-drag, and the gate is a <em>fetch</em> — a watcher on the pair
   * would ask the server for a window per pointer move.</p>
   */
  cursorsCommitted: []
  /**
   * The window this chart is drawing, after anything that moved it.
   *
   * <p>The server cuts the record to the window it is asked for and sends the <b>waveform itself</b>
   * once that window holds no more samples than there are columns to draw it in — which is the
   * whole of what makes a rise time or an edge believable. So something has to tell it what is on
   * screen, and the only thing that knows is the chart: a drag-zoom moves uPlot's scales in place
   * and the display panel's From/To boxes never hear about it. Reported here rather than watched
   * upstream for exactly that reason, which is `SpectrumChart.reportViewport`'s own.</p>
   */
  viewport: [window: ScopeViewport]
}>()

/** What `viewport` carries: the window on screen in seconds, and the width to cut it into. */
export interface ScopeViewport {
  fromS: number
  toS: number
  /** The plot's own width in pixels, so a column is about a pixel and the raw floor is honest. */
  columns: number
}

const { isDark } = useAppTheme()

const root = ref<HTMLElement | null>(null)
const container = ref<HTMLDivElement | null>(null)
const chartHeight = useResultChartHeight(root, container)
const plot = shallowRef<uPlot | null>(null)

/** The channel colours the FFT screen's waveform panel already uses, so one signal is one colour app-wide. */
const CHANNEL_COLORS = ['#3b82f6', '#f97316'] as const

// The cursor teal is neither channel's colour and not the trigger's amber, which is the whole
// requirement: a line on this chart has to say what kind of line it is before it says where it is.
const THEME = {
  dark: { axis: '#a3a3a3', grid: '#404040', gridWidth: 1, trigger: '#a3a3a3', marker: '#f59e0b', cursor: '#2dd4bf' },
  light: { axis: '#525252', grid: '#e5e5e5', gridWidth: 1, trigger: '#525252', marker: '#b45309', cursor: '#0f766e' },
} as const

type Theme = (typeof THEME)[keyof typeof THEME]

function theme(): Theme {
  return isDark.value ? THEME.dark : THEME.light
}

/** One channel ready to draw: the line, the band under it when there is one, and what it is called. */
interface ChannelSeries {
  index: number
  label: string
  color: string
  /** The max of every column, or the samples themselves. */
  line: (number | null)[]
  /** The min of every column, or null for a raw slice — there is nothing left to summarise. */
  band: (number | null)[] | null
  raw: boolean
  /** True for the residual, which is drawn thinner and dashed so it is never read as a trace. */
  residual?: boolean
  /**
   * Which y scale it is drawn against. The waveforms and the residual share <code>y</code>, the
   * volts axis; the math trace has <b>its own</b>, and that is a decision rather than a
   * convenience.
   *
   * <p>Two reasons, and either alone settles it. A derived quantity is frequently not in volts at
   * all — A × B is V², a derivative V/s — so drawing it against a volts axis would put a figure
   * on a scale that means something else. And even in volts it is routinely orders smaller than
   * what it came from: a differential probe's whole point is the millivolts of difference between
   * two ten-volt signals, which on the waveforms' own axis is a flat line on zero.</p>
   */
  scale?: string
  /** True for the math trace, which is excluded from the haze and from the band's volts reading. */
  math?: boolean
  /** True for a stored capture drawn behind the live one — dashed, tinted and thin. */
  reference?: boolean
  /** The dash that tells one reference from another, and from the live trace, in a report with no colour. */
  dash?: number[]
}

/**
 * The residual's colours, one per channel.
 *
 * <p>A <b>lighter shade of the channel's own</b> rather than a colour of its own: it is that
 * channel's waveform with its fundamental taken out, so it has to read as belonging to it. The
 * dashing is what says it is derived rather than measured.</p>
 */
const RESIDUAL_COLORS = ['#93c5fd', '#fdba74'] as const

/**
 * The math trace's colour.
 *
 * <p>A third hue rather than a shade of either channel's, because it belongs to neither — A − B
 * is as much the right input's as the left's, and painting it blue would read as a second left
 * trace. Violet is the app's own third accent, and the one the phase curves already use for a
 * quantity drawn against its own axis.</p>
 */
const MATH_COLOR = '#a855f7'

/**
 * How far a reference's colour is pulled toward its run's own hue, and how much of the trace's
 * weight it keeps.
 *
 * <p><b>Dashed first, tinted second</b>, which is the impedance chart's own order and for its
 * reason: the dash is what the legend draws, what survives a black-and-white report and what
 * matches a run across two channels, where the hue is the quicker read on screen. Thin, because a
 * reference is there to be compared against rather than read.</p>
 */
const REFERENCE_TINT = 0.55

/** The scale the math trace is drawn against, which is its own. See ChannelSeries.scale. */
const MATH_SCALE = 'm'

/** The x array every series shares, in the milliseconds this chart plots. */
function sliceXMs(slice: ScopeChannelSlice, count: number): number[] {
  const x = new Array<number>(count)
  const x0 = toChartMs(slice.x0)
  const dx = toChartMs(slice.dx)
  for (let i = 0; i < count; i++) x[i] = x0 + i * dx
  return x
}

function sliceLength(slice: ScopeChannelSlice): number {
  if (slice.raw) return slice.samples?.length ?? 0
  return Math.min(slice.max?.length ?? 0, slice.min?.length ?? 0)
}

/**
 * What the plot draws.
 *
 * <p>Every channel is cut from the same window by the server and so shares one x array — which is
 * also what uPlot requires. The <b>longest</b> slice is what it is built from rather than the first:
 * a channel that came back short would otherwise silently decide the axis for the one that did not.
 * </p>
 */
const series = computed(() => {
  const record = props.record
  // Only the inputs the display rail is showing. What arrives is already only what the measurement
  // captures — the record endpoint answers for those alone — so this is the second of the two
  // gates and the one a reader turns.
  const drawn = (record?.channels ?? []).filter((slice) =>
    slice.channel === 1 ? props.showRight : props.showLeft,
  )
  // The references, resolved before the x array is chosen: with no live record they are the whole
  // of what is on the chart, so they have to be able to decide its geometry. Each is a stored
  // capture the analyzer cut to this same window.
  const references: { label: string; slice: ScopeChannelSlice; run: number }[] = []
  props.references.forEach((entry, index) => {
    // One behind the live trace when there is one, taking its place when there is not -- the sweep
    // chart's own rule, so a run's dash means the same thing on both screens.
    const run = overlayRunIndex(index, drawn.length > 0)
    for (const slice of entry.scopeRecord?.channels ?? []) {
      if (slice.channel === 1 ? !props.showRight : !props.showLeft) continue
      if (sliceLength(slice) === 0) continue
      references.push({
        label: `${entry.detail.name} · ${slice.channel === 0 ? 'Left' : 'Right'}`,
        slice,
        run,
      })
    }
  })

  if (!drawn.length && !references.length) {
    return { xMs: [] as number[], channels: [] as ChannelSeries[] }
  }

  // The x array is the live record's wherever there is one, so the trace being measured is never
  // the one that gets resampled; with nothing live, the longest reference decides it.
  const geometry = drawn.length ? drawn : references.map((reference) => reference.slice)
  let longest: ScopeChannelSlice | null = null
  let count = 0
  for (const slice of geometry) {
    const length = sliceLength(slice)
    if (length > count) {
      count = length
      longest = slice
    }
  }
  if (!longest || count === 0) return { xMs: [] as number[], channels: [] as ChannelSeries[] }

  const xMs = sliceXMs(longest, count)
  const channels: ChannelSeries[] = []

  // The references FIRST, so the live trace paints over them: uPlot draws its series in order, and
  // what is being read is the capture that just arrived -- a stored one drawn on top of it would
  // hide the very trace the screen exists for. Before the reconstruction too, so they go through
  // it as well: a reference drawn with straight lines beside a reconstructed live trace would
  // differ by the drawing rather than by the measurement.
  //
  // Mapped onto the grid by TIME and not by index: a reference is a different capture, taken at
  // its own sample rate, so a 48 kS/s one beside a 192 kS/s live record holds a quarter as many
  // samples over the same window. See scope/references.ts.
  const grid = { x0S: longest.x0, dxS: longest.dx, count }
  for (const reference of references) {
    const slice = reference.slice
    const source = { x0S: slice.x0, dxS: slice.dx, length: sliceLength(slice) }
    channels.push({
      index: slice.channel,
      label: reference.label,
      color: runTint(CHANNEL_COLORS[slice.channel] ?? CHANNEL_COLORS[0], reference.run, REFERENCE_TINT),
      line: slice.raw
        ? onScopeGrid(slice.samples, source, grid, 'interpolate')
        : onScopeGrid(slice.max, source, grid, 'nearest'),
      // Its min/max band only where it is the one reference on the chart -- the spectrum chart's
      // own rule (soleBandOverlay) and for its reason: a stored trace plotted as maxima alone
      // reads several dB quiet at the floor, so the band is worth drawing, and three translucent
      // bands over each other are a wash rather than three readings.
      band: slice.raw || references.length > 1 ? null : onScopeGrid(slice.min, source, grid, 'nearest'),
      raw: slice.raw,
      reference: true,
      dash: runDash(reference.run) ?? undefined,
    })
  }

  for (const slice of drawn) {
    const length = sliceLength(slice)
    const pad = (values: readonly number[] | null | undefined): (number | null)[] => {
      const out = new Array<number | null>(count).fill(null)
      for (let i = 0; i < length; i++) out[i] = values?.[i] ?? null
      return out
    }
    channels.push({
      index: slice.channel,
      label: slice.channel === 0 ? 'Left' : 'Right',
      color: CHANNEL_COLORS[slice.channel] ?? CHANNEL_COLORS[0],
      line: slice.raw ? pad(slice.samples) : pad(slice.max),
      band: slice.raw ? null : pad(slice.min),
      raw: slice.raw,
    })

    // The residual, on the same x array by construction: the server builds it from the very slice
    // above, so it is raw exactly where the waveform is and has the same column count where it is
    // not. It arrives only when the display rail asks and only where there was a fundamental to
    // remove, so an absent one is a state rather than a failure.
    const left = slice.residual
    if (left) {
      channels.push({
        index: slice.channel,
        label: `${slice.channel === 0 ? 'Left' : 'Right'} residual`,
        color: RESIDUAL_COLORS[slice.channel] ?? RESIDUAL_COLORS[0],
        line: slice.raw ? pad(left.samples) : pad(left.max),
        band: slice.raw ? null : pad(left.min),
        raw: slice.raw,
        residual: true,
      })
    }
  }

  // The math trace last, so it draws over the waveforms it was derived from -- and on its own
  // scale, which is what keeps a millivolt difference between two ten-volt signals visible.
  const math = record?.math
  if (math && mathDraws(props.mathOperation)) {
    const length = math.samples?.length ?? Math.min(math.max?.length ?? 0, math.min?.length ?? 0)
    const pad = (values: readonly number[] | null | undefined): (number | null)[] => {
      const out = new Array<number | null>(count).fill(null)
      for (let i = 0; i < Math.min(length, count); i++) out[i] = values?.[i] ?? null
      return out
    }
    const raw = math.samples !== null
    channels.push({
      index: -1,
      label: mathLabel(props.mathOperation),
      color: MATH_COLOR,
      line: raw ? pad(math.samples) : pad(math.max),
      band: raw ? null : pad(math.min),
      raw,
      scale: MATH_SCALE,
      math: true,
    })
  }

  // The band-limited reconstruction, applied last so every trace on the chart -- the waveforms,
  // the residual, the math channel -- lands on one x array. Only where the slice is raw: on
  // min/max columns the samples are gone, and which values fell where inside a column is exactly
  // what the decimation left out, so there is nothing to reconstruct from.
  if (drawingNeedsSamples(props.drawing) && channels.length > 0 && channels.every((c) => c.raw)) {
    return {
      xMs: sincResampleX(xMs),
      channels: channels.map((channel) => ({ ...channel, line: sincResample(channel.line) })),
    }
  }

  return { xMs, channels }
})

/**
 * Whether the chart is drawing the samples alone.
 *
 * <p>Only where it has them: on a decimated slice a "sample" is a column's extreme, so a dotted
 * picture of one would be a picture of the decimation. There the mode falls back to lines and the
 * rail says so.</p>
 */
const dotsOnly = computed(
  () => props.drawing === 'Dots' && !drawingJoins(props.drawing) && props.record?.channels?.[0]?.raw === true,
)

/**
 * Whether a math trace is on the chart.
 *
 * <p>Both halves: the rail has to ask for one <em>and</em> the server has to have been able to
 * build it — it answers null where the operation needs an input this measurement does not
 * capture. So the axis appears with the trace rather than with the setting, and an axis labelled
 * for a quantity nothing is drawing never takes 72 px off the plot.</p>
 */
const mathDrawn = computed(() => mathDraws(props.mathOperation) && !!props.record?.math)

/** The window the chart shows, in the milliseconds it plots. */
const xWindowMs = computed(() => ({ min: toChartMs(props.timeFromS), max: toChartMs(props.timeToS) }))

/** The span the time formatter reads its unit from — what the window shows, not what the record holds. */
const spanMs = computed(() => Math.abs(xWindowMs.value.max - xWindowMs.value.min))

/** The two ends of the volts axis, derived from the V/div and the centre. See scope/voltAxis.ts. */
const yWindow = computed(() => voltWindow(props.voltsPerDivision, props.voltOffsetV))

/**
 * Bumped whenever the plot's own scales move, so anything derived from them recomputes.
 *
 * <p>uPlot's scales are not reactive — they are mutated in place on the instance — so a drag-zoom
 * changes what is on screen without Vue noticing. Everything that reads the <em>panel's</em> window
 * is fine, but the trigger indicator must read the window the plot is really showing or it clamps
 * against the wrong one: measured in the app, a level dragged to 0.6 V and then zoomed to a window
 * topping out at 0.1 V kept its "on screen" marker and left its handle at a stale height.</p>
 */
const plotWindowTick = ref(0)

/**
 * The volts window on screen: the plot's own after a zoom, the panel's otherwise.
 *
 * <p>The panel's is the fallback rather than the source, because between a rebuild and the first
 * `setScale` the instance has no window yet — and a window of `undefined` would put the indicator
 * in the middle of the plot and call it on screen.</p>
 */
const liveYWindow = computed(() => {
  void plotWindowTick.value
  const scale = plot.value?.scales.y
  if (scale && Number.isFinite(scale.min ?? Number.NaN) && Number.isFinite(scale.max ?? Number.NaN)) {
    return { topV: scale.max as number, bottomV: scale.min as number }
  }
  return yWindow.value
})

/** Where the trigger line goes, and whether the level is really there. */
const indicator = computed(() =>
  triggerIndicator(triggerLevelV.value, liveYWindow.value.topV, liveYWindow.value.bottomV, props.triggerHysteresisV),
)

/**
 * The window trigger's two bounds and the band between them, or null on every other kind.
 *
 * <p>The band <b>replaces</b> the hysteresis band rather than joining it: two translucent bands
 * over each other, one of them a few millivolts tall, is unreadable — and on this kind the band
 * between the bounds is the setting, where the hysteresis is only a noise reject on its edges.</p>
 */
const windowBand = computed(() =>
  props.triggerKind === 'Window'
    ? windowIndicator(
        triggerLevelV.value,
        props.triggerLevelLowV ?? 0,
        liveYWindow.value.topV,
        liveYWindow.value.bottomV,
      )
    : null,
)

/** The cursors' own colour, for their grips — the chart paints the lines in it through `theme()`. */
const cursorColor = computed(() => theme().cursor)

/** Which trace the trigger watches — the line is drawn in that channel's own colour. */
const triggerColor = computed(() => CHANNEL_COLORS[triggerChannel(props.triggerSource)] ?? CHANNEL_COLORS[0])

/** The per-division pair the status line quotes, the way a bench instrument does. */
const timebaseLabel = computed(() => formatPerDivision(spanMs.value))
const verticalLabel = computed(() => formatVoltsPerDivision(props.voltsPerDivision))

/**
 * Both extremes of every column, the min as a band under the line.
 *
 * <p>Skipped for a raw slice, where the line <em>is</em> the waveform — drawing a band there would
 * be a band of zero width over every sample, which costs a full pass for nothing.</p>
 */
function minMaxBandPlugin(): uPlot.Plugin {
  return {
    hooks: {
      draw: [
        (u) => {
          const { xMs, channels } = series.value
          if (!xMs.length) return

          const ctx = u.ctx
          for (const channel of channels) {
            if (!channel.band) continue
            ctx.save()
            ctx.beginPath()
            ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
            ctx.clip()

            const path = new Path2D()
            for (let i = 0; i < xMs.length; i++) {
              const lo = channel.band[i]
              const hi = channel.line[i]
              if (lo === null || hi === null) continue
              const x = u.valToPos(xMs[i], 'x', true)
              // The channel's own scale: the math trace is not on the volts axis, and reading its
              // band against that one would draw it somewhere the trace is not.
              path.moveTo(x, u.valToPos(lo, channel.scale ?? 'y', true))
              path.lineTo(x, u.valToPos(hi, channel.scale ?? 'y', true))
            }

            ctx.strokeStyle = channel.color
            ctx.globalAlpha = 0.35
            ctx.lineWidth = 1
            ctx.stroke(path)
            ctx.restore()
          }
        },
      ],
    },
  }
}

/** A dashed vertical at t = 0 with a marker on the axis — where the trigger fired, and the origin every reading is from. */
function triggerPositionPlugin(t: Theme): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          // Nothing in Roll: t = 0 there is "now", the record's own right-hand edge, and a
          // trigger-position marker on it would claim an event that never happened. The axis says
          // where the edge is by reading 0 at it.
          if (props.record?.rolled === true) return

          const x = u.valToPos(0, 'x', true)
          if (x < u.bbox.left || x > u.bbox.left + u.bbox.width) return

          const ctx = u.ctx
          ctx.save()
          ctx.setLineDash([4, 4])
          ctx.strokeStyle = t.marker
          ctx.lineWidth = 1
          ctx.beginPath()
          ctx.moveTo(x, u.bbox.top)
          ctx.lineTo(x, u.bbox.top + u.bbox.height)
          ctx.stroke()

          // The downward marker on the top edge, which is where a scope puts the trigger position.
          ctx.setLineDash([])
          ctx.fillStyle = t.marker
          const size = 5 * (u.bbox.width / Math.max(u.over.clientWidth, 1))
          ctx.beginPath()
          ctx.moveTo(x - size, u.bbox.top)
          ctx.lineTo(x + size, u.bbox.top)
          ctx.lineTo(x, u.bbox.top + size * 1.6)
          ctx.closePath()
          ctx.fill()
          ctx.restore()
        },
      ],
    },
  }
}

/**
 * The trigger level, and the hysteresis band around it.
 *
 * <p>Drawn in the <b>trigger source channel's own colour</b>, so there is never a question of which
 * trace the level belongs to. The band is the one control whose effect is otherwise completely
 * invisible — it is what a user reaches for when the trace will not stand still, and without
 * something on screen there is nothing to aim it at.</p>
 *
 * <p>In <code>drawAxes</code> rather than <code>draw</code>, so it sits under the traces: the level
 * is a setting rather than a reading, and a line painted over the waveform hides the samples that
 * decide whether it is in the right place.</p>
 */
function triggerLevelPlugin(color: string): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          // Never on a rolling record: nothing triggered, so a line at the trigger level would
          // be read as the thing that placed this window.
          if (!props.showTriggerLevel || props.record?.rolled === true) return

          const ctx = u.ctx
          const left = u.bbox.left
          const right = u.bbox.left + u.bbox.width

          // A window trigger has two bounds and a band between them, which is a different picture
          // from a level with a noise-reject band around it -- so it is drawn as its own thing
          // rather than by passing the level twice through the one above.
          if (windowBand.value) {
            const { high, low, band: filled } = windowBand.value
            ctx.save()

            if (filled) {
              const top = u.valToPos(filled.topV, 'y', true)
              const bottom = u.valToPos(filled.bottomV, 'y', true)
              ctx.fillStyle = color
              ctx.globalAlpha = 0.12
              ctx.fillRect(left, Math.min(top, bottom), u.bbox.width, Math.abs(bottom - top))
              ctx.globalAlpha = 1
            }

            ctx.strokeStyle = color
            ctx.lineWidth = 1
            for (const bound of [high, low]) {
              const y = u.valToPos(bound.levelV, 'y', true)
              ctx.setLineDash(bound.offWindow ? [] : [6, 4])
              ctx.beginPath()
              ctx.moveTo(left, y)
              ctx.lineTo(right, y)
              ctx.stroke()
            }

            ctx.restore()
            return
          }

          const { levelV, band, offWindow } = indicator.value
          ctx.save()

          if (band) {
            const top = u.valToPos(band.topV, 'y', true)
            const bottom = u.valToPos(band.bottomV, 'y', true)
            ctx.fillStyle = color
            ctx.globalAlpha = 0.12
            ctx.fillRect(left, Math.min(top, bottom), u.bbox.width, Math.abs(bottom - top))
            ctx.globalAlpha = 1
          }

          const y = u.valToPos(levelV, 'y', true)
          ctx.strokeStyle = color
          ctx.lineWidth = 1
          // Dashed, so it cannot be mistaken for a trace; solid at the edge when the level is off
          // the window, which is where the arrow says the rest of it is.
          ctx.setLineDash(offWindow ? [] : [6, 4])
          ctx.beginPath()
          ctx.moveTo(left, y)
          ctx.lineTo(right, y)
          ctx.stroke()
          ctx.restore()
        },
      ],
    },
  }
}


/**
 * The cursors: two dashed verticals in time and two dashed horizontals in volts.
 *
 * <p>In <code>draw</code> rather than <code>drawAxes</code>, which is the opposite of where the
 * trigger level goes and for the matching reason: the level is a <em>setting</em> and must not hide
 * the samples that decide whether it is in the right place, while a cursor is a <em>reading</em>
 * and has to be visible exactly where it crosses the trace it is being read against.</p>
 *
 * <p>Each line carries its letter at the end the handle is on, so the readout's "Time A" and the
 * line on screen cannot be confused for one another.</p>
 */
function cursorsPlugin(t: Theme): uPlot.Plugin {
  return {
    hooks: {
      draw: [
        (u) => {
          const c = cursors.value
          if (!c.timeOn && !c.voltOn) return

          const ctx = u.ctx
          const left = u.bbox.left
          const right = left + u.bbox.width
          const top = u.bbox.top
          const bottom = top + u.bbox.height
          const px = u.bbox.width / Math.max(u.over.clientWidth, 1)

          ctx.save()
          ctx.strokeStyle = t.cursor
          ctx.fillStyle = t.cursor
          ctx.lineWidth = px
          ctx.setLineDash([3 * px, 3 * px])
          ctx.font = `${Math.round(10 * px)}px system-ui, sans-serif`

          if (c.timeOn) {
            for (const [label, seconds] of [['A', c.timeAS], ['B', c.timeBS]] as const) {
              if (!Number.isFinite(seconds)) continue
              const x = u.valToPos(toChartMs(seconds), 'x', true)
              // Off the window: drawn nowhere rather than clamped to the edge. A time cursor is read
              // against the trace under it, so a clamped one would claim a crossing it is not at --
              // where the trigger level, which is a setting rather than a reading, is better clamped
              // and marked than hidden.
              if (x < left || x > right) continue
              ctx.beginPath()
              ctx.moveTo(x, top)
              ctx.lineTo(x, bottom)
              ctx.stroke()
              ctx.setLineDash([])
              ctx.textAlign = 'center'
              ctx.textBaseline = 'bottom'
              ctx.fillText(label, x, bottom - 2 * px)
              ctx.setLineDash([3 * px, 3 * px])
            }
          }

          if (c.voltOn) {
            for (const [label, volts] of [['A', c.voltAV], ['B', c.voltBV]] as const) {
              if (!Number.isFinite(volts)) continue
              const y = u.valToPos(volts, 'y', true)
              if (y < top || y > bottom) continue
              ctx.beginPath()
              ctx.moveTo(left, y)
              ctx.lineTo(right, y)
              ctx.stroke()
              ctx.setLineDash([])
              ctx.textAlign = 'left'
              ctx.textBaseline = 'middle'
              ctx.fillText(label, left + 3 * px, y)
              ctx.setLineDash([3 * px, 3 * px])
            }
          }

          ctx.restore()
        },
      ],
    },
  }
}


// ---------------------------------------------------------------------------
// Persistence — the digital-phosphor picture
// ---------------------------------------------------------------------------

/** One accumulator per drawn channel, so the haze is painted in the trace's own colour. */
interface IntensityLayer {
  index: number
  color: string
  buffer: ScopeIntensityBuffer
  pixels?: Uint8ClampedArray<ArrayBuffer>
}

/**
 * The accumulated hit counts, in CSS pixels of the plot area, and the picture they were drawn for.
 *
 * <p>Held on the component rather than in a store because it <b>is</b> the component's canvas: it
 * is a history of a <em>display</em> and not of a capture, so it is not parked with the run, not
 * restored with the measurement and not in a report — the spectrogram's rows, for the same
 * reason.</p>
 */
const intensityLayers = shallowRef<IntensityLayer[]>([])
let intensityFor = ''
let intensityCanvas: HTMLCanvasElement | null = null

/** Bumped when the picture changes, so a redraw repaints it. */
const intensityTick = ref(0)

/** The record already folded in, so a redraw cannot count one twice. */
let accumulatedRecord: ScopeRecordResponse | null = null

/**
 * What the accumulated counts are a picture <em>of</em>.
 *
 * <p><b>This is the rule the whole feature turns on.</b> A cell is a pixel, and a pixel stands for
 * a time and a level only while the plot's size and both of its windows are unchanged — so a zoom,
 * a V/div step, a resize or a measurement switch makes every count already there a claim about a
 * different part of the signal. Read fresh on every record rather than watched: a watcher is a
 * list somebody has to keep in step, and this is the one place that already knows all of it.</p>
 */
function currentIntensitySignature(u: uPlot): string {
  const geo = intensityGeometry(u)
  return intensitySignature(
    geo.width,
    geo.height,
    u.scales.x.min ?? Number.NaN,
    u.scales.x.max ?? Number.NaN,
    u.scales.y.min ?? Number.NaN,
    u.scales.y.max ?? Number.NaN,
    `${props.persistenceKey}:${props.showLeft}:${props.showRight}`,
  )
}

/** The plot area in CSS pixels, which is what the buffer is sized and addressed in. */
function intensityGeometry(u: uPlot): { width: number; height: number } {
  const css = u.over.clientWidth
  const scale = css > 0 ? u.bbox.width / css : 1
  return { width: Math.round(u.bbox.width / scale), height: Math.round(u.bbox.height / scale) }
}

/** Throws the picture away. A changed signature, a Clear, and switching persistence off all land here. */
function resetIntensity() {
  intensityLayers.value = []
  intensityFor = ''
  accumulatedRecord = null
  intensityTick.value++
}

/**
 * Folds the record on screen into the accumulated picture.
 *
 * <p>Called when a record arrives rather than from the draw hook, and that is what keeps the counts
 * a measurement: a redraw happens for a dozen reasons — a theme change, a cursor drag, a resize —
 * and counting on any of them would make the brightness a reading of how often the chart
 * repainted.</p>
 */
function accumulateIntensity() {
  const u = plot.value
  if (!u || !persistenceDraws(props.persistence)) return

  const record = props.record
  if (!record || record === accumulatedRecord) return

  const { width, height } = intensityGeometry(u)
  if (width < 1 || height < 1) return

  const signature = currentIntensitySignature(u)
  const { xMs, channels } = series.value

  // The residual and the math trace are derived and are left out: accumulating one would put a
  // second, fainter waveform into a picture whose whole subject is how often the measured one
  // passes somewhere -- and the math trace is not even on the volts scale this reads positions
  // against, so its hits would land at the wrong heights.
  const measured = channels.filter((channel) => !channel.residual && !channel.math && !channel.reference)
  if (!measured.length || !xMs.length) return

  let layers = intensityLayers.value
  const stale =
    intensityFor !== signature ||
    layers.length !== measured.length ||
    layers.some((layer, i) => layer.index !== measured[i].index)

  if (stale) {
    layers = measured.map((channel) => ({
      index: channel.index,
      color: channel.color,
      buffer: new ScopeIntensityBuffer(width, height),
    }))
    intensityLayers.value = layers
    intensityFor = signature
  }

  const decay = persistenceDecay(props.persistence)

  for (let c = 0; c < measured.length; c++) {
    const channel = measured[c]
    const buffer = layers[c].buffer
    buffer.decay(decay)

    let previous: number | null = null
    for (let i = 0; i < xMs.length; i++) {
      const hi = channel.line[i]
      if (hi === null) {
        previous = null
        continue
      }

      // A decimated column already *is* a span — both extremes of what it covers — while a raw
      // trace's span is from the previous sample to this one, which is what keeps a fast edge a
      // line rather than a column of dots.
      const lo = channel.band ? (channel.band[i] ?? hi) : (previous ?? hi)
      // valToPos with canvasPixels false is already relative to the plot area's own origin, which
      // is exactly what a buffer cell is addressed in -- subtracting bbox would shift every hit up
      // and left by the axis gutters.
      buffer.addSpan(
        u.valToPos(xMs[i], 'x', false),
        u.valToPos(hi, 'y', false),
        u.valToPos(lo, 'y', false),
      )
      previous = hi
    }

    buffer.renormalise()
  }

  accumulatedRecord = record
  intensityTick.value++
}

/**
 * The accumulated picture, painted under the traces.
 *
 * <p>Under them deliberately: the newest record is drawn at full strength as an ordinary series, so
 * what the haze adds is everything <em>before</em> it — which is how a bench instrument reads, the
 * live trace brightest with its own history behind it.</p>
 */
function intensityPlugin(): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          // Read so the draw re-runs when the picture changes rather than only when uPlot decides.
          void intensityTick.value
          if (!persistenceDraws(props.persistence)) return

          const layers = intensityLayers.value
          if (!layers.length) return

          if (!intensityCanvas) intensityCanvas = document.createElement('canvas')
          const scratch = intensityCanvas

          const ctx = u.ctx
          ctx.save()
          ctx.imageSmoothingEnabled = false
          for (const layer of layers) {
            const buffer = layer.buffer
            if (buffer.peak <= 0) continue

            layer.pixels = intensityPixels(
              buffer,
              props.intensityMode,
              props.intensityPalette,
              hexRgb(layer.color),
              layer.pixels,
            )

            if (scratch.width !== buffer.width || scratch.height !== buffer.height) {
              scratch.width = buffer.width
              scratch.height = buffer.height
            }
            const scratchCtx = scratch.getContext('2d')
            if (!scratchCtx) break
            scratchCtx.putImageData(new ImageData(layer.pixels, buffer.width, buffer.height), 0, 0)
            ctx.drawImage(scratch, u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          }
          ctx.restore()
        },
      ],
    },
  }
}

const { zoomed, isZoomedNow, noteScale, resetZoom, noteZoomGesture, zoomBack, currentWindow, canGoBack } = useZoomBounds(
  () => plot.value,
  () => ({
    xMin: xWindowMs.value.min,
    xMax: xWindowMs.value.max,
    yMin: yWindow.value.bottomV,
    yMax: yWindow.value.topV,
  }),
)

/**
 * This chart's own drag-zoom, and what the Box gesture puts back when it is disarmed — one constant
 * so <code>cursor.drag</code> and <code>syncBoxDragMode</code> cannot disagree. See BoxDragResting.
 *
 * <p>Both axes: a scope is zoomed in time far more often than in volts, but the vertical window is
 * a V/div rung rather than an arbitrary pair, so a vertical drag is the only way to get between two
 * rungs — and demanding a pixel-accurate horizontal drag to avoid it would make both awkward.</p>
 */
const DRAG_RESTING: BoxDragResting = { y: true, uni: 12 }

/** How long a burst of window changes is collapsed for before the parent is told. See reportViewport. */
const VIEWPORT_DEBOUNCE_MS = 150

/**
 * "Set axis": hands the window on screen up, for the display panel's own boxes to take it on.
 *
 * <p>Converted here rather than by the parent, which is the point of `fromChartMs` existing: this
 * chart is the one place that knows it plots milliseconds, and the boxes upstream are in seconds.</p>
 */
function applyZoomToAxis() {
  const w = currentWindow()
  if (!w) return
  emit('axisWindow', {
    fromS: fromChartMs(w.xMin),
    toS: fromChartMs(w.xMax),
    topV: Math.max(w.yMin, w.yMax),
    bottomV: Math.min(w.yMin, w.yMax),
  })
}

function buildOptions(width: number, height: number, t: Theme, interactive: boolean): uPlot.Options {
  const channels = series.value.channels
  return {
    width,
    height,
    scales: {
      // Arguments passed through rather than ignored, so a drag-zoom survives setScale. What puts
      // the panel's window on the x axis is refresh()'s explicit setScale, not the `??` here — on
      // an x scale that fallback is dead code, since uPlot only passes null with no data yet.
      x: { time: false, auto: false, range: (_u, min, max) => [min ?? xWindowMs.value.min, max ?? xWindowMs.value.max] },
      y: { auto: false, range: (_u, min, max) => [min ?? yWindow.value.bottomV, max ?? yWindow.value.topV] },
      // The math trace auto-ranges, which is the honest answer for a derived quantity whose
      // magnitude nobody knows in advance -- and unlike the volts axis there is no V/div ladder it
      // could be a rung of. uPlot ranges an auto scale over what is visible in x, so it follows a
      // zoom the way a scope's own math channel does.
      [MATH_SCALE]: { auto: true },
    },
    axes: [
      {
        label: `Time (${scopeTimeScale(spanMs.value).label})`,
        stroke: t.axis,
        grid: { stroke: t.grid, width: t.gridWidth },
        ticks: { stroke: t.grid, width: t.gridWidth },
        // One formatter for the ticks, the readout and the panel's boxes, so a figure means the
        // same thing wherever it is read. The unit is on the label, so not repeated per tick.
        values: (_u, splits) => splits.map((ms) => formatScopeTime(ms, spanMs.value, false)),
      },
      {
        label: 'Level',
        size: 64,
        stroke: t.axis,
        grid: { stroke: t.grid, width: t.gridWidth },
        ticks: { stroke: t.grid, width: t.gridWidth },
        values: (_u, splits) => splits.map((v) => formatVolts(v, props.voltsPerDivision)),
      },
      // The math axis, on the right and only while there is a trace on it -- an axis labelled for
      // a quantity nothing is drawing takes 64 px off the plot and says nothing.
      ...(mathDrawn.value
        ? [
            {
              scale: MATH_SCALE,
              side: 1 as const,
              label: mathAxisLabel(props.mathOperation),
              size: 72,
              stroke: MATH_COLOR,
              // No grid of its own: a second set of horizontals over the first would make both
              // unreadable, and the graticule belongs to the volts axis the waveforms are on.
              grid: { show: false },
              ticks: { stroke: t.grid, width: t.gridWidth },
              values: (_u: uPlot, splits: number[]) =>
                splits.map((v) => formatMathValue(v, mathUnitOf(props.mathOperation))),
            },
          ]
        : []),
    ],
    series: [
      { label: 'Time' },
      ...channels.map((channel) => ({
        label: channel.label,
        scale: channel.scale,
        stroke: channel.color,
        // Thinner and dashed for the residual: it is derived from the trace beside it rather than
        // measured, and at a quiet DUT it sits on the zero line where a solid line of the same
        // weight would read as a second waveform.
        width: channel.residual || channel.reference ? 1 : props.thickLines ? 2 : 1,
        dash: channel.residual ? [4, 3] : channel.dash,
        // Dots: the samples and nothing joining them, which is the one mode that can never draw
        // something the analyzer did not measure. uPlot takes a null path builder for that.
        paths: dotsOnly.value ? () => null : undefined,
        // Markers only on a raw slice: a point drawn on a min/max column marks the column's
        // maximum rather than a sample, which is a reading of the decimation.
        points: {
          show: (props.showDataPoints || dotsOnly.value) && channel.raw && !channel.residual && !channel.reference,
          size: dotsOnly.value ? 3 : 4,
        },
      })),
    ],
    // Last: whatever draws after the annotations paints over their labels.
    plugins: [
      intensityPlugin(),
      minMaxBandPlugin(),
      triggerPositionPlugin(t),
      triggerLevelPlugin(triggerColor.value),
      cursorsPlugin(t),
      annotationsPlugin(annotationOverlay, ANNOTATION_AXES, () => annotationTheme(t === THEME.dark)),
    ],
    cursor: interactive ? { drag: { x: true, ...DRAG_RESTING } } : { show: false },
    legend: { show: false },
    hooks: interactive
      ? {
          setScale: [
            (u: uPlot, key: string) => {
              noteScale(u)
              // The committed window, which is the one a gesture has really left on screen.
              if (key === 'x' || key === 'y') heldWindow.value = isZoomedNow(u) ? currentWindow() : null
              // The indicator and its handle are positioned against the window on screen, so a
              // zoom has to move them -- uPlot's scales are mutated in place and Vue sees nothing.
              plotWindowTick.value++
              syncHandle()
              if (key === 'x') reportViewport()
            },
          ],
          setSelect: [(u: uPlot) => noteZoomGesture(u)],
        }
      : {},
  }
}

// ---------------------------------------------------------------------------
// Dragging the trigger level
// ---------------------------------------------------------------------------

/**
 * Where the drag handle sits, in CSS pixels relative to the chart's container, or null when there is
 * no plot to put it on.
 *
 * <p><b>A DOM handle above the canvas, not a third claimant on uPlot's drag.</b> The plot already
 * carries a drag-zoom, and the annotation system commandeers that same gesture while a Box is armed;
 * a third would be the <code>syncBoxDragMode</code> trap exactly. A handle that is its own element
 * takes the pointer only when the press starts on it, by construction rather than by a rule — a
 * <code>pointerdown</code> on it never reaches <code>u.over</code>, so nothing has to be restored
 * afterwards and nothing can be left half-restored.</p>
 */
const handleRect = shallowRef<{ top: number; right: number; height: number; width: number } | null>(null)

/** How wide the grab area is, and how tall. Generous enough to hit, far enough right to be clear of a zoom drag. */
const HANDLE_WIDTH_PX = 48
const HANDLE_HEIGHT_PX = 18

function syncHandle() {
  // The cursors' grips are positioned against the same window, so they move with it -- in one
  // function rather than beside every call, so a new call site cannot update one and not the other.
  syncCursorHandles()

  const u = plot.value
  if (!u || !props.showTriggerLevel || props.record?.rolled === true) {
    handleRect.value = null
    return
  }
  const css = u.over.clientWidth
  const scale = css > 0 ? u.bbox.width / css : 1
  const y = u.valToPos(indicator.value.levelV, 'y', false)
  if (!Number.isFinite(y)) {
    handleRect.value = null
    return
  }
  handleRect.value = {
    top: u.bbox.top / scale + y - HANDLE_HEIGHT_PX / 2,
    right: 0,
    height: HANDLE_HEIGHT_PX,
    width: HANDLE_WIDTH_PX,
  }
}

const dragging = ref(false)

function onHandlePointerDown(event: PointerEvent) {
  const u = plot.value
  if (!u) return
  // Keeps the press away from uPlot's own listeners on `over`, which is the whole reason the handle
  // is an element of its own.
  event.stopPropagation()
  event.preventDefault()
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  dragging.value = true
}

function onHandlePointerMove(event: PointerEvent) {
  if (!dragging.value) return
  const u = plot.value
  if (!u) return

  const rect = u.over.getBoundingClientRect()
  const volts = u.posToVal(event.clientY - rect.top, 'y')
  if (!Number.isFinite(volts)) return

  // Written live rather than on release, so the line and the setting cannot disagree mid-drag. The
  // *re-arm* waits for the release -- re-arming per pointer move would throw away a capture per
  // pixel.
  //
  // Rounded against the window on screen -- see roundVoltsForWindow for why that is a decimal
  // place rather than a step, and for what the box read before it.
  triggerLevelV.value = roundVoltsForWindow(volts, Math.abs(liveYWindow.value.topV - liveYWindow.value.bottomV))
}

function onHandlePointerUp(event: PointerEvent) {
  if (!dragging.value) return
  dragging.value = false
  ;(event.currentTarget as HTMLElement).releasePointerCapture?.(event.pointerId)
  emit('triggerLevelCommitted')
}

/** The reading beside the handle while it is dragged — the level in the same unit the axis is in. */
const triggerReadout = computed(() => formatVolts(triggerLevelV.value, props.voltsPerDivision))

// ---------------------------------------------------------------------------
// Dragging the cursors
// ---------------------------------------------------------------------------

/**
 * Which cursor a handle belongs to. The four are one list rather than four refs so the template
 * draws them in one loop and the drag reads which one it has from the handle itself.
 */
type CursorId = 'timeA' | 'timeB' | 'voltA' | 'voltB'

interface CursorHandle {
  id: CursorId
  label: string
  /** 'time' sits on the bottom axis and moves in x; 'volt' sits on the left and moves in y. */
  axis: 'time' | 'volt'
  /** Position in CSS pixels within the chart container. */
  left: number
  top: number
}

/** The grab squares, in CSS pixels. 18 px, the trigger handle's own height, so all five feel alike. */
const CURSOR_HANDLE_PX = 18

/**
 * Where the four grab squares sit.
 *
 * <p><b>On the bottom and left edges, which is the one placement that is free.</b> The trigger
 * level's handle is on the right and the trigger position's marker is on the top, so time cursors go
 * along the bottom and level cursors down the left: no two of the five claimants overlap, and a
 * press therefore lands on exactly one of them by construction rather than by a rule — the same
 * thing that keeps all of them clear of uPlot's own drag on <code>over</code>.</p>
 */
const cursorHandles = shallowRef<CursorHandle[]>([])

function syncCursorHandles() {
  const u = plot.value
  const c = cursors.value
  if (!u) {
    cursorHandles.value = []
    return
  }

  const css = u.over.clientWidth
  const scale = css > 0 ? u.bbox.width / css : 1
  const plotLeft = u.bbox.left / scale
  const plotTop = u.bbox.top / scale
  const plotWidth = u.bbox.width / scale
  const plotHeight = u.bbox.height / scale
  const half = CURSOR_HANDLE_PX / 2

  const out: CursorHandle[] = []

  if (c.timeOn) {
    for (const [id, label, seconds] of [
      ['timeA', 'A', c.timeAS],
      ['timeB', 'B', c.timeBS],
    ] as const) {
      const x = u.valToPos(toChartMs(seconds), 'x', false)
      // A handle for a cursor that is off the window is not drawn, matching the line: there is
      // nothing on screen for it to be the grip of.
      if (!Number.isFinite(x) || x < 0 || x > plotWidth) continue
      out.push({ id, label, axis: 'time', left: plotLeft + x - half, top: plotTop + plotHeight - half })
    }
  }

  if (c.voltOn) {
    for (const [id, label, volts] of [
      ['voltA', 'A', c.voltAV],
      ['voltB', 'B', c.voltBV],
    ] as const) {
      const y = u.valToPos(volts, 'y', false)
      if (!Number.isFinite(y) || y < 0 || y > plotHeight) continue
      out.push({ id, label, axis: 'volt', left: plotLeft, top: plotTop + y - half })
    }
  }

  cursorHandles.value = out
}

const draggingCursor = ref<CursorId | null>(null)

function onCursorPointerDown(event: PointerEvent, id: CursorId) {
  if (!plot.value) return
  // Away from uPlot's listeners on `over`, which is why each of these is an element of its own.
  event.stopPropagation()
  event.preventDefault()
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  draggingCursor.value = id
}

function onCursorPointerMove(event: PointerEvent) {
  const id = draggingCursor.value
  const u = plot.value
  if (!id || !u) return

  const rect = u.over.getBoundingClientRect()
  // Written live, so the lines, the readout and the panel's boxes cannot disagree mid-drag; the
  // *gate* waits for the release, a fetch per pointer move being the thing to avoid.
  //
  // The other three fields come from the model as it stands, which is safe for the reason the
  // trigger drag is: the one field being written is taken from the pointer each move rather than
  // from a value that has to have arrived back as a prop first.
  if (id === 'timeA' || id === 'timeB') {
    const ms = u.posToVal(event.clientX - rect.left, 'x')
    if (!Number.isFinite(ms)) return
    cursors.value = { ...cursors.value, [id === 'timeA' ? 'timeAS' : 'timeBS']: fromChartMs(ms) }
  } else {
    const volts = u.posToVal(event.clientY - rect.top, 'y')
    if (!Number.isFinite(volts)) return
    const rounded = roundVoltsForWindow(volts, Math.abs(liveYWindow.value.topV - liveYWindow.value.bottomV))
    cursors.value = { ...cursors.value, [id === 'voltA' ? 'voltAV' : 'voltBV']: rounded }
  }
}

function onCursorPointerUp(event: PointerEvent) {
  if (!draggingCursor.value) return
  draggingCursor.value = null
  ;(event.currentTarget as HTMLElement).releasePointerCapture?.(event.pointerId)
  emit('cursorsCommitted')
}

/** What a handle says while it is held — the cursor's own position, in the axis's own unit. */
function cursorReadout(id: CursorId): string {
  const c = cursors.value
  if (id === 'timeA') return formatScopeTime(toChartMs(c.timeAS), spanMs.value)
  if (id === 'timeB') return formatScopeTime(toChartMs(c.timeBS), spanMs.value)
  return formatVolts(id === 'voltA' ? c.voltAV : c.voltBV, props.voltsPerDivision)
}



// ---------------------------------------------------------------------------

async function exportPng({ width, height, captureZoom = false }: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header

  const host = document.createElement('div')
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(host)

  const live = plot.value
  const xWin =
    captureZoom && live?.scales.x.min != null && live.scales.x.max != null
      ? { min: live.scales.x.min, max: live.scales.x.max }
      : xWindowMs.value
  const yWin =
    captureZoom && live?.scales.y.min != null && live.scales.y.max != null
      ? { min: live.scales.y.min, max: live.scales.y.max }
      : { min: yWindow.value.bottomV, max: yWindow.value.topV }

  let u: uPlot | null = null
  try {
    const { xMs, channels } = series.value
    const blank = [[], ...channels.map(() => [])] as unknown as uPlot.AlignedData
    u = new uPlot(buildOptions(width, plotHeight, exportGridTheme(THEME.light), false), blank, host)
    u.setData([xMs, ...channels.map((c) => c.line)] as unknown as uPlot.AlignedData)
    u.setScale('x', xWin)
    u.setScale('y', yWin)
    await nextPaint()

    const record = props.record
    const detail = record
      ? ` — ${timebaseLabel.value} · ${verticalLabel.value}${props.filterNote ? ` · ${props.filterNote}` : ''}${
          record.spliced ? ' · spliced' : ''
        }${record.forced ? ' · untriggered' : ''}${record.rolled ? ' · rolling' : ''}`
      : ''
    return await composeChartPng({
      width,
      height,
      plotCanvas: u.ctx.canvas,
      headerHeight: header,
      title: `Oscilloscope${detail}`,
      legend: channels.map((c) => ({ label: c.label, color: c.color })),
    })
  } finally {
    u?.destroy()
    host.remove()
  }
}

defineExpose({ exportPng })

function rebuild() {
  const el = container.value
  if (!el) return

  // A rebuild may not throw the window away, and on this chart that is not a nicety: both of the
  // things in the rebuild watcher's list -- the axis unit, and a slice flipping between min/max
  // columns and raw samples -- are *consequences* of a zoom. Rebuilding back to the panel's window
  // would undo the gesture that caused the rebuild, report the whole record, and be handed the
  // overview again, which is a loop rather than a lost zoom.
  plot.value?.destroy()
  const width = el.getBoundingClientRect().width || 600
  // One empty array per declared series. Empty construction then setData is also what makes the x
  // window honour the panel's props rather than the data's span (CLAUDE.md).
  const blank = [[], ...series.value.channels.map(() => [])] as unknown as uPlot.AlignedData
  plot.value = new uPlot(buildOptions(width, chartHeight.value, theme(), true), blank, el)

  attachAnnotationListeners(plot.value, annotationOverlay)
  syncBoxDragMode(plot.value, annotationOverlay.armedForBox.value, DRAG_RESTING)
  plot.value.over.addEventListener('dblclick', resetZoom)
  // refresh() puts the held window back by itself, which is the point of holding it rather than
  // reading it off the plot: a slice flipping between min/max columns and raw samples *is* a
  // rebuild, and a zoom is what causes that flip.
  refresh()
}

/**
 * The window a gesture put on screen, or null while the chart is showing the display panel's.
 *
 * <p><b>Held here rather than read back off the plot</b>, and that is what makes a zoom survive a
 * run. uPlot commits a scale on a microtask, so between a <code>setScale</code> and its commit the
 * plot still reports the old window — or none at all, on one just constructed. Every refresh that
 * landed in such a gap concluded the chart was not zoomed and wrote the panel's window over the
 * held one, and during a run refreshes arrive several times a second: a zoom lasted about one
 * capture (reported 2026-11-17). With the window kept here each refresh <em>asserts</em> it rather
 * than guessing from a plot that has not caught up, so the order of the two stops mattering.</p>
 *
 * <p>Written by the <code>setScale</code> hook, which is where a real gesture lands and which sees
 * the committed window: a drag sets it, and anything that returns the chart to the panel's window
 * — Reset zoom, the last step of Back, a typed box — clears it.</p>
 */
const heldWindow = ref<ZoomWindow | null>(null)

/**
 * New data, and the window it is drawn in.
 *
 * <p><b>The window is only re-applied when the chart is not zoomed</b>, which is the other half of
 * fetching a slice per window: the record that arrives for a zoomed window <em>is</em> that window,
 * so setting the panel's would both undo the zoom and ask the server for the overview again on the
 * next report. A change to the panel's own boxes passes <code>force</code>, because typing a window
 * is an instruction rather than an arriving record — and it is what ends the zoom.</p>
 */
function refresh(force = false) {
  const p = plot.value
  if (!p) return

  // A panel edit is an instruction, and it is what ends a zoom.
  if (force) heldWindow.value = null

  const { xMs, channels } = series.value
  // `false`: setData's default is to autoscale x to the *data's* own span, and a slice cut to a
  // zoom window is data whose span is that window -- so the default both fights the window being
  // held and, on the tick where the previous record is still in hand, snaps the axis to the old
  // one. Every window this chart shows is set deliberately, just below.
  p.setData([xMs, ...channels.map((c) => c.line)] as unknown as uPlot.AlignedData, false)

  // One of the two windows, always written: `setData` with its scale reset turned off does not
  // commit, so without a setScale after it uPlot never repaints — a zoomed chart went on drawing
  // the previous record until something else forced it, which a Display checkbox did because it
  // rebuilds (reported 2026-11-17, "when zoomed in and the scope is running, the data is not
  // updated"). setData has already dropped the cached paths, so this redraws from the new samples
  // whether or not the window moved.
  const held = heldWindow.value
  if (held) {
    p.setScale('x', { min: held.xMin, max: held.xMax })
    p.setScale('y', { min: held.yMin, max: held.yMax })
  } else {
    p.setScale('x', xWindowMs.value)
    p.setScale('y', { min: yWindow.value.bottomV, max: yWindow.value.topV })
  }
  syncHandle()
}

/**
 * Hands the parent the window on screen, for the fetch that cuts the record to it.
 *
 * <p><b>Debounced and change-gated here</b>, which is <code>SpectrumChart.reportViewport</code>'s
 * own shape and is not merely about asking less often. A rebuild — which a slice flipping between
 * min/max columns and raw samples causes — constructs the plot at the panel's window and only then
 * puts the held window back, so it passes through a window that was never on screen. Reported
 * straight out, that intermediate is the <em>last</em> thing the parent hears, and it fetches the
 * overview, which flips the slice back, which rebuilds: a loop that settles on the unzoomed record.
 * One debounce collapses the pair and only the window that ended up on screen leaves.</p>
 */
let viewportTimer: ReturnType<typeof setTimeout> | undefined
let lastReportedFromS: number | undefined
let lastReportedToS: number | undefined

function reportViewport() {
  const p = plot.value
  if (!p) return
  const min = p.scales.x.min
  const max = p.scales.x.max
  if (min == null || max == null || !(max > min)) return

  const view = {
    fromS: fromChartMs(min),
    toS: fromChartMs(max),
    columns: Math.max(1, Math.round(p.over?.getBoundingClientRect().width || p.width || 600)),
  }
  if (view.fromS === lastReportedFromS && view.toS === lastReportedToS) return
  lastReportedFromS = view.fromS
  lastReportedToS = view.toS

  clearTimeout(viewportTimer)
  viewportTimer = setTimeout(() => emit('viewport', view), VIEWPORT_DEBOUNCE_MS)
}

let observer: ResizeObserver | null = null

function fit() {
  const el = container.value
  if (el && plot.value) {
    plot.value.setSize({ width: el.getBoundingClientRect().width, height: chartHeight.value })
    syncHandle()
    // The width is the column budget, so a wider chart is a finer slice -- and a chart narrow
    // enough turns a raw window back into columns.
    reportViewport()
  }
}
watch(chartHeight, fit)

/**
 * What this chart's two axes are, in annotation terms.
 *
 * <p>A <b>linear time in milliseconds that crosses zero</b> — the pre-trigger sits at a negative
 * time, which is the case the parser's "any finite x" rule and the linear offset space exist for;
 * a log axis has no position for t = 0 at all. The same quantity the impulse chart uses, so a note
 * means the same thing on both.</p>
 *
 * <p>Not a computed, unlike the impulse chart's: neither axis of this chart ever changes which
 * quantity it plots. The displayed <em>unit</em> moves with the window, but the stored figure does
 * not, which is exactly the arrangement that keeps a note in place when a tighter window changes
 * the axis from milliseconds to microseconds.</p>
 */
const ANNOTATION_AXES: UplotAnnotationAxes = {
  x: { quantity: 'ms' },
  y: { quantity: 'volt' },
}

const annotationOverlay = useAnnotationOverlay({
  view: () => OSCILLOSCOPE_ANNOTATION_VIEW,
  list: annotationList,
  armed: annotationArmed,
  numbers: () => props.annotationNumbers !== false,
  cssPlot: () => (plot.value ? cssAnnotationPlot(plot.value, ANNOTATION_AXES) : null),
  surface: () => plot.value?.over ?? null,
  overlayRect: () => (plot.value ? annotationOverlayRect(plot.value) : null),
  redraw: () => plot.value?.redraw(),
  onArmedChange: () => syncBoxDragMode(plot.value, annotationOverlay.armedForBox.value, DRAG_RESTING),
})

function onWindowMouseUp() {
  finishBoxGesture(plot.value, annotationOverlay, DRAG_RESTING)
}

function onWindowKeyDown(event: KeyboardEvent) {
  annotationOverlay.onWindowKeyDown(event)
}

// A redraw, never a rebuild: annotations are draw-hook state, and a rebuild throws the zoom away.
watch(
  () => [annotationList.value, props.annotationNumbers] as const,
  () => plot.value?.redraw(),
  { deep: true },
)

// The trigger line and its band are draw-hook state too, so moving the level is a redraw and the
// handle follows it. A rebuild here would discard the zoom on every pixel of a drag.
watch(
  () =>
    [
      triggerLevelV.value,
      props.triggerHysteresisV,
      props.showTriggerLevel,
      triggerColor.value,
      // Or switching to the window kind, or typing its other bound, leaves the old line on screen
      // until something else happens to redraw -- which reads as a control that does nothing.
      props.triggerKind,
      props.triggerLevelLowV,
    ] as const,
  () => {
    plot.value?.redraw()
    syncHandle()
  },
)

// The cursors are draw-hook state too, so moving one is a redraw and its grip follows it. A rebuild
// here would discard the zoom on every pixel of a drag.
watch(cursors, () => {
  plot.value?.redraw()
  syncCursorHandles()
})

onMounted(() => {
  window.addEventListener('mouseup', onWindowMouseUp)
  window.addEventListener('keydown', onWindowKeyDown)
  rebuild()
  observer = new ResizeObserver(fit)
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  clearTimeout(viewportTimer)
  window.removeEventListener('mouseup', onWindowMouseUp)
  window.removeEventListener('keydown', onWindowKeyDown)
  observer?.disconnect()
  observer = null
  plot.value?.destroy()
  plot.value = null
})

/**
 * A theme change or a change of series count needs the whole plot rebuilt; new data does not.
 *
 * <p>The axis <em>unit</em> is in this list because it is baked into the axis label and the tick
 * formatter at construction — so a window tightened from milliseconds into microseconds is a
 * rebuild, where moving the same window is not.</p>
 */
watch(
  [
    isDark,
    () => props.thickLines,
    () => props.showDataPoints,
    () => scopeTimeScale(spanMs.value).unit,
    // The math operation, because its axis label and tick formatter are baked in at construction
    // the way the time axis's unit is -- two operations both drawn would otherwise keep the first
    // one's label.
    () => `${mathDrawn.value}:${props.mathOperation}`,
    // The drawing mode, because Dots replaces the series' own path builder, which uPlot reads at
    // construction -- a mode changed without a rebuild ticks and does nothing.
    () => props.drawing,
    () =>
      series.value.channels
        .map((c) => `${c.index}:${c.raw}:${c.residual === true}:${c.math === true}:${c.reference === true}`)
        .join(','),
  ],
  rebuild,
)

// A record arriving keeps whatever window is on screen; see refresh().
watch(() => props.record, () => {
  refresh()
  // Queued, so the samples are measured against the scales they are about to be drawn against:
  // uPlot commits a scale on a microtask, and reading valToPos before that commit would place
  // every hit in the window being left.
  void nextTick(() => {
    accumulateIntensity()
    plot.value?.redraw()
  })
})

// Switching persistence off throws the picture away rather than freezing it: what is on screen has
// to be what the setting says, and a frozen haze over a live trace is neither.
watch(
  () => [props.persistence, props.intensityMode, props.intensityPalette] as const,
  ([mode]) => {
    if (!persistenceDraws(mode)) resetIntensity()
    plot.value?.redraw()
  },
)

// A record going away — a Clear, or a measurement with nothing captured — takes its history too.
watch(
  () => props.record === null,
  (empty) => {
    if (empty) resetIntensity()
  },
)

// The panel's own boxes are an instruction, and they end a zoom.
watch(
  [() => props.timeFromS, () => props.timeToS, () => props.voltsPerDivision, () => props.voltOffsetV],
  () => refresh(true),
)

/**
 * The axis an empty chart draws — the window the <em>settings</em> ask for.
 *
 * <p>An empty scope still has to show where the next capture will land, which is the "an empty chart
 * draws empty axes, it does not destroy its plot" rule: a torn-down plot is a zero-height box, and
 * the whole column jumps the moment a capture arrives.</p>
 */
const emptyWindow = computed(() => recordWindow(props.recordSeconds, props.preTriggerFraction))

watch(emptyWindow, () => {
  if (!props.record) refresh()
})

/** What the header says about the record on screen. */
const recordNote = computed(() => {
  const record = props.record
  if (!record) return 'Nothing captured yet'
  const parts = [`${record.frameCount.toLocaleString()} samples`, `${(record.sampleRateHz / 1000).toFixed(0)} kS/s`]
  // An envelope's columns are the band itself rather than a decimation of a waveform, so neither
  // of the two usual answers is true of one.
  if (record.envelope === true) {
    parts.push(`envelope of ${(record.averagesTaken ?? 1).toLocaleString()} records`)
    return parts.join(' · ')
  }
  const raw = record.channels.some((c) => c.raw)
  parts.push(raw ? 'every sample drawn' : 'min/max per column')
  return parts.join(' · ')
})

/**
 * The one thing about this record that the picture cannot say for itself — a badge, not a sentence.
 *
 * <p>Both of these make an ordinary-looking trace mean something else, so they have to be on the
 * chart and not only in the Measurements panel. They were a paragraph above the plot, and a
 * paragraph that comes and goes <b>resizes the chart</b>: the height is the view less this card's
 * own furniture, so two lines appearing took two lines off the canvas — and in Auto, where a record
 * is forced only when nothing crossed, that happened per capture. A badge on the header row costs
 * no height at all, that row already being <code>min-h-6</code> and taller than the badge.</p>
 *
 * <p>The sentence is not lost: it is this badge's tooltip, and the Measurements panel prints it in
 * full under the readings it qualifies.</p>
 */
const recordFlag = computed(() => {
  const record = props.record

  // Ahead of the two flags below, because it changes what every reading on the screen *means*
  // rather than qualifying one capture: an envelope's figures are its own extremes, and an
  // averaged trace's noise floor is not the analyzer's.
  if (record && props.accumulation !== 'Normal')
  {
    const badge = accumulationBadge(
      record.envelope === true ? 'Envelope' : props.accumulation,
      record.averagesTaken ?? 1,
      props.averageCount,
    )
    if (badge) return badge
  }

  if (record?.spliced)
    return {
      label: 'Spliced',
      detail:
        'This record spans a dropped chunk or block — two pieces of the stream sit side by side in it, so an edge in the middle may be the splice rather than the signal.',
    }
  if (record?.rolled)
    return {
      label: 'Rolling',
      detail:
        'There is no trigger in Roll: this is the newest window of the stream, with t = 0 at its right-hand edge and the record running backwards from it. The trigger level, slope and hysteresis do nothing here.',
    }
  if (record?.forced)
    return {
      label: 'Untriggered',
      detail:
        'Nothing crossed the trigger level within the timeout, so this record was taken anyway and is aligned to nothing. Drag the trigger line onto the trace, or switch to Roll.',
    }
  return null
})

/** The window in the one place a reader needs it spelled out — what fromChartMs is for. */
const windowNote = computed(
  () =>
    `${formatScopeTime(xWindowMs.value.min, spanMs.value)} to ${formatScopeTime(xWindowMs.value.max, spanMs.value)}`,
)
</script>

<template>
  <section
    ref="root"
    class="relative flex flex-col gap-1 panel-surface rounded-xl border border-neutral-200 p-3 dark:border-neutral-800"
  >
    <ExpandToggle class="absolute right-3 top-3" :target="root" />
    <div class="min-h-6 pr-9 flex items-center justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
      <span class="shrink-0">Oscilloscope</span>
      <span
        v-if="recordFlag"
        class="shrink-0 rounded bg-amber-500/15 px-1.5 py-0.5 text-[11px] font-medium text-amber-700 dark:text-amber-300"
        :title="recordFlag.detail"
        >{{ recordFlag.label }}</span
      >
      <!-- A badge rather than a line, for recordFlag's reason: a note that comes and goes above the
           plot takes its height off the canvas every time it appears. -->
      <span
        v-if="filterNote"
        class="shrink-0 rounded bg-blue-500/15 px-1.5 py-0.5 text-[11px] font-medium text-blue-700 dark:text-blue-300"
        title="Something in the display path is changing this trace, so every reading beside it is of the filtered waveform. What is stored is always the unfiltered capture."
        >{{ filterNote }}</span
      >
      <span class="truncate">
        {{ timebaseLabel }} · {{ verticalLabel }} · {{ windowNote }} · {{ recordNote }}
      </span>

      <ChartZoomControls
        :zoomed="zoomed"
        :can-go-back="canGoBack"
        reset-title="Back to the time and level window set in the display panel."
        apply-title="Write this window into the display panel's time and level boxes."
        @back="zoomBack"
        @apply="applyZoomToAxis"
        @reset="resetZoom"
      />
      <span v-if="!zoomed" class="shrink-0">Drag to zoom · double-click to reset</span>
    </div>

    <div class="relative" :class="annotationArmed ? 'cursor-crosshair' : ''">
      <div ref="container" class="w-full"></div>
      <ChartAnnotationOverlay :controller="annotationOverlay" />

      <!-- The trigger level's grab handle. Its own element above the canvas, so a press on it never
           reaches uPlot's drag listeners and nothing has to be restored afterwards. -->
      <div
        v-if="handleRect"
        class="absolute flex items-center justify-end gap-1 pr-0.5 cursor-ns-resize select-none touch-none"
        :style="{
          top: `${handleRect.top}px`,
          right: `${handleRect.right}px`,
          width: `${handleRect.width}px`,
          height: `${handleRect.height}px`,
        }"
        :title="`Trigger level ${triggerReadout} — drag to move it`"
        @pointerdown="onHandlePointerDown"
        @pointermove="onHandlePointerMove"
        @pointerup="onHandlePointerUp"
        @pointercancel="onHandlePointerUp"
      >
        <span
          v-if="dragging"
          class="rounded px-1 text-[10px] font-medium tabular-nums text-white"
          :style="{ backgroundColor: triggerColor }"
        >{{ triggerReadout }}</span>
        <!-- An arrow rather than a bar when the level is off the window: the line is clamped to the
             edge and this is how a bench instrument says the rest of it is that way. -->
        <span
          class="grid h-[14px] w-[14px] place-items-center rounded-sm text-[9px] font-bold leading-none text-white"
          :style="{ backgroundColor: triggerColor }"
          >{{ indicator.offWindow === 'above' ? '▲' : indicator.offWindow === 'below' ? '▼' : 'T' }}</span
        >
      </div>

      <!-- The cursors' grips. Each is its own element above the canvas, for the trigger handle's
           reason, and they sit on the two edges nothing else claims: time along the bottom, level
           down the left. -->
      <div
        v-for="handle in cursorHandles"
        :key="handle.id"
        class="absolute grid place-items-center rounded-sm text-[10px] font-bold leading-none text-white select-none touch-none"
        :class="handle.axis === 'time' ? 'cursor-ew-resize' : 'cursor-ns-resize'"
        :style="{
          left: `${handle.left}px`,
          top: `${handle.top}px`,
          width: `${CURSOR_HANDLE_PX}px`,
          height: `${CURSOR_HANDLE_PX}px`,
          backgroundColor: cursorColor,
        }"
        :title="`Cursor ${handle.label} ${cursorReadout(handle.id)} — drag to move it`"
        @pointerdown="onCursorPointerDown($event, handle.id)"
        @pointermove="onCursorPointerMove"
        @pointerup="onCursorPointerUp"
        @pointercancel="onCursorPointerUp"
      >
        {{ handle.label }}
      </div>
    </div>
  </section>
</template>
