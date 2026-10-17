<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { OverlayEntry } from '../composables/useMeasurements'
import type { ResponseDelayView, ResponsePhaseKindName } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { DELAY_INCREMENTS, chooseDelayUnit, delayLabel, delayScale, delayValuesOf, usToUnit } from '../response/delayAxis'
import { sweepOverlayDash } from '../measurements/sweepOverlayStyles'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import {
  CHART_EXPORT_HEADER_PX,
  composeChartPng,
  nextPaint,
  type ChartExportOptions,
  type ExportLegendEntry,
} from '../charts/exportChart'
import { makeChartPalette, type ChartPalette } from '../charts/palette'
// The dashed verticals and the double-headed arrow the ±1 dB / −3 dB readings are drawn with —
// shared with the display rail's swatches (charts/bandwidthColors.ts).
import { BANDWIDTH_COLORS } from '../charts/bandwidthColors'
import { axisLabel } from '../charts/exportChart'
import ExpandToggle from './ExpandToggle.vue'
import ToolbarIcon from './ToolbarIcon.vue'
import { freqAxis } from '../freqAxis'
import { formatHz } from '../response/format'
import { useAppTheme } from '../composables/useAppTheme'
import { phaseColumnUsable, phaseFloorDb, phaseSeries } from '../response/phaseCurve'

const props = defineProps<{
  traces: ResponseTrace[]
  /** Stored measurements checked on in the History list, drawn dashed behind the live curves. */
  overlays: OverlayEntry[]
  /**
   * 'Dbv' shows each input's own level; 'Gain' shows left relative to the reference; 'Crosstalk'
   * shows the crosstalk trace(s) — victim relative to aggressor, one per direction measured — on
   * the Gain (dB) window, with no bandwidth markers (a crosstalk curve has no corners).
   */
  view: 'Dbv' | 'Gain' | 'Crosstalk'
  showLeft: boolean
  showRight: boolean
  showPhase: boolean
  showCoherence: boolean
  showOneDb: boolean
  showThreeDb: boolean
  thickLines: boolean
  showDataPoints: boolean
  /** The Gain (dB) view's own y window — independent of yTopDbv/yBottomDbv below so switching views never clobbers the other one's range. */
  yTopDb: number
  yBottomDb: number
  /** The FR (dBV) view's own y window. */
  yTopDbv: number
  yBottomDbv: number
  xFromHz: number
  xToHz: number
  /** The phase axis' own window, in degrees — set in the display panel, independent of the dB one because unwrapped phase can run past ±180°. */
  phaseTopDeg: number
  phaseBottomDeg: number
  /** What phase this run measured — decides which curve carries it and what it is called. See ResponsePhaseKind. */
  phaseKind: ResponsePhaseKindName
  /**
   * Which delay curve rides the delay axis, if any, and that axis' window in microseconds. Optional
   * so the crosstalk screen, which has no phase to derive a delay from, need not say so.
   */
  delayView?: ResponseDelayView
  delayTopUs?: number
  delayBottomUs?: number
  /** The button that starts this screen's measurement — RUN on the chirp, RUN SWEEP on the stepped sine. */
  runLabel?: string
  /**
   * Axis titles on screen, not only on the export. Every screen that mounts this chart asks for
   * them: five stacked quantities — dB, degrees, coherence, a delay in µs or ms, and a log
   * frequency — share one plot here, and the display panel beside it names what is *drawn* rather
   * than what each of the three right-hand axes measures. Left out and the dropdowns are the only
   * thing saying which axis is which; reported 2026-09-25. Still a prop rather than a constant, so
   * an embedding with no room for them can say so.
   */
  axisTitles?: boolean
  /**
   * The pinned cursor's frequency, or null when nothing is pinned — one of this chart's own plotted
   * columns, emitted by `cursorPin` below and owned by the parent, which is what makes it survive
   * the pointer leaving the plot. Drawn here as a vertical line and held in the readout above;
   * on the stepped-sine screen it also holds the two preview charts in the rail, which is what it
   * was asked for (Joost, 2026-10-11).
   *
   * Absent on the report's headless render, which has no cursor and draws no line.
   */
  pinnedX?: number | null
  /**
   * Put the chirp's removed bulk delay back on the phase (and so on the delay curves) — the display
   * panel's **Restore removed delay**, which the complex views have always had and the Bode plot
   * gained 2026-10-17.
   *
   * <p>The estimator de-rotates every record by an estimated latency before accumulating, and on a
   * minimum-phase DUT that latency <em>is</em> most of its phase: measured against the simulator's
   * 5th-order Butterworth, the passband reads 8–10° with it removed and the textbook 0 → −450°
   * through the corner with it back. Off by default, because excess phase is the right reading for
   * a DUT whose own delay is the thing being removed — a loudspeaker, a converter, anything behind
   * a buffer.</p>
   *
   * <p>Null latency on a stepped run: `arg(L/R)` comes from two channels of one capture, so the
   * record's arbitrary start offset is common-mode and there is nothing to restore.</p>
   */
  restoreDelay?: boolean
  latencySamples?: number | null
  sampleRateHz?: number | null
}>()

/**
 * A click on the plot: the nearest plotted column's frequency, or null to release.
 *
 * <p>Separate from the hover (which stays internal to this component, since the readout is drawn
 * here) because a pin is a choice that has to outlive the pointer: the whole point of it is that
 * reaching for a preview chart's expand button, or simply looking away, no longer takes the reading
 * with it.</p>
 */
const emit = defineEmits<{ cursorPin: [freqHz: number | null] }>()

function clearPin() {
  emit('cursorPin', null)
}

const { isDark } = useAppTheme()

const root = ref<HTMLDivElement | null>(null)
const plotBox = ref<HTMLDivElement | null>(null)
const container = ref<HTMLDivElement | null>(null)
const plot = shallowRef<uPlot | null>(null)

/**
 * Fitted to the view — see charts/useResultChartHeight.ts. The plot is sized from the box's own
 * height, so this is set as an inline style rather than baked into the uPlot options: the
 * ResizeObserver below then keeps reading one number, whether the change came from here or from
 * the window.
 */
const chartHeight = useResultChartHeight(root, plotBox)

/**
 * Hue is the quantity, matching the sweep chart's convention: the gain curve is the one the
 * bandwidth markers refer to and gets the neutral colour, the two input curves take the same
 * blue/orange the rest of the app uses for left and right.
 */
const TRACE_COLORS = {
  Gain: ['#111827', '#f3f4f6'],
  Left: ['#2563eb', '#60a5fa'],
  Right: ['#ea580c', '#fb923c'],
  // Coloured by the *aggressor*: blue when the left channel drives, orange when the right does —
  // the same L/R hues, read as "which channel was playing".
  CrosstalkLeftToRight: ['#2563eb', '#60a5fa'],
  CrosstalkRightToLeft: ['#ea580c', '#fb923c'],
} as const

/** What a curve is called in the legend and the readout. */
function traceLabel(kind: ResponseTrace['kind']): string {
  switch (kind) {
    case 'CrosstalkLeftToRight':
      return 'L→R'
    case 'CrosstalkRightToLeft':
      return 'R→L'
    default:
      return kind
  }
}

function isCrosstalkTrace(kind: ResponseTrace['kind']): boolean {
  return kind === 'CrosstalkLeftToRight' || kind === 'CrosstalkRightToLeft'
}

const PHASE_COLOR = ['#7c3aed', '#a78bfa'] as const
const COHERENCE_COLOR = ['#059669', '#34d399'] as const
/** Pink: distinct from the violet phase, the green coherence, the blue/orange inputs and the amber/blue markers. */
const DELAY_COLOR = ['#db2777', '#f472b6'] as const

/**
 * The pinned cursor's vertical — [light, dark] like every pair here, and deliberately the inverse of
 * the gridline it crosses (`#e5e5e5` light / `#404040` dark), so it is the highest-contrast line on
 * the plot in either theme.
 *
 * <p>Neutral rather than a colour, and the same pair the sweep chart uses: the pin is chrome, not a
 * reading. A colour would also have to dodge seven others on this chart — the two inputs, the gain,
 * phase, coherence, delay and the amber ±1 dB markers — and the one it picked would read as an
 * eighth curve.</p>
 */
const PINNED_CURSOR_COLOR = ['#404040', '#e5e5e5'] as const

// See charts/palette.ts for why the palette is an argument and not a module-level flag.
type Palette = ChartPalette
const makePalette = makeChartPalette

/** The screen's palette, following the app theme. */
const palette = computed(() => makePalette(isDark.value))

const gainTrace = computed(() => props.traces.find((t) => t.kind === 'Gain') ?? null)
const leftTrace = computed(() => props.traces.find((t) => t.kind === 'Left') ?? null)
const rightTrace = computed(() => props.traces.find((t) => t.kind === 'Right') ?? null)

/**
 * The curves currently on screen, in draw order. In gain view there is exactly one; in dBV view
 * there are the two inputs, subject to their checkboxes.
 */
const visibleTraces = computed<ResponseTrace[]>(() => {
  if (props.view === 'Crosstalk') return props.traces.filter((t) => isCrosstalkTrace(t.kind))
  if (props.view === 'Gain') return gainTrace.value ? [gainTrace.value] : []

  const shown: ResponseTrace[] = []
  if (props.showLeft && leftTrace.value) shown.push(leftTrace.value)
  if (props.showRight && rightTrace.value) shown.push(rightTrace.value)
  return shown
})

/**
 * One curve per checked-on stored measurement, of the same kind the live view is showing — a stored
 * gain curve belongs under the live gain curve, not under the dBV one. Dash pattern comes from the
 * shared overlay styling, so a run is identified the same way here as on the sweep screen.
 */
/**
 * Whether the live measurement occupies run 0 — see runDash. With nothing measured, the first
 * checked history item is promoted to the solid line instead of every curve on screen being dashed.
 */
const liveDrawn = computed(() => visibleTraces.value.length > 0)

const overlayCurves = computed(() => {
  // A stored crosstalk result may hold both directions; each is its own curve, coloured by its
  // kind and dashed by the overlay, so two stored runs of both directions are still four
  // distinguishable lines.
  if (props.view === 'Crosstalk') {
    return props.overlays.flatMap((overlay) =>
      (overlay.responseTraces ?? [])
        .filter((t) => isCrosstalkTrace(t.kind))
        .map((trace) => ({ id: overlay.id, name: `${overlay.detail.name} ${traceLabel(trace.kind)}`, trace })),
    )
  }
  return props.overlays
    .map((overlay) => {
      const wanted: ResponseTrace['kind'] = props.view === 'Gain' ? 'Gain' : props.showLeft ? 'Left' : 'Right'
      const trace = overlay.responseTraces?.find((t) => t.kind === wanted)
      return trace ? { id: overlay.id, name: overlay.detail.name, trace } : null
    })
    .filter((entry): entry is { id: number; name: string; trace: ResponseTrace } => entry !== null)
})

/**
 * The curve the bandwidth markers and the readout table describe — whichever one the view is about.
 * Null in the crosstalk view: nothing there has corners, and a primary would only draw arrows.
 */
const primaryTrace = computed<ResponseTrace | null>(() => {
  if (props.view === 'Crosstalk') return null
  if (props.view === 'Gain') return gainTrace.value
  return (props.showLeft ? leftTrace.value : null) ?? rightTrace.value ?? leftTrace.value
})

/**
 * Phase gets its own scale and its own axis on the right, in degrees — the classic Bode layout, and
 * the only way a phase reading can actually be read off the plot. It was previously squeezed onto
 * the dB window, which put a number next to it that meant nothing.
 *
 * The window is a display-panel setting (props.phaseTopDeg/phaseBottomDeg) rather than fitted to the
 * data, so it stays put while a run comes in instead of rescaling under the curve; the panel's own
 * Autofit button covers the case the old auto-fit did. Independent of the dB window because the
 * phase here is *unwrapped* and the bulk delay already removed: a well-behaved DUT sits inside a few
 * tens of degrees, while a DUT with real excess phase runs well past 180° — the two cannot share one
 * pair of bounds.
 */
const phaseRange = computed<[number, number]>(() => [props.phaseBottomDeg, props.phaseTopDeg])

/**
 * The y window actually in effect — the FR (dBV) pair or the Gain (dB) pair, whichever `view` is
 * showing. The two are independent so switching views never clobbers the other one's range.
 */
const activeYTop = computed(() => (props.view === 'Dbv' ? props.yTopDbv : props.yTopDb))
const activeYBottom = computed(() => (props.view === 'Dbv' ? props.yBottomDbv : props.yBottomDb))

/**
 * Whether any currently-visible magnitude trace, within the plotted frequency window, sits above
 * or below the dB/dBV window — the cue that there is real data off the top or bottom of the chart.
 * Scoped to the primary magnitude axis only, not phase/coherence.
 */
const outOfRange = computed(() => {
  let above = false
  let below = false
  for (const trace of visibleTraces.value) {
    for (let i = 0; i < trace.freqHz.length; i++) {
      const freqHz = trace.freqHz[i]
      if (freqHz < props.xFromHz || freqHz > props.xToHz) continue
      if (trace.magnitudeMaxDb[i] > activeYTop.value) above = true
      if (trace.magnitudeMinDb[i] < activeYBottom.value) below = true
      if (above && below) return { above, below }
    }
  }
  return { above, below }
})

/**
 * The curve the phase series is drawn from — <b>not</b> `primaryTrace</b>, which is what the view is
 * about rather than what was measured.
 *
 * Phase is a property of the measurement, not of whichever magnitude curve is on screen. Sourcing it
 * from `primaryTrace` meant FR (dBV) view plotted the Left trace's phase, and for a stepped sine that
 * is a genuine array of zeros (only the Gain trace is a relationship between the two inputs) — drawn
 * as a solid 0° line, indistinguishable from a DUT with no phase shift at all. `PerTrace` is the one
 * case where the trace on screen really does carry its own: the chirp estimates Left and Right
 * against the synthesised sweep.
 */
const phaseTrace = computed<ResponseTrace | null>(() => {
  if (props.phaseKind !== 'PerTrace') return gainTrace.value
  // The curve on screen, and never one that is not. `primaryTrace` stood here, and its fallback
  // chain reaches the *right* trace the moment the Left checkbox is cleared -- which with the right
  // input as the reference is a curve the chart refuses to draw at all, being the stimulus leg. Its
  // phase is flat 0 by construction, so clearing Left silently replaced the DUT's phase with a
  // straight line through the middle of the plot. Reported 2026-10-17 as the phase appearing only
  // once Left was unticked; what appeared was the generator's own leg.
  return visibleTraces.value[0] ?? leftTrace.value ?? rightTrace.value
})

/**
 * The phase series the chart draws: the trace's own phase, the removed delay back on it when the
 * display panel asks for that, and a gap wherever the response is into the noise. The rule lives in
 * `response/phaseCurve.ts` because the panel's Autofit has to size the axis to the very same curve
 * — and because nothing in a `.vue` is testable here.
 */
function phaseSeriesOf(trace: ResponseTrace | null, length: number): (number | null)[] {
  return phaseSeries(trace, props.phaseKind, restoreOptions(), length)
}

/** The three props `delayDegPerHz` reads, as one object. */
function restoreOptions() {
  return { restoreDelay: props.restoreDelay, latencySamples: props.latencySamples, sampleRateHz: props.sampleRateHz }
}

/** What the phase curve is entitled to be called — a difference between two measured paths is not a DUT's phase response. */
const phaseLabel = computed(() => (props.phaseKind === 'ChannelDifference' ? 'Phase difference' : 'Phase'))

/**
 * The delay curve, drawn from the same trace as the phase: it is the phase's derivative (group) or
 * its ratio to frequency (phase delay), computed server-side at full resolution, so it belongs to
 * the measurement exactly the way the phase does. One curve at a time on one axis — group delay and
 * phase delay differ by orders of magnitude on a DUT with a high-pass corner and would each need a
 * window of their own.
 *
 * The window is held in microseconds and shown in µs or ms as its size calls for; the unit follows
 * the *window*, not the data, so the axis cannot flip units as a run settles.
 */
const delayView = computed<ResponseDelayView>(() => props.delayView ?? 'None')
const delayShown = computed(() => delayView.value !== 'None')
const delayUnit = computed(() => chooseDelayUnit(props.delayTopUs ?? 1000, props.delayBottomUs ?? -1000))
const delayRange = computed<[number, number]>(() => [
  usToUnit(props.delayBottomUs ?? -1000, delayUnit.value),
  usToUnit(props.delayTopUs ?? 1000, delayUnit.value),
])
const delayLabelText = computed(() => delayLabel(delayView.value, props.phaseKind === 'ChannelDifference'))

/**
 * The selected delay curve of a trace in the axis' unit; all-null (a gap, not a zero line) when the
 * trace never recorded one. Blanked column for column with the phase it is the derivative of —
 * where the phase is noise the delay is its slope, which is noise multiplied by the bin spacing.
 */
function delaySeriesOf(trace: ResponseTrace | null, length: number): (number | null)[] {
  const values = delayValuesOf(trace, delayView.value)
  if (!trace || !values || values.length !== length) return new Array<number | null>(length).fill(null)
  const floorDb = phaseFloorDb(trace, props.phaseKind)
  const scale = delayScale(delayUnit.value)
  return Array.from(values, (v, i) =>
    Number.isFinite(v) && phaseColumnUsable(trace, i, floorDb) ? v * scale : null,
  )
}

/**
 * A stored result's delay curve, from whichever of its traces carries a phase under this run's
 * phase kind (the trace on screen under PerTrace, the gain curve otherwise), resampled onto the
 * live grid like its magnitude. Null throughout when the result predates the delay curves.
 */
function overlayDelaySeries(overlayId: number, shownKind: ResponseTrace['kind'], grid: Float64Array): (number | null)[] {
  const traces = props.overlays.find((o) => o.id === overlayId)?.responseTraces ?? []
  const wanted: ResponseTrace['kind'] = props.phaseKind === 'PerTrace' ? shownKind : 'Gain'
  const source = traces.find((t) => t.kind === wanted) ?? null
  const values = delayValuesOf(source, delayView.value)
  if (!source || !values || values.length !== source.freqHz.length) return new Array<number | null>(grid.length).fill(null)
  const scale = delayScale(delayUnit.value)
  return interpolateOnto(source.freqHz, values, grid).map((v) => (v === null ? null : v * scale))
}

/**
 * Draws the min of each column as a vertical stroke under its max, exactly as the spectrum chart's
 * band does. Both bounds must be drawn: the line alone is the max over the bins in that column, so
 * a rippled response would be redrawn riding along the top of its own ripple rather than through
 * the middle of it.
 */
function minMaxBandPlugin(p: Palette): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          const ctx = u.ctx
          ctx.save()
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          for (const trace of visibleTraces.value) {
            const path = new Path2D()
            for (let i = 0; i < trace.freqHz.length; i++) {
              const freqHz = trace.freqHz[i]
              if (!(freqHz > 0)) continue

              const x = u.valToPos(freqHz, 'x', true)
              path.moveTo(x, u.valToPos(trace.magnitudeMinDb[i], 'y', true))
              path.lineTo(x, u.valToPos(trace.magnitudeMaxDb[i], 'y', true))
            }

            ctx.strokeStyle = p.color(TRACE_COLORS[trace.kind])
            ctx.globalAlpha = 0.35
            ctx.lineWidth = 1
            ctx.stroke(path)
            ctx.globalAlpha = 1
          }

          ctx.restore()
        },
      ],
    },
  }
}

/**
 * The bandwidth markers: a dashed vertical at each corner, a double-headed arrow between them, and
 * a centred label — the previous app's `DrawBandwithLines`, which is what makes a bandwidth
 * readable off the plot rather than only out of the table.
 *
 * A clamped end is drawn at the edge of the measured band and labelled with a "&gt;", because the
 * curve never reached the threshold there: it is where the search stopped, not a corner.
 */
function bandwidthPlugin(p: Palette): uPlot.Plugin {
  return {
    hooks: {
      draw: [
        (u) => {
          const trace = primaryTrace.value
          if (!trace) return

          const ctx = u.ctx
          ctx.save()
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          const readings: { label: string; reading: ResponseTrace['oneDb']; palette: readonly string[]; row: number }[] = []
          // The labels are the Measurements panel's own, not "B1"/"B3": the reader has that panel
          // beside the chart, and two names for one reading is one too many. "±" on the 1 dB row
          // because that search is symmetric and the 3 dB one is not — see the bandwidth rules.
          if (props.showOneDb) readings.push({ label: '±1 dB', reading: trace.oneDb, palette: BANDWIDTH_COLORS.oneDb, row: 0 })
          if (props.showThreeDb) readings.push({ label: '−3 dB', reading: trace.threeDb, palette: BANDWIDTH_COLORS.threeDb, row: 1 })

          // Only the corners and the arrow are drawn. The reference level and the thresholds the
          // corners were found against used to be drawn as horizontals too (a grey `ref` line and a
          // dotted line per threshold), and were removed: on a trace with several readings and a
          // phase curve they read as more curves, and the Measurements panel already quotes the
          // reference level in numbers.
          for (const { label, reading, palette, row } of readings) {
            const stroke = p.color(palette)
            const xLow = u.valToPos(Math.max(reading.lowerHz, u.scales.x.min ?? 1), 'x', true)
            const xHigh = u.valToPos(Math.min(reading.upperHz, u.scales.x.max ?? reading.upperHz), 'x', true)

            // Rows are spaced from the top so the two arrows never overlap each other.
            const y = u.bbox.top + u.bbox.height * (0.18 + row * 0.14)

            ctx.strokeStyle = stroke
            ctx.fillStyle = stroke
            ctx.lineWidth = 1

            ctx.setLineDash([4, 3])
            for (const x of [xLow, xHigh]) {
              ctx.beginPath()
              ctx.moveTo(x, y)
              ctx.lineTo(x, u.bbox.top + u.bbox.height)
              ctx.stroke()
            }

            // Dashed, like the drops it joins: the span is a *reading off* the curve, and a solid
            // rule across the plot reads as one more trace (Joost, 2026-10-12). The dash is left
            // set from the drops above.
            ctx.beginPath()
            ctx.moveTo(xLow, y)
            ctx.lineTo(xHigh, y)
            ctx.stroke()
            ctx.setLineDash([])

            const head = 5
            for (const [x, dir] of [
              [xLow, 1],
              [xHigh, -1],
            ] as const) {
              ctx.beginPath()
              ctx.moveTo(x, y)
              ctx.lineTo(x + dir * head, y - head * 0.7)
              ctx.lineTo(x + dir * head, y + head * 0.7)
              ctx.closePath()
              ctx.fill()
            }

            const text = `${label} · ${formatHz(reading.bandwidthHz)}${
              reading.lowerClamped || reading.upperClamped ? ' (partial)' : ''
            }`
            ctx.font = '11px ui-sans-serif, system-ui, sans-serif'
            ctx.textAlign = 'center'
            ctx.textBaseline = 'middle'

            // A rounded chip on the line rather than a square patch — the same 6 px corner every
            // badge in the app carries, so the reading reads as a label and not as a hole in the
            // plot. `roundRect` is Chromium 99+; the app ships a WebView of its own, and the
            // fallback is there for a browser old enough not to have it rather than as a real case.
            const midX = (xLow + xHigh) / 2
            const width = ctx.measureText(text).width + 14
            const box = [midX - width / 2, y - 9, width, 18] as const
            ctx.beginPath()
            if (typeof ctx.roundRect === 'function') ctx.roundRect(box[0], box[1], box[2], box[3], 6)
            else ctx.rect(box[0], box[1], box[2], box[3])
            ctx.fillStyle = p.labelBackground
            ctx.fill()
            ctx.stroke()
            ctx.fillStyle = stroke
            ctx.fillText(text, midX, y)
          }

          ctx.restore()
        },
      ],
    },
  }
}

/**
 * The shared x grid. Every curve of one measurement was decimated on the same column boundaries, so
 * the first one's frequencies serve for all of them — but they are read from the data rather than
 * rebuilt from a log-spacing formula, for the reason streaming/protocol.ts documents.
 */
function buildData(): uPlot.AlignedData {
  // Falls back to an overlay's own grid when nothing has been measured yet: checking a stored
  // result on before running anything is a normal thing to do, and without this the chart has no x
  // axis to resample onto and silently draws nothing.
  const grid = visibleTraces.value[0] ?? primaryTrace.value ?? overlayCurves.value[0]?.trace
  if (!grid || grid.freqHz.length === 0) return [[], []]

  const x = Float64Array.from(grid.freqHz)
  const series: (Float64Array | (number | null)[])[] = []

  for (const trace of visibleTraces.value) series.push(Float64Array.from(trace.magnitudeMaxDb))

  // Both go out in their own units now that each has its own scale — degrees and 0..1 — so the
  // cursor and the axis agree with each other.
  if (props.showPhase) series.push(phaseSeriesOf(phaseTrace.value, x.length))

  if (props.showCoherence) {
    const source = primaryTrace.value
    series.push(source ? Float64Array.from(source.coherence) : new Float64Array(x.length))
  }

  if (delayShown.value) series.push(delaySeriesOf(phaseTrace.value, x.length))

  // Overlays last, so they draw behind nothing and their series indices are stable against the
  // optional phase/coherence/delay traces above. An overlay's delay curve follows its magnitude,
  // so the two lists stay in the same order buildSeries and buildReadoutSeries walk them.
  for (const overlay of overlayCurves.value) series.push(resampleOnto(overlay.trace, x))
  if (delayShown.value) {
    for (const overlay of overlayCurves.value) series.push(overlayDelaySeries(overlay.id, overlay.trace.kind, x))
  }

  return [x, ...series] as uPlot.AlignedData
}

/** Linear interpolation in log frequency of an arbitrary array onto the live grid; null outside the source's band. */
function interpolateOnto(sourceHz: number[], values: number[], grid: Float64Array): (number | null)[] {
  const out: (number | null)[] = new Array(grid.length)
  let j = 0

  for (let i = 0; i < grid.length; i++) {
    const f = grid[i]
    if (sourceHz.length < 2 || f < sourceHz[0] || f > sourceHz[sourceHz.length - 1]) {
      out[i] = null
      continue
    }

    while (j < sourceHz.length - 2 && sourceHz[j + 1] < f) j++

    const f0 = sourceHz[j]
    const f1 = sourceHz[j + 1]
    const t = f1 > f0 ? (Math.log10(f) - Math.log10(f0)) / (Math.log10(f1) - Math.log10(f0)) : 0
    out[i] = values[j] + t * (values[j + 1] - values[j])
  }

  return out
}

/**
 * A stored curve on the live curve's frequency grid. The two are decimated independently — a
 * different span or resolution puts the columns in different places — so the stored one is linearly
 * interpolated in log frequency rather than assumed to line up. Outside its own measured band it
 * yields null, which uPlot renders as a gap: a stored 24 kHz measurement must not be drawn as a flat
 * line across a 96 kHz one.
 */
function resampleOnto(trace: ResponseTrace, grid: Float64Array): (number | null)[] {
  const out: (number | null)[] = new Array(grid.length)
  const source = trace.freqHz
  let j = 0

  for (let i = 0; i < grid.length; i++) {
    const f = grid[i]
    if (source.length === 0 || f < source[0] || f > source[source.length - 1]) {
      out[i] = null
      continue
    }

    while (j < source.length - 2 && source[j + 1] < f) j++

    const f0 = source[j]
    const f1 = source[j + 1]
    const t = f1 > f0 ? (Math.log10(f) - Math.log10(f0)) / (Math.log10(f1) - Math.log10(f0)) : 0
    out[i] = trace.magnitudeMaxDb[j] + t * (trace.magnitudeMaxDb[j + 1] - trace.magnitudeMaxDb[j])
  }

  return out
}

function buildSeries(p: Palette): uPlot.Series[] {
  const width = props.thickLines ? 2 : 1
  const points = { show: props.showDataPoints, size: 3 }

  const series: uPlot.Series[] = [{}]

  for (const trace of visibleTraces.value) {
    series.push({
      label: traceLabel(trace.kind),
      stroke: p.color(TRACE_COLORS[trace.kind]),
      width,
      points,
    })
  }

  if (props.showPhase) {
    series.push({
      label: phaseLabel.value,
      scale: 'phase',
      stroke: p.color(PHASE_COLOR),
      width,
      dash: [4, 3],
      points: { show: false },
    })
  }

  if (props.showCoherence) {
    series.push({
      label: 'Coherence',
      scale: 'coh',
      stroke: p.color(COHERENCE_COLOR),
      width,
      dash: [2, 2],
      points: { show: false },
    })
  }

  if (delayShown.value) {
    series.push({
      label: delayLabelText.value,
      scale: 'delay',
      stroke: p.color(DELAY_COLOR),
      width,
      dash: [10, 4],
      points: { show: false },
    })
  }

  for (const overlay of overlayCurves.value) {
    series.push({
      label: overlay.name,
      // In the crosstalk view a stored curve keeps its direction's colour, so an overlay's L→R
      // reads as an L→R; elsewhere overlays are the neutral gain colour and told apart by dash.
      stroke: p.color(props.view === 'Crosstalk' ? TRACE_COLORS[overlay.trace.kind] : TRACE_COLORS.Gain),
      // The same width and markers as the live trace it is there to be compared against: an overlay
      // drawn at a fixed weight ignored both checkboxes, so Thick lines and Data points changed only
      // half the chart and the comparison was between two differently-drawn curves.
      width,
      // Null screen: these overlays carry curves, not sweep points, so this is always undefined --
      // a Bode plot tells its curves apart by position. The polar chart uses responseOverlayDash.
      dash: sweepOverlayDash(props.overlays, overlay.id, liveDrawn.value, null) ?? undefined,
      points,
      alpha: liveDrawn.value ? 0.7 : 1,
    })
  }

  if (delayShown.value) {
    for (const overlay of overlayCurves.value) {
      series.push({
        label: `${overlay.name} ${delayLabelText.value.toLowerCase()}`,
        scale: 'delay',
        stroke: p.color(DELAY_COLOR),
        // Markers stay off here, as they are on the live delay curve — this follows the live
        // series it mirrors, not the magnitude's.
        width,
        // Null screen: these overlays carry curves, not sweep points, so this is always undefined --
      // a Bode plot tells its curves apart by position. The polar chart uses responseOverlayDash.
      dash: sweepOverlayDash(props.overlays, overlay.id, liveDrawn.value, null) ?? undefined,
        points: { show: false },
        alpha: liveDrawn.value ? 0.7 : 1,
      })
    }
  }

  return series
}

/**
 * One row of the cursor readout: every curve on the plot, at the frequency under the pointer.
 *
 * Built from the same three lists buildSeries walks, in the same order, because the readout indexes
 * uPlot's own data arrays — entry i is `u.data[i + 1]`. Nothing here can drift from the plot: every
 * input to this list is part of the rebuild signature below, so a change to it replaces the plot
 * whose data the readout is about to index.
 */
interface ReadoutSeries {
  key: string
  label: string
  color: string
  /** Appended with a space, except the degree sign — "12.3°", but "12.30 dB". */
  unit: string
  digits: number
}

function buildReadoutSeries(p: Palette): ReadoutSeries[] {
  // In gain view the one curve is a ratio; in dBV view the curves are absolute input levels. The
  // axis does not say which, because it is the same numbers either way — the readout is the only
  // place the distinction is written down.
  const magnitudeUnit = props.view === 'Dbv' ? 'dBV' : 'dB'
  const entries: ReadoutSeries[] = []

  for (const trace of visibleTraces.value) {
    entries.push({
      key: `live-${trace.kind}`,
      label: traceLabel(trace.kind),
      color: p.color(TRACE_COLORS[trace.kind]),
      unit: magnitudeUnit,
      digits: 2,
    })
  }

  if (props.showPhase) {
    entries.push({ key: 'phase', label: phaseLabel.value, color: p.color(PHASE_COLOR), unit: '°', digits: 1 })
  }

  if (props.showCoherence) {
    entries.push({ key: 'coherence', label: 'Coh', color: p.color(COHERENCE_COLOR), unit: '', digits: 3 })
  }

  const delayDigits = delayUnit.value === 'ms' ? 3 : 1
  if (delayShown.value) {
    entries.push({ key: 'delay', label: delayLabelText.value, color: p.color(DELAY_COLOR), unit: delayUnit.value, digits: delayDigits })
  }

  for (const overlay of overlayCurves.value) {
    entries.push({
      key: `overlay-${overlay.id}-${overlay.trace.kind}`,
      label: overlay.name,
      color: p.color(props.view === 'Crosstalk' ? TRACE_COLORS[overlay.trace.kind] : TRACE_COLORS.Gain),
      unit: magnitudeUnit,
      digits: 2,
    })
  }

  if (delayShown.value) {
    for (const overlay of overlayCurves.value) {
      entries.push({
        key: `overlay-delay-${overlay.id}-${overlay.trace.kind}`,
        label: `${overlay.name} ${delayLabelText.value.toLowerCase()}`,
        color: p.color(DELAY_COLOR),
        unit: delayUnit.value,
        digits: delayDigits,
      })
    }
  }

  return entries
}

const readoutSeries = computed<ReadoutSeries[]>(() => buildReadoutSeries(palette.value))

type CursorReadout = { freqHz: number; entries: (ReadoutSeries & { text: string })[] }

const cursorReadout = ref<CursorReadout | null>(null)

/**
 * Every curve's value at one plotted column, for the readout above the chart. Null for an index
 * that is not on the plot — which is what uPlot hands over once the pointer leaves it.
 */
function readoutAt(u: uPlot, idx: number): CursorReadout | null {
  const xs = u.data[0]
  if (idx < 0 || xs == null || xs[idx] == null) return null

  return {
    freqHz: xs[idx] as number,
    entries: readoutSeries.value.map((series, i) => {
      const value = u.data[i + 1]?.[idx]
      return { ...series, text: value == null ? '—' : formatReading(value as number, series) }
    }),
  }
}

/**
 * The plotted column nearest a frequency, or -1 when there is nothing plotted.
 *
 * <p>A scan rather than a binary search: the x array is ascending, but this runs once per pin and
 * once per pass, not per pointer move, and a scan cannot be wrong about a grid that is ever not
 * monotone (an overlay measured on another grid is merged into this one, and the merge is what
 * guarantees the order — not this).</p>
 */
function nearestColumn(u: uPlot, freqHz: number): number {
  const xs = u.data[0]
  if (xs == null || xs.length === 0) return -1

  let best = -1
  let bestDistance = Infinity
  for (let i = 0; i < xs.length; i++) {
    const x = xs[i]
    if (x == null) continue
    const distance = Math.abs((x as number) - freqHz)
    if (distance < bestDistance) {
      bestDistance = distance
      best = i
    }
  }
  return best
}

/**
 * Puts the readout back on the pinned column — after a pin is made or moved, and after every pass
 * of a continuous run, whose new numbers the frozen row would otherwise not show.
 */
function refreshPinnedReadout() {
  const u = plot.value
  if (!u) return
  cursorReadout.value = props.pinnedX == null ? null : readoutAt(u, nearestColumn(u, props.pinnedX))
}

function formatReading(value: number, series: ReadoutSeries): string {
  if (!Number.isFinite(value)) return '—'

  // `(-0.0001).toFixed(2)` is "-0.00", which reads as a signed quantity where there is none — a
  // flat loopback's gain lands there at nearly every column. Same normalisation response/format.ts
  // applies to the readout table, generalised over the digit counts used here.
  const text = value.toFixed(series.digits).replace(/^-(0(?:\.0+)?)$/, '$1')

  if (series.unit === '') return text
  return series.unit === '°' ? `${text}°` : `${text} ${series.unit}`
}

/**
 * Whether the user has dragged a zoom. Drives the Reset zoom button, which is the only visible cue
 * that the axes are no longer the ones the panel's From/To and dB boxes name.
 *
 * <p><b>Recorded when the gesture happens</b>, rather than inferred by comparing the live scales
 * against the panel's window the way the shared `useZoomBounds` helper does. That comparison cannot
 * work on this chart, because its x axis was never at the panel's window to begin with — see the
 * range callbacks in `buildOptions`. It read "zoomed" from the first frame instead, which is what
 * put a Reset zoom button on an untouched chart.</p>
 *
 * <p>The x range callback consults it, so it has to be settled <i>before</i> the scale it guards is
 * computed. uPlot obliges: `mouseUp` fires `setSelect` before it calls `setScale` for the drag.</p>
 */
const zoomed = ref(false)

/**
 * Back to the window the display panel asks for — the Reset zoom button, and a double-click.
 *
 * <p>Clearing the flag first is not tidying: the range callback reads it, so a `setScale` made while
 * it was still true would be handed straight back out again.</p>
 */
function resetZoom() {
  const u = plot.value
  if (!u) return

  zoomed.value = false
  u.setScale('x', { min: props.xFromHz, max: props.xToHz })
  u.setScale('y', { min: activeYBottom.value, max: activeYTop.value })
}

/**
 * A signature of everything uPlot cannot be told about without rebuilding — the series list and
 * their styling. Data changes go through setData on the existing instance; only this forces a new
 * plot, per the project's "never re-create the plot per frame" rule.
 *
 * The four axis-window props are in here as well, and that is a consequence of the zoom rather than
 * of anything uPlot needs: the scale range callbacks now pass a pending window through instead of
 * always returning the props (which is what let every drag be undone the instant it was made), so a
 * changed dropdown no longer reaches the axis by itself. A rebuild is how it does — and it is also
 * the right behaviour, since the user moving a bound is the user saying which window they want, and
 * restoring the old zoom over it would make the panel look broken. See windowKey below.
 */
const signature = computed(() =>
  [
    props.view,
    visibleTraces.value.map((t) => t.kind).join(','),
    props.showPhase,
    // The series label is baked in at build time, so a change of kind needs a rebuild rather than a
    // redraw -- and the kind also decides which curve the phase is read from.
    props.phaseKind,
    props.showCoherence,
    // The delay series' label and unit are baked into the series and the axis, so a change of
    // either is a rebuild; the window itself is a refresh (see the delayRange watch).
    delayView.value,
    delayUnit.value,
    overlayCurves.value.map((o) => o.id).join(','),
    props.thickLines,
    props.showDataPoints,
    isDark.value,
    props.xFromHz,
    props.xToHz,
    activeYBottom.value,
    activeYTop.value,
  ].join('|'),
)

/**
 * The axis-window half of the signature. A rebuild triggered by anything else — a theme flip, an
 * overlay checkbox, thick lines — has to give the user back the zoom they were looking at; a
 * rebuild triggered by one of these must not.
 */
function xWindowKey(): string {
  return `${props.xFromHz}|${props.xToHz}`
}

function yWindowKey(): string {
  return `${activeYBottom.value}|${activeYTop.value}`
}

let plotXWindowKey = ''
let plotYWindowKey = ''

type ScaleWindow = { min: number; max: number }

/** A live scale's window, or null while it is still unset. */
function windowOf(scale: uPlot.Scale | undefined): ScaleWindow | null {
  return scale?.min != null && scale.max != null ? { min: scale.min, max: scale.max } : null
}

/**
 * @param p The palette every colour is taken from — see Palette.
 * @param interactive False for the export's offscreen plot: no cursor, and none of the hooks that
 *   feed the readout and the zoom flag, which would otherwise fire for a plot nobody can see.
 */
function buildOptions(width: number, height: number, p: Palette, interactive: boolean): uPlot.Options {
  return {
    width,
    height,
    scales: {
      // Both windows *start* pinned to the display panel, and auto:false is what makes that stick:
      // otherwise every setData refits the axes to the data, and the response would crawl as the
      // averages came in.
      //
      // The x axis is the panel's window unless a drag is live, and the `zoomed` guard is the whole
      // of what makes both halves of that true.
      //
      // A pass-through — `min ?? props.xFromHz` — was what stood here, and on the *x* scale it is
      // dead code: uPlot never passes null for x once there is data. This chart hands real data
      // straight to the constructor, so the one call this callback gets carries the **data's own
      // span** (measured: 10..96000 on a 10 Hz-96 kHz sweep), which it then passed through. The
      // panel's From/To were discarded on every rebuild and the dropdowns did nothing at all.
      // Charts that seed an empty plot first, or push the window with an explicit setScale
      // afterwards, get nulls here and never showed it — see the note in CLAUDE.md.
      //
      // Ignoring the arguments outright is the opposite bug, and was what stood here before that:
      // uPlot re-derives x through this callback on *every* commit, including the one carrying the
      // window a drag just asked for, so it fed the props back in and undid the zoom as it was
      // made. Hence a flag rather than a `??` — it separates "uPlot is handing me a zoom" from
      // "uPlot is handing me whatever the axis happened to hold".
      x: {
        time: false,
        distr: 3,
        log: 10,
        auto: false,
        range: (_u, min, max) =>
          zoomed.value && min != null && max != null
            ? [min, max]
            : [props.xFromHz, props.xToHz],
      },
      y: {
        auto: false,
        range: (_u, min, max) => [min ?? activeYBottom.value, max ?? activeYTop.value],
      },
      // The two right-hand scales are deliberately left auto-ranged and are not what a y drag
      // zooms (uPlot zooms the scale of the first plotted series, which is a magnitude curve).
      // Phase is already fitted to its own data, and coherence is a 0..1 quantity whose axis means
      // nothing rescaled.
      phase: { range: () => phaseRange.value },
      coh: { range: () => [0, 1] as [number, number] },
      delay: { range: () => delayRange.value },
    },
    axes: [
      // Axis titles with units on the export, and on screen only where the screen asks for them
      // (`axisTitles`): on paper the picture has to stand on its own.
      {
        ...freqAxis(),
        ...axisLabel(props.axisTitles ?? false, interactive, 'Frequency (Hz)'),
        stroke: p.axis,
        grid: { stroke: p.grid, width: p.gridWidth },
        ticks: { stroke: p.grid, width: p.gridWidth },
      },
      {
        size: 50,
        ...axisLabel(props.axisTitles ?? false, interactive, magnitudeAxisLabel()),
        stroke: p.axis,
        grid: { stroke: p.grid, width: p.gridWidth },
        ticks: { stroke: p.grid, width: p.gridWidth },
      },
      // Right-hand axes, outermost last. Only the visible trace's axis is shown, and neither draws
      // gridlines: a second and third grid over the dB one turns the plot into graph paper, and the
      // dB grid is the one people read a response off.
      {
        scale: 'phase',
        side: 1,
        show: props.showPhase,
        size: 52,
        ...axisLabel(props.axisTitles ?? false, interactive, `${phaseLabel.value} (°)`),
        incrs: [15, 30, 45, 90, 180, 360, 720, 1440],
        values: (_u, vals) => vals.map((v) => `${v}°`),
        stroke: p.color(PHASE_COLOR),
        grid: { show: false },
        ticks: { stroke: p.color(PHASE_COLOR), width: 1 },
      },
      {
        scale: 'coh',
        side: 1,
        show: props.showCoherence,
        size: 44,
        ...axisLabel(props.axisTitles ?? false, interactive, 'Coherence'),
        incrs: [0.1, 0.2, 0.25, 0.5],
        values: (_u, vals) => vals.map((v) => v.toFixed(1)),
        stroke: p.color(COHERENCE_COLOR),
        grid: { show: false },
        ticks: { stroke: p.color(COHERENCE_COLOR), width: 1 },
      },
      {
        scale: 'delay',
        side: 1,
        show: delayShown.value,
        size: 60,
        ...axisLabel(props.axisTitles ?? false, interactive, `${delayLabelText.value} (${delayUnit.value})`),
        incrs: DELAY_INCREMENTS,
        values: (_u, vals) => vals.map((v) => `${v}`),
        stroke: p.color(DELAY_COLOR),
        grid: { show: false },
        ticks: { stroke: p.color(DELAY_COLOR), width: 1 },
      },
    ],
    series: buildSeries(p),
    cursor: interactive
      ? {
          // A dot on every curve at the hovered column, so the numbers in the readout above can be
          // told apart when two curves run close together.
          points: { show: true },
          // Both axes draggable, with `uni` making a drag unidirectional unless it is a real box: a
          // drag that stays under 12 px in one direction zooms the other axis across its full span.
          // Frequency-only is the common gesture (narrowing onto a corner), but a dB window matters
          // just as much on a response that is flat to a fraction of a dB, and demanding a
          // pixel-accurate horizontal drag to get one without the other would make both awkward.
          drag: { x: true, y: true, uni: 12 },
        }
      : { show: false },
    // Same reason as the other charts: uPlot's own legend doubles as a click-to-hide control, which
    // would desync from the display panel's checkboxes — those are the single source of visibility.
    legend: { show: false },
    hooks: interactive
      ? {
          // Drives the Reset zoom button. Fires on a completed drag and nothing else — uPlot suppresses
          // it for the selection it hides afterwards — so it is the one unambiguous "the user zoomed"
          // signal. setScale is not: it fires on every setData as well.
          setSelect: [
            (u: uPlot) => {
              if (u.select.width > 0 || u.select.height > 0) zoomed.value = true
            },
          ],
          // u.cursor.idx indexes the shared x array buildData produced — not a pixel-derived value, so
          // it always names a column something was actually plotted at, and the readout can index every
          // series at it. null once the pointer leaves the plot (uPlot clears idx on mouseleave).
          setCursor: [
            (u: uPlot) => {
              // Pinned, the hover is ignored outright rather than restored afterwards: this fires
              // on every pointer move, and recomputing the same frozen row sixty times a second to
              // arrive back where it started is work for nothing.
              if (props.pinnedX != null) return
              cursorReadout.value = readoutAt(u, u.cursor.idx ?? -1)
            },
          ],
        }
      : {},
    plugins: [minMaxBandPlugin(p), bandwidthPlugin(p), pinnedCursorPlugin(p)],
  }
}

/** The magnitude axis title, by view. */
function magnitudeAxisLabel(): string {
  switch (props.view) {
    case 'Gain':
      return 'Gain (dB)'
    case 'Crosstalk':
      return 'Crosstalk (dB)'
    default:
      return 'Level (dBV)'
  }
}

/** What the export's header line calls the plot — which y quantity it shows. */
function exportTitle(): string {
  return `${magnitudeAxisLabel()} vs frequency`
}

/**
 * The chart as a PNG of exactly the requested size, drawn with the light palette whatever the theme.
 *
 * A second uPlot is built offscreen from the same options and data as the visible one, so the
 * export is this chart and not an imitation of it — the min/max band, the bandwidth arrows and
 * every overlay come along, because the plugins are the same functions. The host
 * has to be *attached* (positioned off to the left) rather than detached: uPlot measures its
 * container, and a detached div measures zero. uPlot commits its first draw in a microtask, hence the
 * wait before compositing. The visible plot is never touched.
 *
 * Exposed for the Screenshot button (`captureZoom`, so the file shows the window on screen) and for
 * the report, which mounts this component headless and calls it with the display panel's window.
 */
async function exportPng({ width, height, captureZoom = false, primaryLabel }: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header
  const light = makePalette(false, true)

  const host = document.createElement('div')
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(host)

  const restore =
    captureZoom && plot.value ? { x: windowOf(plot.value.scales.x), y: windowOf(plot.value.scales.y) } : null

  let u: uPlot | null = null
  try {
    u = new uPlot(buildOptions(width, plotHeight, light, false), buildData(), host)
    // Same order as rebuild(): the data goes to the constructor, so a setScale afterwards is not
    // clobbered by a later setData. The x range callback reads `zoomed`, which is this component's
    // own flag, so a captured drag window passes through it the way it does on screen.
    if (restore?.x) u.setScale('x', restore.x)
    if (restore?.y) u.setScale('y', restore.y)
    await nextPaint()

    // The primary curves carry the report's name for this measurement when it shares the chart with
    // history overlays; the overlays already carry theirs.
    const legend: ExportLegendEntry[] = buildReadoutSeries(light).map((entry) => ({
      label: primaryLabel && !entry.key.startsWith('overlay-') ? `${primaryLabel} — ${entry.label}` : entry.label,
      color: entry.color,
      dash:
        entry.key === 'phase' ? [4, 3]
        : entry.key === 'coherence' ? [2, 2]
        : entry.key === 'delay' ? [10, 4]
        : entry.key.startsWith('overlay-') ? [6, 3]
        : undefined,
    }))

    return await composeChartPng({
      width,
      height,
      plotCanvas: u.ctx.canvas,
      headerHeight: header,
      title: exportTitle(),
      legend,
    })
  } finally {
    u?.destroy()
    host.remove()
  }
}

defineExpose({ exportPng })

/**
 * The pinned cursor's vertical line — a dashed amber rule through the column the readout (and, on
 * the stepped screen, the preview charts) are frozen on.
 *
 * <p>Amber rather than one of the curve colours for the reason the sweep chart's is: it is not a
 * reading, it is the user having frozen one. Drawn in `draw`, after the series, because a line under
 * a dense response disappears beneath it. Out of the current window it is skipped rather than
 * clamped to the edge — a line against the axis would claim a column that is not there — and the pin
 * itself survives, since it is a frequency and not a position.</p>
 */
function pinnedCursorPlugin(p: Palette): uPlot.Plugin {
  return {
    hooks: {
      draw: [
        (u) => {
          const pinned = props.pinnedX
          if (pinned == null) return

          const x = u.valToPos(pinned, 'x', true)
          if (x < u.bbox.left || x > u.bbox.left + u.bbox.width) return

          const ctx = u.ctx
          ctx.save()
          ctx.setLineDash([5, 4])
          ctx.strokeStyle = p.color(PINNED_CURSOR_COLOR)
          ctx.lineWidth = 1.5
          ctx.beginPath()
          ctx.moveTo(x, u.bbox.top)
          ctx.lineTo(x, u.bbox.top + u.bbox.height)
          ctx.stroke()
          ctx.restore()
        },
      ],
    },
  }
}

/**
 * Where the button went down, so a drag-zoom can be told from a click: a zoom gesture ends with a
 * `click` on the same element, and without this every zoom would also move the pin. The threshold is
 * in CSS pixels and deliberately small — a hand is never perfectly still on a press, and anything
 * larger would swallow a very short drag.
 */
const CLICK_SLOP_PX = 4
let pressOrigin: { x: number; y: number } | null = null

function notePressOrigin(event: MouseEvent) {
  pressOrigin = { x: event.clientX, y: event.clientY }
}

/**
 * A click pins the nearest plotted column, moves the pin there if one is up elsewhere, or releases
 * it when it is already on that column. Both sides of the comparison come from the same x array, so
 * the equality is exact and there is no tolerance to tune.
 */
function onPlotClick(event: MouseEvent) {
  const origin = pressOrigin
  pressOrigin = null

  const u = plot.value
  if (!u) return
  if (origin && (Math.abs(event.clientX - origin.x) > CLICK_SLOP_PX || Math.abs(event.clientY - origin.y) > CLICK_SLOP_PX)) {
    return
  }

  const idx = u.cursor.idx
  const xs = u.data[0]
  const freqHz = idx != null && idx >= 0 ? (xs?.[idx] as number | undefined) : undefined
  if (freqHz == null) return

  emit('cursorPin', props.pinnedX === freqHz ? null : freqHz)
}

function rebuild() {
  const el = container.value
  if (!el) return

  // A rebuild that is not itself an axis-window change hands the zoom back afterwards — a theme
  // flip or an overlay checkbox must not throw away the window the user is reading. A changed window
  // drops it instead, on *both* axes together: the panel is the user saying which window they want,
  // and restoring half a zoom would leave the plot zoomed with no button offering to undo it.
  const nextX = xWindowKey()
  const nextY = yWindowKey()
  const windowChanged = plotXWindowKey !== nextX || plotYWindowKey !== nextY
  const restore =
    plot.value && !windowChanged
      ? { x: windowOf(plot.value.scales.x), y: windowOf(plot.value.scales.y) }
      : null
  plotXWindowKey = nextX
  plotYWindowKey = nextY
  if (windowChanged) zoomed.value = false

  plot.value?.destroy()
  cursorReadout.value = null

  const width = el.clientWidth || 800
  const height = el.clientHeight || chartHeight.value
  const u = new uPlot(buildOptions(width, height, palette.value, true), buildData(), el)
  plot.value = u

  // uPlot's own double-click resets the x axis to the data extent, which on an auto:false chart is
  // whatever the zoom already shows. Ours is added afterwards on the same element, so it runs
  // second and actually gets back out. See resetZoom.
  u.over.addEventListener('dblclick', resetZoom)

  // Pinning. Both on uPlot's own `over`, which is the plot area and nothing else, so a click on an
  // axis or a label is not a click on a column.
  u.over.addEventListener('mousedown', notePressOrigin)
  u.over.addEventListener('click', onPlotClick)

  // Safe here, unlike the sweep chart's deferred restore: the data is passed to the constructor, so
  // no setData follows to clobber the scales.
  if (restore?.x) u.setScale('x', restore.x)
  if (restore?.y) u.setScale('y', restore.y)

  // The rebuild above cleared the readout; a pin outlives a rebuild, so put its row back.
  refreshPinnedReadout()
}

function refresh() {
  if (!plot.value) {
    rebuild()
    return
  }

  plot.value.setData(buildData())
  plot.value.redraw()

  // A continuous run replaces the curves under a pin that has not moved — the frozen row has to
  // show the new pass's numbers, not the pass it was made on.
  refreshPinnedReadout()
}

let observer: ResizeObserver | null = null

onMounted(() => {
  rebuild()
  observer = new ResizeObserver(() => {
    const el = container.value
    if (el && plot.value) plot.value.setSize({ width: el.clientWidth, height: el.clientHeight })
  })
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  plot.value?.destroy()
  plot.value = null
})

// Moving the pin is a repaint and a readout rebuild, never a plot rebuild: rebuilding would throw
// away the zoom on every click.
watch(
  () => props.pinnedX,
  () => {
    plot.value?.redraw(false)
    refreshPinnedReadout()
  },
)

watch(signature, () => rebuild())
watch([() => props.traces, () => props.overlays], refresh)

// The bandwidth markers are drawn straight from the props by their plugin, so toggling one needs a
// redraw and nothing more. The axis bounds are *not* here any more — they are part of the rebuild
// signature now, for the reason its own remarks give.
watch(() => [props.showOneDb, props.showThreeDb], () => refresh())

// A settings change, not a data change, but still only a re-range: the phase scale is not part of
// the drag-zoom (uPlot zooms the first plotted series, which is the dB one), so nothing about the
// series or their styling needs rebuilding here.
// Data, not styling: the delay is put back inside the phase series, so a refresh is all it needs.
watch(() => [props.restoreDelay, props.latencySamples, props.sampleRateHz], () => refresh())

watch(phaseRange, () => refresh())
watch(delayRange, () => refresh())
</script>

<template>
  <!-- The chart is a card like every settings and display panel beside it (the 2026-10-12
       mockups): one surface, one 12 px radius, with the cursor readout and the plot inside it
       rather than the readout floating on the page ground above a bare plot. -->
  <div ref="root" class="relative flex flex-col gap-2 panel-surface rounded-xl border border-neutral-200 p-3 dark:border-neutral-800">
    <ExpandToggle class="absolute right-3 top-3" :target="root" />
    <!-- Cursor readout on the left, zoom state on the right. Both are about the plot below rather
         than about the measurement, so they sit with it and not in the display panel. -->
    <div class="min-h-6 pr-9 flex items-start justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
      <span v-if="cursorReadout" class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 font-mono">
        <span class="text-neutral-800 dark:text-neutral-100">{{ formatHz(cursorReadout.freqHz) }}</span>
        <span v-for="entry in cursorReadout.entries" :key="entry.key" class="flex items-center gap-1">
          <span class="inline-block h-1.5 w-1.5 shrink-0 rounded-full" :style="{ backgroundColor: entry.color }" />
          <span class="max-w-32 truncate" :title="entry.label">{{ entry.label }}</span>
          <span class="text-neutral-800 dark:text-neutral-100">{{ entry.text }}</span>
        </span>
      </span>
      <span v-else>Hover the plot for values at a frequency</span>

      <!-- Only shown while something is pinned, and it is also the way out: the pin is released by
           clicking its own line, which is discoverable once you know it and not before. -->
      <button
        v-if="props.pinnedX != null"
        type="button"
        class="inline-flex shrink-0 items-center gap-1 rounded border px-1.5 py-0.5 btn-action"
        title="The readings above are held at this frequency, and hovering the chart no longer moves them. Click here, or the pinned line itself, to release."
        @click="clearPin"
      >
        <ToolbarIcon name="pin" />
        Pinned
      </button>

      <!-- The only visible sign that the axes are no longer the window the display panel names —
           double-clicking does the same thing, but nothing on screen would say so. The hint takes
           its place while unzoomed, so the header never carries two messages about one gesture. -->
      <button
        v-if="zoomed"
        type="button"
        class="inline-flex shrink-0 items-center gap-1 rounded border px-2 py-0.5 btn-action"
        title="Back to the frequency and dB window set in the display panel."
        @click="resetZoom"
      >
        <ToolbarIcon name="reset-zoom" />
        Reset zoom
      </button>
      <span v-else class="shrink-0">Click to pin · drag to zoom</span>
    </div>

    <div ref="plotBox" class="relative w-full" :style="{ height: `${chartHeight}px` }">
      <div ref="container" class="h-full w-full" />
      <!-- Off-scale cue: real data sits above/below the currently visible dB/dBV window. -->
      <div
        v-if="outOfRange.above"
        class="pointer-events-none absolute right-2 top-2 text-amber-500 dark:text-amber-400"
        title="Data above the visible range"
      >
        ▲
      </div>
      <div
        v-if="outOfRange.below"
        class="pointer-events-none absolute bottom-2 right-2 text-amber-500 dark:text-amber-400"
        title="Data below the visible range"
      >
        ▼
      </div>
    </div>
    <p v-if="traces.length === 0 && overlays.length === 0" class="text-xs text-neutral-500 dark:text-neutral-400">
      No {{ props.view === 'Crosstalk' ? 'crosstalk' : 'frequency response' }} measured yet. Press {{ props.runLabel ?? 'Run' }}.
    </p>
  </div>
</template>
