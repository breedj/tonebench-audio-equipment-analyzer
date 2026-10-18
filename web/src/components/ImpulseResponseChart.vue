<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { ImpulseTraceJson } from '../api/client'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { CHART_EXPORT_HEADER_PX, composeChartPng, nextPaint, type ChartExportOptions } from '../charts/exportChart'
import ExpandToggle from './ExpandToggle.vue'
import ChartZoomControls from './ChartZoomControls.vue'
import type { ZoomWindow } from '../charts/zoomHistory'
import { useAppTheme } from '../composables/useAppTheme'
import type { OverlayEntry } from '../composables/useMeasurements'
import { useZoomBounds } from '../composables/useZoomBounds'
import { impulseTimeGrid, resampleImpulse } from '../measurements/impulseOverlay'
import { formatImpulsePeak, impulseUnit } from '../measurements/impulseUnit'
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
import { sweepOverlayDash } from '../measurements/sweepOverlayStyles'
import { exportGridTheme } from '../charts/palette'

/**
 * The impulse response itself, on a **linear** time axis with the impulse at t = 0.
 *
 * <para>Linear, not logarithmic, and that is the one thing that makes this chart unlike every other
 * one in the app: an impulse response is a waveform, its x axis is time, and a log grid would crush
 * the whole decay tail into the last few pixels.</para>
 *
 * <para>Min and max are both drawn — the max as the line, the min as a band beneath it — for the
 * same reason the spectrum charts do it. It matters more here than anywhere: a reflection can be a
 * single sample, and a column that showed only one side of the waveform would hide half of them.</para>
 */
const annotationArmed = defineModel<AnnotationStyle | null>('annotationArmed', { default: null })
/** The measurement's notes, across its charts; this one draws the impulse trace's. See FrequencyResponseChart. */
const annotationList = defineModel<ChartAnnotation[]>('annotations', { default: () => [] })

// `annotationNumbers` is defaulted rather than left absent: Vue casts an absent Boolean prop
// with no default to `false`, which silently turned the ordinal badges off on every chart but
// the FFT's -- see AnnotationOverlayOptions.numbers for the whole of it.
const props = withDefaults(defineProps<{
  /** Whether the ordinal badges are drawn; off in a report whose Annotations table was not included. */
  annotationNumbers?: boolean
  impulse: ImpulseTraceJson | null
  /**
   * Checked history items. Drawn as a line each, in the trace colour with the run's own dash — the
   * convention the response and impedance charts use, where colour says which quantity and the dash
   * says which run. No min/max band: three overlapping envelopes are mud, and the band belongs to
   * the measurement being read rather than to what it is compared against.
   */
  overlays: OverlayEntry[]
  /** dB relative to the impulse's own peak, rather than volts. What you want for reading a decay. */
  normalised: boolean
  thickLines: boolean
  timeFromMs: number
  timeToMs: number
  /** Only used by the normalised view; the volts view autoscales, since its range is the DUT's. */
  yTopDb: number
  yBottomDb: number
}>(), { annotationNumbers: true })

const { isDark } = useAppTheme()

const root = ref<HTMLElement | null>(null)
const container = ref<HTMLDivElement | null>(null)
// Fitted to the view — see charts/useResultChartHeight.ts. uPlot sizes the container itself, so a
// new height is applied through setSize, the same call a width change makes below.
const chartHeight = useResultChartHeight(root, container)
const plot = shallowRef<uPlot | null>(null)

const THEME = {
  dark: { axis: '#a3a3a3', grid: '#404040', gridWidth: 1, trace: '#60a5fa', marker: '#f59e0b' },
  light: { axis: '#525252', grid: '#e5e5e5', gridWidth: 1, trace: '#1d6fd0', marker: '#b45309' },
} as const

type Theme = (typeof THEME)[keyof typeof THEME]

// The screen's theme; the export builds its own light plot and hands the theme in as an argument
// rather than reading this at draw time — see charts/palette.ts for why.
function theme(): Theme {
  return isDark.value ? THEME.dark : THEME.light
}

/** dB re the impulse's own peak, floored so a zero sample does not become -Infinity. */
function toDb(volts: number, peak: number): number {
  return 20 * Math.log10(Math.max(Math.abs(volts), 1e-12) / Math.max(peak, 1e-12))
}

// Both pure, and in their own module so they can be unit-tested: nothing in a .vue file is testable
// under the node environment vitest runs in. See impulseUnit.ts for why the scaling exists at all.
/** The checked history items that actually carry a waveform; any other kind is skipped, not drawn empty. */
const overlayTraces = computed(() =>
  props.overlays
    .map((o) => ({ id: o.id, name: o.detail.name, trace: o.impulseTrace ?? null }))
    .filter((o): o is { id: number; name: string; trace: ImpulseTraceJson } => o.trace !== null),
)

/**
 * What the volts axis is scaled by — the live impulse's peak, or the loudest overlay's when there is
 * no live trace yet, so a history item checked on before a run is drawn in a sensible unit instead
 * of scaled against zero.
 */
const referencePeakVolts = computed(() => {
  if (props.impulse) return props.impulse.peakVolts
  let peak = 0
  for (const o of overlayTraces.value) peak = Math.max(peak, o.trace.peakVolts)
  return peak
})

const unit = computed(() => impulseUnit(referencePeakVolts.value))

const peakLabel = computed(() =>
  props.impulse ? formatImpulsePeak(props.impulse.peakVolts, unit.value) : '',
)

/**
 * The y window when nothing is zoomed.
 *
 * The volts view used to let uPlot autoscale, which is the right picture but leaves "not zoomed"
 * undefined — there is no window to compare a drag against, and so nothing to reset to. Fitting the
 * data explicitly gives the same view and makes both work.
 */
const yWindow = computed(() => {
  if (props.normalised) return { min: props.yBottomDb, max: props.yTopDb }

  const { min, max, overlays } = series.value
  if (min.length === 0) return { min: -1, max: 1 }

  let lo = Number.POSITIVE_INFINITY
  let hi = Number.NEGATIVE_INFINITY
  // Nulls are the gaps outside a curve's own span (see impulseOverlay.ts) and are not levels.
  const note = (v: number | null) => {
    if (v === null) return
    if (v < lo) lo = v
    if (v > hi) hi = v
  }

  for (let i = 0; i < min.length; i++) {
    note(min[i])
    note(max[i])
  }
  // The overlays are inside the window as well, or checking one on would draw it off the top.
  for (const overlay of overlays) for (const v of overlay.values) note(v)

  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return { min: -1, max: 1 }

  const pad = (hi - lo) * 0.05 || 0.1
  return { min: lo - pad, max: hi + pad }
})

/** One curve's two envelope halves on the shared grid; equal, and the magnitude, in the dB view. */
function envelopeOf(trace: ImpulseTraceJson, grid: readonly number[]) {
  const timeMs = trace.timeS.map((t) => t * 1000)

  if (!props.normalised) {
    const factor = unit.value.factor
    return {
      min: resampleImpulse(timeMs, trace.minVolts.map((v) => v * factor), grid),
      max: resampleImpulse(timeMs, trace.maxVolts.map((v) => v * factor), grid),
    }
  }

  // In dB the two envelope halves stop being "min" and "max": what a reader wants is the column's
  // largest excursion either way, so the band collapses to one magnitude curve. Drawing 20·log of a
  // negative minimum would be arithmetic on a sign that no longer means anything.
  //
  // Each curve is dB re **its own** peak, overlays included, which is the point of the view: it puts
  // two decays side by side without the louder run simply sitting above the other.
  const peak = Math.max(trace.peakVolts, 1e-12)
  const magnitude = trace.maxVolts.map((v, i) =>
    toDb(Math.max(Math.abs(v), Math.abs(trace.minVolts[i])), peak),
  )
  const resampled = resampleImpulse(timeMs, magnitude, grid)
  return { min: resampled, max: resampled }
}

/**
 * Every curve on one time axis. See impulseOverlay.ts for why the grid is the live trace's own
 * columns rather than a union: on them the live envelope is reproduced exactly, so nothing about the
 * measurement changes when a history item is checked on.
 */
const series = computed(() => {
  const impulse = props.impulse
  const overlays = overlayTraces.value

  const grid = impulseTimeGrid(
    impulse ? impulse.timeS.map((t) => t * 1000) : [],
    overlays.map((o) => o.trace.timeS.map((t) => t * 1000)),
  )

  const empty = { timeMs: [] as number[], min: [] as (number | null)[], max: [] as (number | null)[], overlays: [] as { id: number; name: string; values: (number | null)[] }[] }
  if (grid.length === 0) return empty

  const live = impulse ? envelopeOf(impulse, grid) : { min: grid.map(() => null), max: grid.map(() => null) }

  return {
    timeMs: grid,
    min: live.min,
    max: live.max,
    overlays: overlays.map((o) => ({ id: o.id, name: o.name, values: envelopeOf(o.trace, grid).max })),
  }
})

/**
 * The min/max band, stroked as one vertical per column. The project's decimation rule: the server
 * sends both, and a client that drew only the max would redraw the waveform riding along one edge
 * of itself.
 */
function minMaxBandPlugin(t: Theme): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          if (props.normalised) return
          const { min, max, timeMs } = series.value
          if (timeMs.length === 0) return

          const ctx = u.ctx
          ctx.save()
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          const path = new Path2D()
          for (let i = 0; i < timeMs.length; i++) {
            // Null where the live trace does not reach — a column an overlay's wider gate added.
            const lo = min[i]
            const hi = max[i]
            if (lo === null || hi === null) continue

            const x = u.valToPos(timeMs[i], 'x', true)
            path.moveTo(x, u.valToPos(lo, 'y', true))
            path.lineTo(x, u.valToPos(hi, 'y', true))
          }

          ctx.strokeStyle = t.trace
          ctx.globalAlpha = 0.35
          ctx.lineWidth = 1
          ctx.stroke(path)
          ctx.restore()
        },
      ],
    },
  }
}

/** A dashed vertical at t = 0 — where the impulse is, and the origin every reflection is read from. */
function impulseMarkerPlugin(t: Theme): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
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
          ctx.restore()
        },
      ],
    },
  }
}

/**
 * True while either axis shows something other than the window the display panel asks for. Drives
 * the Reset zoom button, which is the only visible sign the axes are no longer the panel's.
 */
const { zoomed, noteScale, resetZoom, noteZoomGesture, zoomBack, currentWindow, canGoBack } = useZoomBounds(
  () => plot.value,
  () => ({ xMin: props.timeFromMs, xMax: props.timeToMs, yMin: yWindow.value.min, yMax: yWindow.value.max }),
)

/** "Set axis" hands the window on screen up — the display panel's boxes are the parent's. */
/**
 * This chart's own drag-zoom, and what the Box gesture puts back when it is disarmed — one constant
 * so `cursor.drag` and `syncBoxDragMode` cannot disagree. See BoxDragResting.
 */
const DRAG_RESTING: BoxDragResting = { y: true, uni: 12 }

const emit = defineEmits<{ axisWindow: [window: ZoomWindow] }>()

/** "Set axis": hands the window on screen up, for the display panel's own boxes to take it on. */
function applyZoomToAxis() {
  const w = currentWindow()
  if (w) emit('axisWindow', w)
}


/**
 * @param t The theme every colour is taken from.
 * @param interactive False for the export's offscreen plot: no cursor and no zoom bookkeeping.
 */
function buildOptions(width: number, height: number, t: Theme, interactive: boolean): uPlot.Options {
  return {
    width,
    height,
    scales: {
      // Arguments passed through rather than ignored, so a drag-zoom survives setScale — the same
      // idiom the other charts use. Note that what actually puts the panel's window on the x axis is
      // refresh()'s explicit setScale, not the `??` here: on an x scale that fallback is dead code,
      // because uPlot only ever passes null for x when there is no data yet. Drop those setScale
      // calls and this axis silently reverts to the data's own span. See CLAUDE.md.
      x: { time: false, auto: false, range: (_u, min, max) => [min ?? props.timeFromMs, max ?? props.timeToMs] },
      y: { auto: false, range: (_u, min, max) => [min ?? yWindow.value.min, max ?? yWindow.value.max] },
    },
    axes: [
      {
        label: 'Time (ms)',
        stroke: t.axis,
        grid: { stroke: t.grid, width: t.gridWidth },
        ticks: { stroke: t.grid, width: t.gridWidth },
      },
      {
        label: props.normalised ? 'Level (dB re peak)' : unit.value.label,
        size: 64,
        stroke: t.axis,
        grid: { stroke: t.grid, width: t.gridWidth },
        ticks: { stroke: t.grid, width: t.gridWidth },
      },
    ],
    series: [
      { label: 'Time (ms)' },
      {
        label: props.normalised ? 'Level' : 'Impulse',
        stroke: t.trace,
        width: props.thickLines ? 2 : 1,
        points: { show: false },
      },
      // One per checked history item, in the order series.value.overlays lists them — refresh()
      // stacks the data arrays in that same order, so the two agree position for position.
      ...series.value.overlays.map((overlay) => ({
        label: overlay.name,
        stroke: t.trace,
        width: props.thickLines ? 2 : 1,
        dash: sweepOverlayDash(props.overlays, overlay.id, props.impulse !== null, null) ?? undefined,
        points: { show: false },
        alpha: props.impulse === null ? 1 : 0.7,
      })),
    ],
    // Last: whatever draws after the annotations paints over their labels.
    plugins: [
      minMaxBandPlugin(t),
      impulseMarkerPlugin(t),
      // `t` is one of two frozen tables rather than a palette object, so the theme is read
      // from the one that built it.
      annotationsPlugin(annotationOverlay, ANNOTATION_AXES.value, () => annotationTheme(t === THEME.dark)),
    ],
    // Both axes, because a reflection is read off the time axis and its level off the other, and
    // demanding a pixel-accurate one-axis drag to get either would make both awkward.
    cursor: interactive ? { drag: { x: true, ...DRAG_RESTING } } : { show: false },
    legend: { show: false },
    // Fires on setData too (both scales are auto:false, so setData re-applies the same window),
    // hence the change gate inside noteScale.
    hooks: interactive
      ? {
          setScale: [(u: uPlot) => noteScale(u)],
          // The window each drag leaves behind, for Back: uPlot fires setSelect before it applies
          // the drag's own scales, so the plot still holds the window to return to.
          setSelect: [(u: uPlot) => noteZoomGesture(u)],
        }
      : {},
  }
}

/**
 * The chart as a PNG of exactly the requested size, drawn with the light theme whatever the screen's
 * — the same arrangement as FrequencyResponseChart.exportPng, which explains it. This chart's own
 * ordering is kept: empty construction, setData, then the explicit setScale pair, which is what puts
 * the window on the axes (see buildOptions).
 */
async function exportPng({ width, height, captureZoom = false }: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header

  const host = document.createElement('div')
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(host)

  const live = plot.value
  const xWindow =
    captureZoom && live?.scales.x.min != null && live.scales.x.max != null
      ? { min: live.scales.x.min, max: live.scales.x.max }
      : { min: props.timeFromMs, max: props.timeToMs }
  const yWin =
    captureZoom && live?.scales.y.min != null && live.scales.y.max != null
      ? { min: live.scales.y.min, max: live.scales.y.max }
      : { min: yWindow.value.min, max: yWindow.value.max }

  let u: uPlot | null = null
  try {
    // Same series order as the live plot, overlays included — a report of this chart shows what is
    // on screen, so a checked history item has to reach the PNG too.
    const { timeMs, max, overlays } = series.value
    const blank = [[], [], ...overlays.map(() => [])] as unknown as uPlot.AlignedData
    u = new uPlot(buildOptions(width, plotHeight, exportGridTheme(THEME.light), false), blank, host)
    u.setData([timeMs, max, ...overlays.map((o) => o.values)] as uPlot.AlignedData)
    u.setScale('x', xWindow)
    u.setScale('y', yWin)
    await nextPaint()

    const impulse = props.impulse
    const detail = impulse
      ? ` — peak ${peakLabel.value} · gate −${impulse.gatePreMs}/+${impulse.gatePostMs} ms`
      : ''
    return await composeChartPng({
      width,
      height,
      plotCanvas: u.ctx.canvas,
      headerHeight: header,
      title: `Impulse response${props.normalised ? ' (dB re peak)' : ''}${detail}`,
      legend: [{ label: props.normalised ? 'Level' : 'Impulse', color: THEME.light.trace }],
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

  plot.value?.destroy()
  const width = el.getBoundingClientRect().width || 600
  // One empty array per declared series, overlays included. Empty construction then setData is
  // also what makes the x window honour the panel's props rather than the data's span (CLAUDE.md).
  const blank = [[], [], ...series.value.overlays.map(() => [])] as unknown as uPlot.AlignedData
  plot.value = new uPlot(buildOptions(width, chartHeight.value, theme(), true), blank, el)

  // Click-to-annotate, and the drag mode an armed Box needs. Re-applied on every rebuild: a fresh
  // instance carries uPlot's own defaults, so an armed chart would silently go back to zooming.
  attachAnnotationListeners(plot.value, annotationOverlay)
  syncBoxDragMode(plot.value, annotationOverlay.armedForBox.value, DRAG_RESTING)
  // uPlot has no double-click-to-reset of its own once drag zoom is on.
  plot.value.over.addEventListener('dblclick', resetZoom)
  refresh()
}

function refresh() {
  const p = plot.value
  if (!p) return

  const { timeMs, max, overlays } = series.value
  // setData on the existing instance, never a new plot per frame. The overlay arrays follow the
  // live one in the order buildOptions declared their series.
  p.setData([timeMs, max, ...overlays.map((o) => o.values)] as uPlot.AlignedData)
  p.setScale('x', { min: props.timeFromMs, max: props.timeToMs })
  p.setScale('y', { min: yWindow.value.min, max: yWindow.value.max })
}

let observer: ResizeObserver | null = null

function fit() {
  const el = container.value
  if (el && plot.value) plot.value.setSize({ width: el.getBoundingClientRect().width, height: chartHeight.value })
}
watch(chartHeight, fit)

/**
 * What this chart's two axes are, in annotation terms.
 *
 * <p>Its x is a <b>linear time in milliseconds that crosses zero</b> — the pre-ringing before the
 * impulse sits at a negative time — which is the case the parser's "any finite x" rule and the
 * linear offset space exist for. A log axis has no position for t = 0 at all.</p>
 *
 * <p>Its y quantity follows the view, like the frequency-response chart's: volts in the volts view,
 * dB re peak in the normalised one. Not volts converted to dB, which could not be converted back —
 * an impulse swings negative and a sign does not survive a logarithm. So a note placed on one view
 * is skipped on the other rather than redrawn wrongly, and returns on the way back. The volts
 * view's axis is auto-scaled to V, mV or µV; the note is stored in <b>volts</b> and the factor
 * applied on the way out, so it keeps its place when a quieter measurement changes the unit.</p>
 */
const ANNOTATION_AXES = computed<UplotAnnotationAxes>(() => ({
  x: { quantity: 'ms' },
  y: props.normalised
    ? { quantity: 'db' }
    : {
        quantity: 'volt',
        toDisplay: (volts) => volts * unit.value.factor,
        fromDisplay: (shown) => shown / unit.value.factor,
      },
}))

const annotationOverlay = useAnnotationOverlay({
  view: () => 'Impulse',
  list: annotationList,
  armed: annotationArmed,
  numbers: () => props.annotationNumbers !== false,
  cssPlot: () => (plot.value ? cssAnnotationPlot(plot.value, ANNOTATION_AXES.value) : null),
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

onMounted(() => {
  window.addEventListener('mouseup', onWindowMouseUp)
  window.addEventListener('keydown', onWindowKeyDown)
  rebuild()
  observer = new ResizeObserver(fit)
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  window.removeEventListener('mouseup', onWindowMouseUp)
  window.removeEventListener('keydown', onWindowKeyDown)
  observer?.disconnect()
  observer = null
  plot.value?.destroy()
  plot.value = null
})

// A theme change or a change of axis meaning needs the whole plot rebuilt; new data does not.
watch(
  [
    isDark,
    () => props.normalised,
    () => props.thickLines,
    () => unit.value.label,
    // Series count and labels are baked in at construction, so checking a history item on or off is
    // a rebuild rather than a redraw — the same rule the sweep and response charts follow.
    () => series.value.overlays.map((o) => o.id).join(','),
  ],
  rebuild,
)
watch(
  [() => props.impulse, () => props.overlays, () => props.timeFromMs, () => props.timeToMs, () => props.yTopDb, () => props.yBottomDb],
  refresh,
)
</script>

<template>
  <!-- A card like every panel beside it — the 2026-10-12 mockups. -->
  <section ref="root" class="relative flex flex-col gap-1 panel-surface rounded-xl border border-neutral-200 p-3 dark:border-neutral-800">
    <ExpandToggle class="absolute right-3 top-3" :target="root" />
    <div class="min-h-6 pr-9 flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>Impulse response</span>
      <span v-if="impulse">
        peak {{ peakLabel }} · gate −{{ impulse.gatePreMs }}/+{{ impulse.gatePostMs }} ms ·
        {{ impulse.fullSampleCount }} samples · nothing below {{ impulse.resolutionLimitHz.toFixed(0) }} Hz is resolved
      </span>
      <span v-else>No impulse response measured</span>

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
    </div>
  </section>
</template>
