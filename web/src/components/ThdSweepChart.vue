<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import {
  CHART_EXPORT_HEADER_PX,
  composeChartPng,
  nextPaint,
  exportAxisLabel,
  type ChartExportOptions,
  type ExportLegendEntry,
} from '../charts/exportChart'
import ExpandToggle from './ExpandToggle.vue'
import ToolbarIcon from './ToolbarIcon.vue'
import { useAppTheme } from '../composables/useAppTheme'
import { useLoadOhms } from '../composables/useLoadOhms'
import { SWEEP_POINTS_KEY, useSweep } from '../composables/useSweep'
import { percentToDb, sweepTraceLabel, sweepTraceValue, type SweepTraceKey } from '../sweep/points'
import {
  decadeOrFallbackSplits,
  formatSweepX,
  logMinorGridAxis,
  sweepXAxis,
  sweepXLabel,
  sweepXUnit,
  sweepXValue,
  type SweepXAxis,
} from '../sweep/xAxis'
import { interpolateOnGrid } from '../sweep/align'
import type { OverlayEntry } from '../composables/useMeasurements'
import { dashAttr, sweepLineStyle, sweepOverlays } from '../measurements/sweepOverlayStyles'
import { useZoomBounds } from '../composables/useZoomBounds'
import type { ThdMethodName, ThdSweepPoint } from '../api/client'

/**
 * The sweep result chart, for both sweep axes.
 *
 * Structurally a trimmed SpectrumChart: one uPlot instance created once and fed through setData(),
 * rebuilt only on a theme/series-shape change, and the same shared log x-axis from freqAxis.ts so a
 * sweep and a spectrum tick their frequency axis identically. What it drops is everything tied to a
 * streaming min/max spectrum — no band plugin, no peak hold, no viewport reporting — because a sweep
 * point is a single value, not a decimated column.
 *
 * What varies along x is a prop (see sweep/xAxis.ts): frequency for a THD-vs-frequency sweep, and
 * one of output voltage / output power / input voltage for a THD-vs-amplitude one. Everything below
 * is written against "the point's x value" rather than against its frequency, because on the two
 * output-referred axes the left and right channels land on *different* x values — that is a real
 * property of the measurement, not a grid mismatch, and the shared-x union already built for
 * overlays is what absorbs it.
 *
 * The y axis has two modes. Percent is logarithmic (distr: 3): distortion spans four or five
 * decades across a sweep and a linear axis renders all of it as a flat line on zero. dB is the same
 * data as dBc (20·log10(pct/100)) on a linear axis, which is the same shape — the toggle is about
 * which numbers people want to read off, matching the previous app's two graph types.
 */

const props = defineProps<{
  /** Per-trace visibility. Every trace is individually switchable from the display panel. */
  showMagnitude: boolean
  showThd: boolean
  showD2: boolean
  showD3: boolean
  showD4: boolean
  showD5: boolean
  showD6Plus: boolean
  showNoiseFloor: boolean
  showLeft: boolean
  showRight: boolean
  unit: 'percent' | 'db'
  thickLines: boolean
  showDataPoints: boolean
  /**
   * Axis bounds, set from the display panel rather than fitted to the data.
   *
   * The two y axes carry their own pair. They are not two views of one window: the percent axis
   * bottoms out at a distortion figure and its top is 100 % at most, while the dB axis has to
   * reach *above* 0 dB for the Magnitude trace, which no percentage can express. Deriving one
   * from the other through percentToDb made the dB top unreachable.
   */
  yMaxPercent: number
  yMinPercent: number
  yTopDb: number
  yBottomDb: number
  /** X bounds, in whatever unit `xAxis` is in — hertz, volts or watts. */
  xFrom: number
  xTo: number
  /** Which quantity x plots. See sweep/xAxis.ts. */
  xAxis: SweepXAxis
  /**
   * Stored sweeps plotted alongside the live one, checked on in the History list — the same
   * OverlayEntry list SpectrumChart draws, filtered to the ones that actually carry sweep points.
   */
  overlays: OverlayEntry[]
  /**
   * The point currently "selected" per channel — the last one measured while a sweep runs, or the
   * one nearest the chart cursor once it's done (App.vue's selectedSweepPointFor, the same state
   * that drives the small FFT/scope preview charts). Drives the metrics pane below the chart, which
   * replaces the legend's old per-series hover values with the full reading at one frequency: F,
   * THD, Magnitude, D2..D6+, power and noise floor, one row per channel that has data.
   */
  selectedLeft: ThdSweepPoint | null
  selectedRight: ThdSweepPoint | null
  /**
   * The same selection, per history result checked on in the checklist — App.vue's
   * selectedSweepPointForOverlay, in the order sweepOverlays() puts them so a group here lines up
   * with the run of the same name in the legend above. Each one gets its own block in the metrics
   * table, so a stored sweep can be read off at the cursor frequency next to the live one instead
   * of only being compared as a line on the chart.
   */
  overlaySelections: { id: number; name: string; left: ThdSweepPoint | null; right: ThdSweepPoint | null }[]
}>()

/**
 * The x value under the pointer, or null once it leaves the plot area — App.vue uses this to drive
 * the small FFT/scope preview charts once a sweep is done ("nearest point to the cursor"). uPlot's
 * own cursor.idx is an index into the shared x array built in update(), so it names a value
 * something on the chart was measured at, not an interpolated pixel position — though with overlays
 * on that grid it may be one only an *overlay* measured, which is why the consumer (App.vue's
 * selectedSweepPointFor) resolves it through nearestSweepPoint.
 */
const emit = defineEmits<{ cursorX: [x: number | null] }>()

// The screen's run from the shared store, or a stored result's when a report renders this chart
// headless — see SWEEP_POINTS_KEY.
const { points, thdMethod } = inject(SWEEP_POINTS_KEY, null) ?? useSweep()
const { isDark } = useAppTheme()
const { loadOhms } = useLoadOhms()

const rootRef = ref<HTMLElement | null>(null)
const containerRef = ref<HTMLDivElement | null>(null)

// Fitted to the view — see charts/useResultChartHeight.ts. uPlot sizes the container itself, so a
// new height is applied through setSize, the same call a width change makes in handleResize.
const chartHeight = useResultChartHeight(rootRef, containerRef)

/**
 * One entry per drawn trace. Order is fixed and shared by buildOptions and update() — the series
 * array and the data array have to agree slot for slot, and deriving both from this list is what
 * keeps them from drifting apart when a trace is added.
 *
 * Hue identifies the *quantity*, matching the previous app's palette (THD black, D2 blue, D3
 * orange, D4 green, D5 red, D6+ purple) so a trace is recognisable at a glance from either app.
 * Channel and run are carried by lightness, width and dash instead — see sweepLineStyle in
 * measurements/sweepOverlayStyles.ts, which owns all three. Each colour is a light/dark pair
 * because near-black THD is invisible on a dark background and the saturated harmonic hues are
 * hard to read against one.
 */
const TRACES = [
  // Magnitude is the measured fundamental relative to the generator setpoint, i.e. the DUT's
  // frequency response riding along with the sweep. It is already a dB quantity, so it has no
  // meaning on the percent axis and `dbOnly` keeps it off it -- the previous app greys its
  // checkbox out on the D% graph for the same reason.
  { key: 'magnitude', label: 'Magn', width: 1, color: ['#be185d', '#f472b6'], show: 'showMagnitude', dbOnly: true },
  { key: 'thd', label: 'THD', width: 1, color: ['#111827', '#e5e7eb'], show: 'showThd' },
  { key: 'd2', label: 'D2', width: 0.8, color: ['#1d6fd0', '#60a5fa'], show: 'showD2' },
  { key: 'd3', label: 'D3', width: 0.8, color: ['#e07b16', '#fbbf24'], show: 'showD3' },
  { key: 'd4', label: 'D4', width: 0.8, color: ['#1a7a3c', '#4ade80'], show: 'showD4' },
  { key: 'd5', label: 'D5', width: 0.8, color: ['#c62828', '#f87171'], show: 'showD5' },
  { key: 'd6plus', label: 'D6+', width: 0.8, color: ['#6b3fa0', '#c084fc'], show: 'showD6Plus' },
  // Not in the previous app's legend, so it gets a hue that stays clear of the six above. Teal
  // rather than the neutral grey it used to be: the right channel is drawn as its hue blended
  // toward the background (see sweepLineStyle), and blending near-black THD lands on a mid grey
  // that a neutral Noise trace is very hard to tell from. Grey now belongs to the THD family alone.
  { key: 'noise', label: 'Noise', width: 0.8, color: ['#0d7d84', '#2dd4bf'], show: 'showNoiseFloor' },
] as const

type TraceKey = (typeof TRACES)[number]['key'] & SweepTraceKey
type TraceToggle = (typeof TRACES)[number]['show']

/**
 * The method a stored run's figures are read under — see sweepTraceValue. Conventional for a row
 * saved before the method existed, which is what useMeasurements.addOverlay resolves it to.
 */
function methodOf(overlay: OverlayEntry): ThdMethodName {
  return overlay.sweepMethod ?? 'Conventional'
}

const CHANNEL_LABELS = ['L', 'R']

// `grid` is the weight every axis has always drawn at. `gridMajor`/`gridMinor` are the decade
// split used by the volt and watt x axes: the decades are what the labels name, so they carry the
// stronger line, and the 2..9 positions between them sit back far enough to read as structure
// rather than as data.
const THEME = {
  dark: { axis: '#a3a3a3', grid: '#404040', gridMajor: '#585858', gridMinor: '#333333' },
  light: { axis: '#525252', grid: '#e5e5e5', gridMajor: '#c8c8c8', gridMinor: '#ededed' },
} as const

type Theme = (typeof THEME)[keyof typeof THEME]

// Threaded through as an argument (which theme, via `dark`) rather than read at draw time — the
// export builds a second, light plot while the screen may be dark. See charts/palette.ts.
function themeFor(dark: boolean): Theme {
  return dark ? THEME.dark : THEME.light
}

/** Minimum pixels between two labelled distortion ticks, for the sub-decade fallback. Tighter than the x axis': a number is far shorter than it is tall. */
const Y_AXIS_SPACE = 30

/** One label-less axis whose only job is the thin 2..9 gridlines of a log scale. */
function minorGridAxis(scale: 'x' | 'y', side: 1 | 2 | 3, theme: Theme): uPlot.Axis {
  return {
    ...logMinorGridAxis(),
    scale,
    side,
    size: 0,
    gap: 0,
    stroke: theme.axis,
    grid: { stroke: theme.gridMinor, width: 1 },
    ticks: { show: false },
  }
}

/**
 * How one line looks, for a run (0 = live, 1-based = history slot), a channel and a trace. The one
 * place stroke/width/dash is decided, shared with the legend below the chart so the two can never
 * disagree about what a line looks like — see sweepLineStyle's own note on why that matters.
 */
function styleOf(runIdx: number, channel: number, trace: (typeof TRACES)[number], dark: boolean) {
  return sweepLineStyle({
    runIdx,
    channel,
    color: trace.color,
    traceWidth: trace.width,
    thickLines: props.thickLines,
    isDark: dark,
  })
}

const activeChannels = computed(() => {
  const channels: number[] = []
  if (props.showLeft) channels.push(0)
  if (props.showRight) channels.push(1)
  return channels
})

const activeTraces = computed(() =>
  TRACES.filter((t) => props[t.show as TraceToggle] && (!('dbOnly' in t) || props.unit === 'db')),
)

/**
 * The overlays actually drawable here: visible, and carrying stored sweep points. An FFT result
 * checked on while the sweep screen is open has no points and is skipped rather than drawn empty.
 * Shared with MeasurementChecklist's swatches so both derive a run's dash from the same slot order.
 */
const activeOverlays = computed(() => sweepOverlays(props.overlays))

/** This point's position on the axis currently selected — the one number everything below sorts, indexes and plots by. */
function xOf(point: ThdSweepPoint): number {
  return sweepXValue(point, props.xAxis)
}

/**
 * Distinct x values, ascending — the shared array every series is aligned to.
 *
 * The union of the live sweep's grid and every visible overlay's, not just the live one: a stored
 * sweep run at different start/end/steps lands on different values, and uPlot aligns all series to
 * one x array, so anything missing from it simply would not be drawn. On the output-referred
 * amplitude axes the two *channels* of one sweep contribute different values for the same reason —
 * each channel's x is its own measured output level.
 *
 * A trace does *not* read back as a gap at the values it has no point at — see sweep/align.ts. Two
 * grids that interleave rather than coincide (adaptive vs fixed, 4 vs 5 points per decade) would
 * otherwise leave every series as value/null/value/null, which uPlot draws as disconnected stubs.
 * Each trace is interpolated along its own measured points instead, within its own range only.
 *
 * Non-positive values are dropped rather than clamped: the axis is logarithmic, and a point whose
 * fundamental was never found reports an output voltage of 0, which has no position on it.
 */
const xValues = computed(() => {
  const all = new Set<number>()
  const add = (point: ThdSweepPoint) => {
    const x = xOf(point)
    if (x > 0 && Number.isFinite(x)) all.add(x)
  }
  points.value.forEach(add)
  for (const overlay of activeOverlays.value) (overlay.sweepPoints ?? []).forEach(add)
  return [...all].sort((a, b) => a - b)
})

const hasData = computed(() => xValues.value.length > 0)

/** x value -> its slot in the shared array. The grid is built from these very numbers, so this is exact. */
const gridIndexByX = computed(() => {
  const index = new Map<number, number>()
  xValues.value.forEach((x, i) => index.set(x, i))
  return index
})

/**
 * Which x slots each series was actually *measured* at, by series index — what the data-point
 * markers are drawn at. Rebuilt by update() alongside the data itself.
 *
 * Without this, turning "show data points" on would put a marker on every interpolated slot too,
 * so a 4-points-per-decade sweep would appear to have as many points as whatever else is on the
 * chart. The interpolated line is a drawing convenience; the markers are the measurement, and they
 * have to stay honest about where it happened.
 */
let measuredIdxBySeries: (number[] | null)[] = []

/**
 * One point's value for a trace, under the method its run used. Delegated to sweep/points.ts so
 * the display panel's Autofit reads the very same figure — a frequency-normalized run shows its
 * normalized figures, and never falls back to the conventional ones where those are absent.
 */
function valueOf(point: ThdSweepPoint, key: TraceKey, method: ThdMethodName): number {
  return sweepTraceValue(point, key, method)
}

/**
 * A percentage as the y axis currently wants it. Non-positive values become null (a gap) rather
 * than 0: on the log percent axis 0 has no position at all, and on the dB axis it would plunge the
 * whole scale to the -infinity floor and flatten every real trace against the top.
 */
function toY(value: number, key: TraceKey): number | null {
  if (!Number.isFinite(value)) return null

  // Magnitude is a gain in dB and goes straight onto the axis -- it is legitimately negative or
  // zero, so it must not go through the percent guard below, and there is nothing to convert.
  if (key === 'magnitude') return value

  if (value <= 0) return null
  return props.unit === 'db' ? percentToDb(value) : value
}

/** The y window in axis units, always [bottom, top]. */
function yRange(): [number, number] {
  return props.unit === 'db' ? [props.yBottomDb, props.yTopDb] : [props.yMinPercent, props.yMaxPercent]
}

/**
 * True while either axis shows something other than the window the display panel's dropdowns ask
 * for — i.e. the user has dragged a zoom. Drives the Reset zoom button, which is the only visible
 * cue that the axes are no longer the ones the dropdowns name.
 */
const { zoomed, noteScale, resetZoom } = useZoomBounds(
  () => plot,
  () => {
    const [yMin, yMax] = yRange()
    return { xMin: props.xFrom, xMax: props.xTo, yMin, yMax }
  },
)

/**
 * Whether any currently-checked trace/channel, live or on a visible overlay, has a plotted value
 * within the current x window that sits above or below the y window — the cue that there is real
 * data off the top or bottom of the chart.
 */
const outOfRange = computed(() => {
  let above = false
  let below = false
  const [yMin, yMax] = yRange()
  const channels = activeChannels.value
  const traces = activeTraces.value
  if (channels.length === 0 || traces.length === 0) return { above, below }

  function consider(pts: ThdSweepPoint[], method: ThdMethodName) {
    for (const p of pts) {
      if (!channels.includes(p.channel)) continue
      const x = xOf(p)
      if (!(x > 0) || x < props.xFrom || x > props.xTo) continue
      for (const trace of traces) {
        const y = toY(valueOf(p, trace.key, method), trace.key)
        if (y == null) continue
        if (y > yMax) above = true
        if (y < yMin) below = true
      }
      if (above && below) return
    }
  }
  consider(points.value, thdMethod.value)
  for (const overlay of activeOverlays.value) consider(overlay.sweepPoints ?? [], methodOf(overlay))
  return { above, below }
})

/**
 * @param dark Which theme to build with — see themeFor.
 * @param interactive False for the export's offscreen plot: no cursor, and none of the hooks that
 *   feed the zoom flag and the preview charts.
 */
function buildOptions(width: number, height: number, dark: boolean, interactive: boolean): uPlot.Options {
  const theme = themeFor(dark)
  // Decade emphasis on x is for the level axes only. The frequency axis is shared with the FFT
  // screen (freqAxis), where the 1/2/5 ladder and its kHz labels are long-standing.
  const decadeGridX = props.xAxis !== 'frequency'

  // On y it follows the unit, not the screen: percent is logarithmic and has decades to emphasise,
  // dB is linear and does not -- 10 dB apart is 10 dB apart wherever you are on that axis.
  const decadeGridY = props.unit === 'percent'
  // filter names the indices to mark; uPlot draws points when `show || idxs` is truthy, so it must
  // return null (not []) when markers are off, or the "off" state would draw them again via idxs.
  const points = {
    show: props.showDataPoints,
    size: props.thickLines ? 7 : 5,
    filter: (_u: uPlot, seriesIdx: number) =>
      props.showDataPoints ? (measuredIdxBySeries[seriesIdx] ?? null) : null,
  }

  const series: uPlot.Series[] = [{}]
  for (const ch of activeChannels.value) {
    for (const trace of activeTraces.value) {
      series.push({
        label: `${CHANNEL_LABELS[ch]} ${sweepTraceLabel(trace.key, thdMethod.value)}`,
        ...styleOf(0, ch, trace, dark),
        points,
        scale: 'y',
      })
    }
  }

  // Overlays keep this chart's own convention rather than SpectrumChart's: colour still means
  // "which quantity", exactly as it does for the live sweep, and the dash pattern identifies the
  // stored run. Giving each run a hue of its own (the old behaviour) painted a whole measurement —
  // THD, D2, D3, D4, D5, D6+ — in one violet, so the comparison a history overlay exists for, the
  // *same* quantity then versus now, was the one thing it could not show. Same colour, different
  // pattern puts the two D3 traces side by side and lets the eye do the subtraction.
  //
  // The loop nesting here is load-bearing: update()/pushSeriesData walks the same channel-then-trace
  // order to fill each slot, so the series array and the data array agree position for position.
  activeOverlays.value.forEach((overlay, overlayIdx) => {
    for (const ch of activeChannels.value) {
      for (const trace of activeTraces.value) {
        series.push({
          label: `${overlay.detail.name} ${CHANNEL_LABELS[ch]} ${sweepTraceLabel(trace.key, methodOf(overlay))}`,
          ...styleOf(overlayIdx + 1, ch, trace, dark),
          points,
          scale: 'y',
        })
      }
    }
  })

  return {
    width,
    height,
    scales: {
      // Both axes *start* pinned to the dropdowns rather than fitted to the data. auto:false is
      // what makes that stick -- otherwise every setData() re-fits, and a sweep calls setData once
      // per point, so the axes would crawl as it ran.
      //
      // The range callbacks pass their arguments through rather than always handing back the
      // dropdown bounds, and that is what lets a drag-zoom survive. uPlot re-derives the x scale
      // through this very callback on every setScale -- including the exact window a drag just
      // requested -- so the original version, which ignored the arguments, fed the zoom request
      // back in and got the full span out again, undoing every zoom the instant it was made. The
      // arguments are null only when nothing is pending (notably on the empty construction data),
      // which is where the dropdown bounds seed the axis — and that empty construction is
      // load-bearing, not incidental. This chart builds the plot with empty arrays and only then
      // calls setData, so x is still dataless when the callback first runs and does receive nulls.
      // A chart that hands real data straight to the constructor gets the data's own span here
      // instead and its x bounds are silently discarded, which is exactly what happened to the
      // frequency-response and impedance charts. See the uPlot range-callback rule in CLAUDE.md. y in practice never sees a non-null pair
      // -- uPlot assigns a pending y window directly and skips the callback -- but it is written
      // the same way so neither axis depends on which branch uPlot takes.
      x: {
        time: false,
        distr: 3,
        log: 10,
        auto: false,
        range: (_u, min, max) => [min ?? props.xFrom, max ?? props.xTo],
      },
      // Percent is log (distr 3) across up to seven decades; dB is the same numbers on a linear
      // axis, so the bounds go through percentToDb and swap ends -- a smaller percentage is a more
      // negative dB, but it is still the bottom of the window.
      y:
        props.unit === 'db'
          ? { auto: false, range: (_u, min, max) => [min ?? yRange()[0], max ?? yRange()[1]] }
          : {
              distr: 3,
              log: 10,
              auto: false,
              range: (_u, min, max) => [min ?? yRange()[0], max ?? yRange()[1]],
            },
    },
    axes: [
      {
        ...sweepXAxis(props.xAxis),
        ...exportAxisLabel(interactive, `${sweepXLabel(props.xAxis)} (${sweepXUnit(props.xAxis)})`),
        stroke: theme.axis,
        grid: { stroke: decadeGridX ? theme.gridMajor : theme.grid, width: decadeGridX ? 1.5 : 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
      {
        scale: 'y',
        size: 62,
        ...exportAxisLabel(interactive, props.unit === 'percent' ? 'Distortion (%)' : 'Distortion (dB)'),
        ...(decadeGridY
          ? {
              splits: (u, axisIdx, scaleMin, scaleMax) =>
                decadeOrFallbackSplits(u, axisIdx, scaleMin, scaleMax, Y_AXIS_SPACE),
              // Same reason as the x axis: uPlot's own log filter keeps only round mantissas, which
              // on a sub-decade zoom is none of them.
              filter: (_u, splits) => splits,
            }
          : {}),
        values: (_u, vals) => vals.map((v) => (v == null ? null : formatYTick(v))),
        stroke: theme.axis,
        grid: { stroke: decadeGridY ? theme.gridMajor : theme.grid, width: decadeGridY ? 1.5 : 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
      // The 2..9 lines of each emphasised axis, as their own label-less axis on the same scale --
      // see logMinorGridAxis on why this cannot be folded into the axis above. Listed after both, so
      // the lighter strokes are drawn over nothing; the two sets of positions never coincide.
      ...(decadeGridX ? [minorGridAxis('x', 2, theme)] : []),
      ...(decadeGridY ? [minorGridAxis('y', 3, theme)] : []),
    ],
    series,
    cursor: interactive
      ? {
          points: { show: false },
          // Both axes draggable, with `uni` making a drag unidirectional unless it is a real box: a
          // drag that stays under 12 px in one direction zooms the other axis only, across the full
          // span of this one. Frequency-only is the common case on a sweep (narrowing to an octave
          // around a rise in THD), but a distortion window matters just as much when four harmonic
          // traces sit within a few dB of each other, and demanding a pixel-accurate horizontal drag
          // to get one without the other would make both awkward.
          drag: { x: true, y: true, uni: 12 },
        }
      : { show: false },
    // Replaced by the SVG legend in this component's own template. uPlot's marker is a CSS border
    // and legend.markers.dash only understands "solid"/"dashed", so it could not show *which* of
    // the three history dash patterns a row used -- the one thing this chart's legend most needs to
    // say. Its rows also toggle their series on click, which would silently desync the display
    // panel's checkboxes; the replacement is inert markup with no such trap.
    legend: { show: false },
    hooks: interactive
      ? {
          // Drives the Reset zoom button. Fires on setData too (the x scale is auto:false, so setData
          // re-applies the same min/max every point of a running sweep), hence the change-gate inside
          // noteScale -- writing an unchanged `false` 50 times per sweep would be harmless but
          // pointless.
          setScale: [(u: uPlot) => noteScale(u)],
          // u.cursor.idx indexes the shared x array from update() -- not a pixel-derived value, so it
          // always names a value something was actually plotted at. null once the pointer leaves the
          // plot area (uPlot clears idx on mouseleave), which is the "nothing hovered" state the small
          // preview charts fall back from.
          setCursor: [
            (u: uPlot) => {
              const idx = u.cursor.idx
              emit('cursorX', idx != null && idx >= 0 ? (xValues.value[idx] ?? null) : null)
            },
          ],
        }
      : {},
  }
}

/**
 * 0.001 / 0.01 / 0.1 / 1 / 10 for percent (no trailing-zero noise across five decades), plain
 * integers for dB.
 *
 * The extra digit over the decade's own exponent, then trimmed, is what makes this right for a tick
 * that is *not* a decade — 0.25 would otherwise print as "0.3". Those only appear on a zoom inside
 * one decade, where the axis falls back to 1/2/5 ticks (see decadeOrFallbackSplits); at every other
 * zoom the trim takes the padding straight back off and the label is the decade, unchanged.
 */
function formatYTick(v: number): string {
  if (props.unit === 'db') return `${v}`
  if (v >= 1) return `${v}`

  const text = v.toFixed(Math.min(8, Math.max(0, Math.ceil(-Math.log10(v)) + 1)))
  return text.includes('.') ? text.replace(/\.?0+$/, '') : text
}

/**
 * A distortion/noise percentage as the metrics pane's active unit wants it — the same
 * percentToDb conversion the chart itself plots with, so a row's numbers always match wherever
 * the corresponding trace actually sits on screen. Unlike toY (which the chart uses), this never
 * returns null for a non-positive value: the pane reports a real reading, not a gap in a line.
 */
function formatDistortion(percent: number): string {
  if (!Number.isFinite(percent)) return '—'
  if (props.unit === 'db') return `${percentToDb(percent).toFixed(2)} dB`
  if (!(percent > 0)) return '0%'
  const decimals = percent >= 1 ? 2 : Math.min(6, Math.max(0, Math.ceil(-Math.log10(percent)) + 1))
  return `${percent.toFixed(decimals)}%`
}

/** Magnitude is a gain in dB on both unit modes — see TRACES' own remarks on why it has no percent form. */
function formatMagnitude(gainDb: number): string {
  return Number.isFinite(gainDb) ? `${gainDb.toFixed(2)} dB` : '—'
}

/** "1 mW (8 Ω)" — the load a sweep's Power figure was computed against, from the same field SweepConfigPanel's own load input edits. */
function formatPower(watt: number): string {
  if (!Number.isFinite(watt)) return '—'
  const mw = watt * 1000
  const value = mw >= 100 ? mw.toFixed(0) : mw >= 10 ? mw.toFixed(1) : mw.toFixed(2)
  return `${value} mW (${loadOhms.value} Ω)`
}

/**
 * The metrics table's columns, in order, for one run's method. One list rather than a label
 * carried alongside every value: the header is written once per block and the rows underneath it
 * are bare values, so the two only line up if they come from the same order — see metricsFor,
 * which returns exactly this many strings. Per run rather than one global list because a
 * frequency-normalized run reads nTHD/nD2… while a conventional one overlaid on it reads THD/D2…,
 * and the header is the only place that difference is stated.
 */
function metricColumns(method: ThdMethodName): string[] {
  const t = (key: SweepTraceKey) => sweepTraceLabel(key, method)
  return [
    sweepXLabel(props.xAxis), t('thd'), 'Magnitude', t('d2'), t('d3'), t('d4'), t('d5'), t('d6plus'), 'Power', 'Noise floor',
  ]
}

/** One channel's row of values for the metrics table, in metricColumns order, under its run's method. */
function metricsFor(point: ThdSweepPoint, method: ThdMethodName): string[] {
  const v = (key: SweepTraceKey) => valueOf(point, key, method)
  return [
    formatSweepX(xOf(point), props.xAxis),
    formatDistortion(v('thd')),
    formatMagnitude(point.gainDb),
    formatDistortion(v('d2')),
    formatDistortion(v('d3')),
    formatDistortion(v('d4')),
    formatDistortion(v('d5')),
    formatDistortion(v('d6plus')),
    formatPower(point.powerWatt),
    formatDistortion(v('noise')),
  ]
}

/**
 * The metrics table, one block per run: the live sweep first, then one per checked history result.
 * A block is a header row plus a row per channel that actually has a point at the selected
 * frequency — a mono sweep gets one row, not an empty second one. Colours match the small preview
 * charts' own left-blue/right-red convention.
 */
const metricGroups = computed(() => {
  const groups: {
    key: string
    name: string | null
    columns: string[]
    rows: { key: string; label: string; colorClass: string; values: string[] }[]
  }[] = []

  function add(key: string, name: string | null, method: ThdMethodName, left: ThdSweepPoint | null, right: ThdSweepPoint | null) {
    const rows = [
      { key: `${key}-L`, label: 'Left channel', colorClass: 'text-blue-600 dark:text-blue-400', point: left },
      { key: `${key}-R`, label: 'Right channel', colorClass: 'text-red-600 dark:text-red-400', point: right },
    ]
      .filter((r) => r.point !== null)
      .map((r) => ({ key: r.key, label: r.label, colorClass: r.colorClass, values: metricsFor(r.point!, method) }))
    if (rows.length > 0) groups.push({ key, name, columns: metricColumns(method), rows })
  }

  add('live', null, thdMethod.value, props.selectedLeft, props.selectedRight)
  for (const sel of props.overlaySelections) {
    const overlay = activeOverlays.value.find((o) => o.id === sel.id)
    add(`o${sel.id}`, sel.name, overlay ? methodOf(overlay) : 'Conventional', sel.left, sel.right)
  }
  return groups
})

/**
 * The legend, one group per run and one swatch per drawn line, built from the same styleOf() the
 * series are — so a swatch is the line, not an approximation of it. Grouping by run is what makes
 * the dash readable as an identity: every entry under "before mod" shares one pattern, and the
 * heading names it.
 */
function legendGroupsFor(dark: boolean) {
  const groups = [{ key: 'live', name: 'Live sweep', runIdx: 0, method: thdMethod.value }]
  activeOverlays.value.forEach((overlay, i) =>
    groups.push({ key: `o${overlay.id}`, name: overlay.detail.name, runIdx: i + 1, method: methodOf(overlay) }),
  )

  return groups.map((group) => ({
    ...group,
    entries: activeChannels.value.flatMap((ch) =>
      activeTraces.value.map((trace) => {
        const style = styleOf(group.runIdx, ch, trace, dark)
        // nTHD, nD2 … on a frequency-normalized run: the figures are a different measurement
        // from a conventional run's, and the legend is where the two are told apart.
        const label = sweepTraceLabel(trace.key, group.method)
        return {
          key: `${group.key}-${ch}-${trace.key}`,
          // Only worth naming the channel when both are drawn — a mono sweep's legend would
          // otherwise read "L THD, L D2, L D3" with nothing to contrast against.
          label: activeChannels.value.length > 1 ? `${CHANNEL_LABELS[ch]} ${label}` : label,
          stroke: style.stroke,
          width: style.width,
          dash: dashAttr(style.dash),
          dashArray: style.dash,
        }
      }),
    ),
  }))
}

const legendGroups = computed(() => legendGroupsFor(isDark.value))

let plot: uPlot | null = null

// Everything baked into series/axes at construction time — uPlot has no live setter for any of it,
// so a change here has to destroy and rebuild rather than go through setData().
let plotSignature = ''
function signature(): string {
  return [
    activeChannels.value.join(','),
    activeTraces.value.map((t) => t.key).join(','),
    // Series count and colours are baked in at construction, so checking an overlay on or off has
    // to rebuild the plot the same way adding a trace does.
    activeOverlays.value.map((o) => o.id).join(','),
    props.unit,
    props.thickLines,
    props.showDataPoints,
    isDark.value,
    // The range functions above close over props and would return the new bounds on their own, but
    // uPlot only calls them on setData/setScale -- so a dropdown change has to force a rebuild to
    // be visible. A sweep is ~50 points; rebuilding costs nothing next to tracking scale updates.
    props.yMaxPercent,
    props.yMinPercent,
    props.xFrom,
    props.xTo,
    props.yTopDb,
    props.yBottomDb,
    // The whole x grid is rebuilt from a different quantity, so switching axis is a data change as
    // much as an axis one -- and the axis config itself is baked in at construction.
    props.xAxis,
    // Series labels name the method (THD vs nTHD) and are baked in too.
    thdMethod.value,
  ].join('|')
}

/**
 * The two halves of signature() that describe the *axis windows* specifically. A rebuild triggered
 * by anything else — a theme flip, a trace or overlay checkbox, thick lines — has to give the user
 * back the zoom they were looking at, exactly as SpectrumChart does across an overlay change. A
 * rebuild triggered by one of these must not: the dropdown is the user saying which window they
 * want, and reapplying the old zoom over it would make the dropdown look broken.
 *
 * The unit belongs to the y key rather than the x one: percent and dB are not two views of one
 * window (see the yMaxPercent/yTopDb prop docs), so a zoom made on one axis means nothing on the
 * other — but the frequency window is the same either way and is worth keeping across the toggle.
 */
function xWindowKey(): string {
  return `${props.xAxis}|${props.xFrom}|${props.xTo}`
}
function yWindowKey(): string {
  return `${props.unit}|${props.yMinPercent}|${props.yMaxPercent}|${props.yBottomDb}|${props.yTopDb}`
}
let plotXWindowKey = ''
let plotYWindowKey = ''

// Set by ensurePlot when a rebuild should restore the zoom that was on screen before it, consumed
// by update() right after its own setData(). It cannot be applied inside ensurePlot: uPlot defers a
// setScale, so a call made before update()'s setData just gets clobbered by the scale reapplication
// setData does synchronously — the same ordering SpectrumChart's own zoom restore needs.
type ScaleWindow = { min: number; max: number }
let pendingScaleRestore: { x: ScaleWindow | null; y: ScaleWindow | null } | null = null

/** A live scale's window, or null while it is still unset (empty construction data). */
function windowOf(scale: uPlot.Scale | undefined): ScaleWindow | null {
  return scale?.min != null && scale.max != null ? { min: scale.min, max: scale.max } : null
}

function ensurePlot() {
  const el = containerRef.value
  if (!el) return

  const next = signature()
  if (plot && plotSignature === next) return

  const nextX = xWindowKey()
  const nextY = yWindowKey()
  pendingScaleRestore = plot
    ? {
        x: plotXWindowKey === nextX ? windowOf(plot.scales.x) : null,
        y: plotYWindowKey === nextY ? windowOf(plot.scales.y) : null,
      }
    : null

  plot?.destroy()
  plotSignature = next
  plotXWindowKey = nextX
  plotYWindowKey = nextY

  const width = el.getBoundingClientRect().width || 800
  const blocks = 1 + activeOverlays.value.length // the live sweep, plus one block per overlay
  const slots = 1 + blocks * activeChannels.value.length * activeTraces.value.length
  // Same AlignedData typing wrinkle as SpectrumChart: a runtime-sized array cannot be contextually
  // typed against uPlot's tuple type. The runtime shape is correct; only the static type needs it.
  const empty = Array.from({ length: slots }, () => [] as number[]) as unknown as uPlot.AlignedData
  plot = new uPlot(buildOptions(width, chartHeight.value, isDark.value, true), empty, el)

  // uPlot's own double-click resets the x axis to the *data* extent, which on an auto:false chart
  // is whatever the zoom already shows — a no-op, and no way back out. Ours runs after it.
  plot.over.addEventListener('dblclick', resetZoom)
}

/** Reapplies a zoom carried across a plot rebuild. Called after setData, never before — see pendingScaleRestore. */
function applyPendingScaleRestore() {
  const pending = pendingScaleRestore
  pendingScaleRestore = null
  if (!plot || !pending) return

  if (pending.x) plot.setScale('x', pending.x)
  if (pending.y) plot.setScale('y', pending.y)
}

/**
 * Every series' data on the shared x grid, in buildOptions' series order — live block first, then
 * one block per active overlay in the same iteration order. Null with nothing to plot. Refills
 * measuredIdxBySeries as a side effect, which buildOptions' point filter reads.
 */
function buildData(): uPlot.AlignedData | null {
  const xs = xValues.value
  if (xs.length === 0) {
    measuredIdxBySeries = []
    return null
  }

  measuredIdxBySeries = [null] // slot 0 is the x series
  const data: (number | null)[][] = [xs]
  pushSeriesData(data, xs, points.value, thdMethod.value)
  for (const overlay of activeOverlays.value) pushSeriesData(data, xs, overlay.sweepPoints ?? [], methodOf(overlay))
  return data as unknown as uPlot.AlignedData
}

function update() {
  ensurePlot()
  if (!plot) return

  const data = buildData()
  if (!data) {
    plot.setData([[]] as unknown as uPlot.AlignedData)
    // Nothing plotted, so nothing to hold a zoom against — drop it rather than reapply it to an
    // empty chart, where the next real data would arrive already zoomed into a window chosen for
    // a sweep that is gone.
    pendingScaleRestore = null
    return
  }

  plot.setData(data)
  applyPendingScaleRestore()
}

/**
 * The chart as a PNG of exactly the requested size, drawn with the light theme whatever the screen's
 * — the same arrangement as FrequencyResponseChart.exportPng, which explains it. This chart's own
 * ordering is kept: empty construction, then setData, so the x range callback seeds the axis from the
 * dropdowns (see buildOptions). The legend is the same one the SVG legend under the chart draws.
 */
async function exportPng({ width, height, captureZoom = false, primaryLabel }: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header

  const host = document.createElement('div')
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(host)

  const restore = captureZoom && plot ? { x: windowOf(plot.scales.x), y: windowOf(plot.scales.y) } : null

  let u: uPlot | null = null
  try {
    const blocks = 1 + activeOverlays.value.length
    const slots = 1 + blocks * activeChannels.value.length * activeTraces.value.length
    const empty = Array.from({ length: slots }, () => [] as number[]) as unknown as uPlot.AlignedData
    u = new uPlot(buildOptions(width, plotHeight, false, false), empty, host)
    const data = buildData()
    if (data) u.setData(data)
    if (restore?.x) u.setScale('x', restore.x)
    if (restore?.y) u.setScale('y', restore.y)
    await nextPaint()

    const legend: ExportLegendEntry[] = legendGroupsFor(false).flatMap((group) =>
      group.entries.map((entry) => ({
        label: group.runIdx === 0 ? (primaryLabel ? `${primaryLabel} ${entry.label}` : entry.label) : `${group.name} ${entry.label}`,
        color: entry.stroke,
        dash: entry.dashArray,
      })),
    )

    return await composeChartPng({
      width,
      height,
      plotCanvas: u.ctx.canvas,
      headerHeight: header,
      title: `Distortion vs ${sweepXLabel(props.xAxis).toLowerCase()}`,
      legend,
    })
  } finally {
    u?.destroy()
    host.remove()
  }
}

defineExpose({ exportPng })

/**
 * Appends one channel x trace block of y arrays for a set of sweep points, aligned to `xs`.
 * Shared by the live sweep and every overlay so a stored sweep goes through the same valueOf/toY
 * conversion the running one does.
 *
 * Each series is expressed on the shared grid by interpolating between this block's own measured
 * x values (sweep/align.ts) rather than by an exact per-value lookup — a block measured on a
 * different grid than the one on screen would otherwise be all gaps except where the two grids
 * happen to collide.
 */
function pushSeriesData(data: (number | null)[][], xs: number[], sweepPoints: ThdSweepPoint[], method: ThdMethodName) {
  // Index the points once per block rather than searching per series per frequency: a stereo
  // sweep with nine traces would otherwise be O(traces x freqs x points).
  const byChannel = new Map<number, ThdSweepPoint[]>()
  for (const point of sweepPoints) {
    const list = byChannel.get(point.channel)
    if (list) list.push(point)
    else byChannel.set(point.channel, [point])
  }

  // The percent axis is logarithmic and the dB axis is linear, so a straight on-screen segment is
  // a log interpolation in the first case and a linear one in the second. Magnitude is dB-only, so
  // one flag per update covers every trace.
  const logY = props.unit === 'percent'
  const index = gridIndexByX.value

  for (const ch of activeChannels.value) {
    // Ascending grid slots this channel has a measurement at. Sorted explicitly: a running sweep
    // appends points as they are measured, and the union grid may reorder them anyway. On an
    // output-referred axis the sort is also not free -- a compressing DUT can measure a *lower*
    // output at a higher drive, so the points are not guaranteed monotonic in x.
    const channelPoints = (byChannel.get(ch) ?? []).slice().sort((a, b) => xOf(a) - xOf(b))
    const anchorIdx: number[] = []
    const anchorPoints: ThdSweepPoint[] = []
    for (const point of channelPoints) {
      const idx = index.get(xOf(point))
      if (idx === undefined) continue
      anchorIdx.push(idx)
      anchorPoints.push(point)
    }

    for (const trace of activeTraces.value) {
      const anchorValues = anchorPoints.map((p) => toY(valueOf(p, trace.key, method), trace.key))
      data.push(interpolateOnGrid(xs, anchorIdx, anchorValues, logY))
      // Markers belong at the measured frequencies only, and only where there is a value to mark.
      measuredIdxBySeries.push(anchorIdx.filter((_, k) => anchorValues[k] != null))
    }
  }
}

watch(points, update)
watch(
  () => signature(),
  () => update(),
)

// Observes the container rather than the window: the column this chart sits in also changes width
// when the measurement sidebar is pinned or unpinned, which is not a window resize — measured, the
// canvas stayed at 432 px inside a column that had grown to 661 px. Same mechanism as the
// ResizeObserver in FrequencyResponseChart.
let resizeObserver: ResizeObserver | null = null
function handleResize() {
  if (plot && containerRef.value)
    plot.setSize({ width: containerRef.value.getBoundingClientRect().width, height: chartHeight.value })
}
watch(chartHeight, handleResize)

onMounted(() => {
  resizeObserver = new ResizeObserver(handleResize)
  if (containerRef.value) resizeObserver.observe(containerRef.value)
  update()
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  plot?.destroy()
  plot = null
})
</script>

<template>
  <section ref="rootRef" class="relative flex flex-col gap-2 rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <ExpandToggle class="absolute right-3 top-3" :target="rootRef" />
    <div class="min-h-6 pr-7 flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>Distortion vs {{ sweepXLabel(props.xAxis).toLowerCase() }}</span>
      <span v-if="!hasData">No sweep data yet — press Run sweep.</span>
      <!-- The only visible sign that the axes are no longer the window the display panel's
           dropdowns name — double-clicking the plot does the same thing, but nothing on screen
           would say so. The hint takes its place while unzoomed rather than sitting alongside it,
           so the header never carries two competing messages about the same gesture. -->
      <button
        v-else-if="zoomed"
        type="button"
        class="inline-flex items-center gap-1 rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Back to the From/To and distortion-range window set in the display panel."
        @click="resetZoom"
      >
        <ToolbarIcon name="reset-zoom" />
        Reset zoom
      </button>
      <span v-else>Drag to zoom · double-click to reset</span>
    </div>
    <div class="relative">
      <div ref="containerRef" class="sweep-chart w-full"></div>
      <!-- Off-scale cue: a checked trace has a value above/below the currently visible y window. -->
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

    <!-- Replaces uPlot's own legend, which could only draw a solid or dashed marker and so could
         never say which of the three history patterns a row belonged to. Every swatch is drawn
         from the same styleOf() that built the series, so it shows the actual line. -->
    <div v-if="hasData" class="flex flex-col gap-1.5 border-t border-neutral-200 pt-2 dark:border-neutral-800">
      <div v-for="group in legendGroups" :key="group.key" class="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span class="w-28 shrink-0 truncate text-xs text-neutral-500 dark:text-neutral-400" :title="group.name">
          {{ group.name }}
        </span>
        <span
          v-for="entry in group.entries"
          :key="entry.key"
          class="flex items-center gap-1 text-xs text-neutral-600 dark:text-neutral-300"
        >
          <svg width="20" height="6" viewBox="0 0 20 6" aria-hidden="true" class="shrink-0 overflow-visible">
            <line
              x1="0"
              y1="3"
              x2="20"
              y2="3"
              :stroke="entry.stroke"
              :stroke-width="entry.width"
              :stroke-dasharray="entry.dash"
            />
          </svg>
          {{ entry.label }}
        </span>
      </div>
    </div>

    <!-- The full reading at the selected frequency: one header, then one line per channel, and the
         same block again for every history result checked on. Replaces the label-above-value
         columns this used to be, which repeated all ten labels for each channel and again for each
         run — unreadable as a comparison, which is the only reason to show two runs at once. -->
    <div v-if="metricGroups.length" class="overflow-x-auto border-t border-neutral-200 pt-2 dark:border-neutral-800">
      <table class="min-w-full text-xs">
        <!-- One table for every run, a tbody each, rather than a table per run: a browser sizes a
             table's columns across all of its rows, so this is what makes THD sit under THD when
             the live sweep reads 22 Hz and a history result reads 90 kHz. Separate tables each
             sized their own columns and the blocks came out staggered. The run's name is a
             full-width row for the same reason — a heading outside the table would not be part of
             the column grid, and the group would drift from it. -->
        <tbody v-for="group in metricGroups" :key="group.key">
          <tr v-if="group.name">
            <td
              :colspan="group.columns.length + 1"
              class="truncate pt-2 pb-0.5 text-neutral-500 dark:text-neutral-400"
              :title="group.name"
            >
              {{ group.name }}
            </td>
          </tr>
          <tr class="text-neutral-400 dark:text-neutral-500">
            <th class="py-0.5 pr-4 text-left font-normal"><span class="sr-only">Channel</span></th>
            <th
              v-for="col in group.columns"
              :key="col"
              class="py-0.5 pr-4 text-left font-normal whitespace-nowrap"
            >
              {{ col }}
            </th>
          </tr>
          <tr
            v-for="row in group.rows"
            :key="row.key"
            class="border-t border-neutral-100 dark:border-neutral-800"
          >
            <td class="py-0.5 pr-4 whitespace-nowrap" :class="row.colorClass">
              <span class="mr-1.5 inline-block h-1.5 w-1.5 rounded-full bg-current align-middle"></span>
              {{ row.label }}
            </td>
            <td
              v-for="(value, i) in row.values"
              :key="i"
              class="py-0.5 pr-4 font-mono whitespace-nowrap text-neutral-800 dark:text-neutral-100"
            >
              {{ value }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>

