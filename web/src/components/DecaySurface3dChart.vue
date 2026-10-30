<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { DecayChartMode, DecayPalette, DecaySurface } from '../api/client'
import { formatHz } from '../measurements/decay'
import {
  decayAcrossLabel,
  decayCameraAngles,
  decayCameraDistance,
  decayCenterPercent,
  decayColormap,
  decayDepthLabel,
  decayExportTitle,
  decayPointFrequencyHz,
  decayPointTime,
  decayOptionsKey,
  decayPoints,
  decayVerticalRatio,
  type DecayPointCloud,
} from '../measurements/decay3d'
import {
  applyVerticalRatio,
  createGraph3d,
  destroyGraph3d,
  type Graph3dInstance,
  type Graph3dOptions,
  type Graph3dPoint,
} from '../charts/graph3d'
import { CHART_EXPORT_HEADER_PX, composeChartPng, type ChartExportOptions } from '../charts/exportChart'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { useAppTheme } from '../composables/useAppTheme'
import ExpandToggle from './ExpandToggle.vue'

/**
 * <p>The decay surface as a turnable, zoomable 3D box, drawn by <c>vis-graph3d</c>.</p>
 *
 * <p>It takes the same props as <c>DecaySurfaceChart</c> and exposes the same <c>exportPng</c>, so
 * the two are interchangeable at the call site; <c>App.vue</c> and <c>report/render.ts</c> pick
 * between them on the user's own <b>Decay surfaces</b> preference and on whether the mode is a
 * waterfall at all.</p>
 *
 * <p><b>It replaced a WebGPU engine on 2026-10-30, and the one thing to carry forward from that is
 * why a capability probe is no longer needed:</b> the old engine, handed no GPU adapter, drew a
 * plausible lit surface that was not the data and said nothing about it. This one is a plain 2D
 * canvas with a painter's algorithm, so it draws the same picture everywhere there is a canvas —
 * which is also what lets a PDF report carry it.</p>
 *
 * <p>The arithmetic is in <c>measurements/decay3d.ts</c>, which reads the surface through the same
 * <c>orientSurface</c> and <c>levelAt</c> the Canvas 2D chart uses, so the two cannot disagree
 * about which way round a waterfall goes or where a ragged slice begins.</p>
 */
const props = defineProps<{
  surface: DecaySurface | null
  mode: DecayChartMode
  palette: DecayPalette
  /** Accepted so the two charts are prop-compatible; the Canvas 2D painter's, and unread here. */
  coloredCurves: boolean
  filled: boolean
  /** Rule the surface along every cell edge — this chart's counterpart to those two. */
  meshLines: boolean
  rotationDeg: number
  depthPercent: number
  /**
   * The four view controls of the panel's own collapsed section — where the surface sits and how
   * large and how tall it is drawn. See `measurements/decay3d.ts` for their ranges; all four are
   * stored with the measurement, which is what lets an awkward view be kept.
   */
  moveX: number
  moveY: number
  magnification: number
  /** How tall the box is drawn against its own floor; see DECAY_EXPLODE_Z. */
  explodeZ: number
  rangeDb: number
  /** The Canvas 2D painter's, and unread here. */
  thickLines: boolean
  computing: boolean
  error: string | null
  emptyMessage: string
  /**
   * A fixed drawing height, for a chart that is one of several stacked in the view.
   *
   * <p>A decay surface cannot overlay, so comparing runs means a chart each — and fitting every one
   * of them to the view would give four charts four full views. Left undefined by the one chart
   * that is the view's own, which then fits it as before.</p>
   */
  pageHeightPx?: number
}>()

/**
 * <p><b>This chart does not emit its view back, and the Canvas 2D one does.</b> That is not an
 * oversight: the two quantities are not the same. The panel's Rotation is a lean of an oblique
 * projection, bounded to ±60°, while this library's horizontal angle is a camera that turns right
 * round the circle. Writing a dragged angle back through the panel's field would clamp it, and the
 * next prop watcher would write the clamped value into the camera — so the surface would spring
 * back part way through every turn.</p>
 *
 * <p>So the fields set the view, and from the first drag the library's camera owns it. What that
 * costs is one thing and it is paid for in {@link exportPng}: an export reads the <em>live</em>
 * camera rather than rebuilding it from the props, so a screenshot is still the picture on
 * screen.</p>
 */

const container = ref<HTMLDivElement | null>(null)
const host = ref<HTMLDivElement | null>(null)
// First in setup, so the chart is built at the height it will keep rather than resized a frame
// later — see the composable's own remarks.
const chartHeight = useResultChartHeight(container, host, { pageHeightPx: () => props.pageHeightPx })

const { isDark } = useAppTheme()

const failure = ref<string | null>(null)

const MIN_WIDTH = 240

/**
 * <p>How far the library insets its colour key from the top and right edges, where it defaults to
 * 10 — raised so the key clears the expand button in that same corner.</p>
 *
 * <p>The button is 28 px at 8 from each edge, so it holds x from `width - 36` to `width - 8` and y
 * from 8 to 36; the key is a 20 px bar at x from `width - margin - 20` to `width - margin`, with
 * its labels further left again and its top at `margin`. At 44 the bar ends 8 px clear of the
 * button's left edge and starts 8 px below its bottom, so neither has to give way. **This is why
 * the button is on the right at all**: it sat top left for a day because the key could not be
 * moved, which is true of `setOptions` and not of the field itself (Joost, 2026-10-30).</p>
 */
const LEGEND_MARGIN_PX = 44

/** How many whole decades of the frequency axis a tick is put on. */
const FREQUENCY_DECADE_STEP = 1

/**
 * Whether a surface is on screen, which is to say whether `chart` is built — kept as a ref because
 * `chart` is not reactive and the template needs to know, and the one thing that decides whether
 * the empty placeholder below is drawn instead.
 */
const drawn = ref(false)

let chart: Graph3dInstance | null = null
let cloud: DecayPointCloud | null = null
let disposed = false
let observer: ResizeObserver | null = null
let redrawFrame = 0

function plotWidth(): number {
  return Math.max(MIN_WIDTH, Math.round(container.value?.clientWidth ?? MIN_WIDTH))
}

/**
 * The inks the box is drawn in. The chart paints its own well rather than taking
 * <c>--surface-plot</c>, for the reason the Canvas 2D chart does: an export forces the light theme
 * while the app may be dark.
 */
function theme(dark: boolean) {
  return {
    // <p><b>The mesh takes this ink, and that is the library's doing rather than a choice.</b>
    // `_redrawSurfaceGraphPoint` strokes each cell with `this.axisColor` — its own source marks
    // that line "should be customizable" — so the mesh cannot be coloured apart from the axes.
    // On the dark style the ink is a near-white that already reads as white against the palette;
    // it is lifted the rest of the way when the mesh is actually on, which is imperceptible on an
    // axis label and is the difference between a white mesh and an off-white one. A light export
    // keeps its dark ink, since white lines would whiten every tick and label on it too.</p>
    ink: dark ? (props.meshLines ? '#ffffff' : '#d4d4d4') : '#404040',
    grid: dark ? '#3f3f46' : '#d4d4d4',
    back: dark ? '#15181d' : '#ffffff',
  }
}

/** The well the empty placeholder paints, so it and the drawn chart cannot differ. */
const wellColour = computed(() => theme(isDark.value).back)

/**
 * <p>Every option the chart is built and updated with. Split out so the live chart, a resize and
 * the export copy cannot come to disagree about anything but the three things they differ in —
 * size, theme and camera.</p>
 */
function buildOptions(
  points: DecayPointCloud | null,
  width: number,
  height: number,
  dark: boolean,
  camera: { horizontal: number; vertical: number; distance: number },
): Graph3dOptions {
  const ink = theme(dark)
  const across = points?.acrossIsFrequency ?? true
  const depthIsFrequency = points?.depthIsFrequency ?? false
  const timeLabel = `Time (${timeUnit()})`
  const centre = decayCenterPercent(props.moveX, props.moveY)

  return {
    style: 'surface',
    width: `${width}px`,
    height: `${height}px`,
    ...centre,
    // False is what draws the floor square: each axis normalises to unit length on its own. True
    // makes the longer-ranged axis longer, which is meaningless where the two are frequency and
    // time — they have no common unit, and the Canvas 2D chart does not preserve a ratio either.
    keepAspectRatio: false,
    verticalRatio: decayVerticalRatio(props.explodeZ),
    // Parallel rather than perspective: a waterfall is read in the near-isometric view, and the
    // previous engine was held at a long lens for the same reason. It is also what makes
    // magnification exactly the reciprocal of the camera's arm.
    showPerspective: false,
    showGrid: true,
    // The library's only shading: a per-quad brightness from the angle the face is seen at, with no
    // light to position. It is what keeps a decay's steps reading as steps rather than as flat
    // colour — the two light-angle sliders went with the old engine, there being nothing to turn.
    showShadow: true,
    // <p>Off by default, and not for speed — measured at 388 ms against 395 ms, inside the noise.
    // It strokes every cell border, and at a cumulative spectral decay's twenty-odd thousand cells
    // that is a wash rather than a mesh; on a coarser analysis, or magnified into a corner of one,
    // it is what shows the shape, so it is the user's call. With it off each quad is stroked in
    // its own fill colour, which closes the hairline seams between them.</p>
    showSurfaceGrid: props.meshLines,
    showGrayBottom: false,
    zoomable: true,
    ctrlToZoom: false,
    rotateAxisLabels: true,
    cameraPosition: camera,
    zMin: -Math.abs(props.rangeDb),
    zMax: 0,
    xLabel: across ? 'Frequency (Hz)' : timeLabel,
    yLabel: depthIsFrequency ? 'Frequency (Hz)' : timeLabel,
    zLabel: 'dB',
    // Whole decades on the frequency axis, which carries its own logarithm — see
    // `decayAcrossCoordinate`. The library has no logarithmic scale of its own.
    ...(across ? { xStep: FREQUENCY_DECADE_STEP } : {}),
    ...(depthIsFrequency ? { yStep: FREQUENCY_DECADE_STEP } : {}),
    xValueLabel: (v: number) => decayAcrossLabel(v, across),
    yValueLabel: (v: number) => decayDepthLabel(v, depthIsFrequency),
    zValueLabel: (v: number) => v.toFixed(0),
    colormap: decayColormap(props.palette, dark),
    axisColor: ink.ink,
    gridColor: ink.grid,
    backgroundColor: { fill: ink.back, stroke: ink.back, strokeWidth: 0 },
    // The key, which the library draws for a surface as a gradient bar keyed to the height range.
    showLegend: true,
    legendLabel: 'dB',
    tooltip: (point: Graph3dPoint) => tooltipFor(point, points),
    tooltipDelay: 120,
  }
}

/**
 * Frequency, then time, then level — the order the Canvas 2D chart's readout uses.
 *
 * <p>Read back through the cloud rather than off the coordinates directly, because which floor
 * axis carries the frequency is the view's business and both of them carry a logarithm when they
 * do. A readout that assumed the across axis would print the <em>depth</em> coordinate as hertz
 * on the by-period view, which is a plausible-looking wrong number.</p>
 */
function tooltipFor(point: Graph3dPoint, cloud: DecayPointCloud | null): string {
  if (!cloud) return `${point.z.toFixed(1)} dB`
  const hz = decayPointFrequencyHz(point, cloud)
  const time = decayPointTime(point, cloud)
  const where = Number.isFinite(hz) ? `${formatHz(hz)} Hz, ` : ''
  return `${where}${time.toFixed(2)} ${timeUnit()} → ${point.z.toFixed(1)} dB`
}

function timeUnit(): string {
  return props.surface?.sliceAxis === 'Periods' ? 'periods' : 'ms'
}

/** The camera the props describe. The live one may have been dragged past it; see the block above. */
function cameraFromProps() {
  return {
    ...decayCameraAngles(props.rotationDeg, props.depthPercent),
    distance: decayCameraDistance(props.magnification),
  }
}

function surfaceCloud(): DecayPointCloud | null {
  const surface = props.surface
  if (!surface || surface.slices.length === 0) return null
  const cloud = decayPoints(surface, props.mode, props.rangeDb)
  // A surface whose every cell went unmeasured is as empty as no surface at all, and the library
  // can be handed neither — see `refreshSurface`. Reachable rather than theoretical: a burst decay
  // leaves out every column whose periods run past the record, so a short enough one leaves none.
  return cloud.points.length > 0 ? cloud : null
}

function build() {
  const element = host.value
  if (!element) return
  cloud = surfaceCloud()
  // Nothing to draw: no chart at all, and the template's empty state is the whole of what is on
  // screen. See `refreshSurface` for why an empty one is not merely pointless but impossible.
  if (!cloud) {
    failure.value = null
    drawn.value = false
    return
  }
  try {
    chart = createGraph3d(
      element,
      cloud.points,
      buildOptions(cloud, plotWidth(), chartHeight.value, isDark.value, cameraFromProps()),
    )
    // After the constructor, which has already drawn once at the library's own 10, and so paid for
    // with a second redraw — the one place in this component that deliberately draws twice. There
    // is no earlier seam: the constructor takes its options and paints, and `margin` is not one of
    // the options it will take. It is a build, not a slider, so it costs a measurement's last
    // ~300 ms rather than a frame of a drag.
    chart.margin = LEGEND_MARGIN_PX
    chart.redraw()
    rememberApplied()
    failure.value = null
  } catch (e) {
    failure.value = e instanceof Error ? e.message : 'Could not draw the 3D surface.'
    chart = null
  }
  drawn.value = chart !== null
}

function teardown() {
  if (redrawFrame !== 0) {
    cancelAnimationFrame(redrawFrame)
    redrawFrame = 0
  }
  chart = null
  cloud = null
  drawn.value = false
  // All the teardown there is: the library has no `destroy()` and its own source says its listeners
  // are never cleaned up, so emptying the host is what takes its DOM and their bindings with it.
  destroyGraph3d(host.value)
}

onMounted(() => {
  build()
  if (!container.value) return
  observer = new ResizeObserver(() => resize())
  observer.observe(container.value)
})

onBeforeUnmount(() => {
  disposed = true
  observer?.disconnect()
  observer = null
  teardown()
})

/**
 * <p><b>Each view control takes the cheapest route the library has for it, and nothing ever
 * redraws twice.</b> Every entry point there ends in a synchronous redraw of its own — a quarter
 * to a third of a second at a cumulative spectral decay's 24 000 vertices (see
 * <c>Graph3dInstance</c> for the figures) — so a handler that calls one of them and then
 * <c>redraw()</c> pays twice. A one-degree nudge of Rotation cost two seconds that way before it
 * was measured.</p>
 *
 * <p>The camera is the cheapest of the three: it moves and redraws without touching the grid.
 * The two centres and the box's height are plain fields the renderer reads on its next pass.
 * Only the palette, the theme and the surface itself go through <c>setOptions</c>, which rebuilds
 * the grid on the way — and none of those three is a slider.</p>
 */
type Pending = { camera: boolean; layout: boolean; options: boolean }
const pending: Pending = { camera: false, layout: false, options: false }

function schedule(what: keyof Pending) {
  pending[what] = true
  if (redrawFrame !== 0) return
  // Coalesced onto one animation frame, so a drag that fires its watcher once a step collapses
  // into one redraw per frame rather than queueing a depth sort per step.
  redrawFrame = requestAnimationFrame(() => {
    redrawFrame = 0
    if (disposed || !chart) return
    const work = { ...pending }
    pending.camera = pending.layout = pending.options = false
    try {
      // Most general first, so a pass that needs the options does not then redraw again for the
      // camera: setOptions already carries the camera it is given.
      if (work.options) {
        chart.setOptions(buildOptions(cloud, plotWidth(), chartHeight.value, isDark.value, cameraFromProps()))
      } else {
        // Reset view writes all six fields at once, so both of these pend together — and each
        // ends in a redraw of its own unless the picture is left to exactly one of them.
        let redrawn = false
        if (work.layout) {
          const centre = decayCenterPercent(props.moveX, props.moveY)
          chart.xCenter = centre.xCenter
          chart.yCenter = centre.yCenter
          redrawn = applyVerticalRatio(chart, decayVerticalRatio(props.explodeZ))
        }
        if (work.camera) chart.setCameraPosition(cameraFromProps())
        else if (!redrawn) chart.redraw()
      }
    } catch {
      // A redraw that throws leaves the last picture, which is better than an empty chart.
    }
  })
}

function resize() {
  if (!chart) return
  chart.setSize(`${plotWidth()}px`, `${chartHeight.value}px`)
}

watch(chartHeight, () => resize())

/**
 * The surface itself.
 *
 * <p><b>A new pass of the same measurement costs one rebuild, not two.</b> The options carry the
 * axis labels and the height range, and <c>setOptions</c> rebuilds the grid itself — on the table
 * it already has — before this hands it the new one. That is two rebuilds for one change, and the
 * way to avoid paying it on every pass of a continuous run is to notice that nothing the options
 * describe has moved.</p>
 *
 * <p><b>The slice axis is part of that test and is the one easy to leave out.</b> It is a
 * property of the <em>surface</em> rather than of a prop: switching the view from the cumulative
 * spectral decay to the burst decay hands over a different surface at the same mode and the same
 * range, and its depth is counted in periods where the other's was in milliseconds. Left out, the
 * burst decay draws with a time axis labelled "Time (ms)" over a scale that is nothing of the
 * kind — which is exactly what it did when this comparison was first written.</p>
 */
let appliedOptionsKey: string | null = null

watch(
  () => [props.surface, props.mode, props.rangeDb],
  () => refreshSurface(),
  { deep: false },
)

/**
 * <p><b>The chart exists only while there is something to draw, and an empty one is not merely
 * pointless but impossible.</b> <c>DataGroup.initializeData</c> returns on its own first lines
 * for a zero-row table — <c>if (data.length == 0) return</c> — which leaves every range unset;
 * <c>_initializeRanges</c> then copies those <c>undefined</c>s onto the instance and ends by
 * calling <c>_setScale</c>, whose first statement is <c>1 / this.xRange.range()</c>. So handing
 * the library an empty array throws <em>"Cannot read properties of undefined (reading
 * 'range')"</em> from inside the constructor, and <c>build</c>'s catch printed that as a red
 * banner across the screen that should have read "Press Single." — reported 2026-10-30, and the
 * state every impulse measurement opens in.</p>
 *
 * <p><b><c>setData([])</c> is the same trap from the other side</b>, which is why a surface going
 * away drops the chart rather than emptying it: that early return also leaves <c>dataPoints</c>
 * undefined for the redraw which follows it.</p>
 */
function refreshSurface() {
  if (!chart) {
    build()
    return
  }
  cloud = surfaceCloud()
  if (!cloud) {
    teardown()
    failure.value = null
    return
  }
  chart.setData(cloud.points)
  if (decayOptionsKey(props.surface, props.mode, props.rangeDb) !== appliedOptionsKey) {
    chart.setOptions(buildOptions(cloud, plotWidth(), chartHeight.value, isDark.value, cameraFromProps()))
    rememberApplied()
  }
}

/** What the options last carried, so the comparison above has one definition rather than two. */
function rememberApplied() {
  appliedOptionsKey = decayOptionsKey(props.surface, props.mode, props.rangeDb)
}

/** The camera: the three sliders that move it, and the cheapest path there is. */
watch(
  () => [props.rotationDeg, props.depthPercent, props.magnification],
  () => schedule('camera'),
)

/** Where the box sits and how tall it is drawn — fields the renderer reads on its next pass. */
watch(
  () => [props.moveX, props.moveY, props.explodeZ],
  () => schedule('layout'),
)

/**
 * The palette, the page's own theme and the mesh. None of the three is a slider, so the rebuild
 * `setOptions` costs is affordable — and all three are options rather than fields, the mesh
 * because it is `showSurfaceGrid` and the ink it strokes with.
 *
 * <p>Every prop `buildOptions` reads has to be in this list. One that is not reaches the chart
 * only when something else happens to rebuild the options, which is to say it silently does
 * nothing — which is what the mesh checkbox did until it was ticked and measured.</p>
 */
watch(
  () => [props.palette, isDark.value, props.meshLines],
  () => schedule('options'),
)

/**
 * The chart as a PNG of the requested size, light whatever the theme — built from a second chart in
 * a detached host rather than read off the one on screen, which is how the Canvas 2D chart does it
 * and what lets a 700x400 dark chart yield a 1600x900 light picture.
 *
 * <p>Simpler than the WebGPU engine's version in three ways that are worth naming, because each was
 * a step there: the redraw is synchronous, so no paint turn has to be waited for; each chart owns
 * its own canvas, so the live one is not disturbed; and the live camera is readable through
 * <c>getCameraPosition()</c>, so a dragged view exports as seen without any property snapshotting.</p>
 */
async function exportPng({ width, height }: ChartExportOptions): Promise<Blob> {
  const plotHeight = height - CHART_EXPORT_HEADER_PX
  const surface = props.surface

  const offscreen = document.createElement('div')
  offscreen.style.cssText = `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(offscreen)

  let copy: Graph3dInstance | null = null
  try {
    // With no surface this exports the well and the title, which is what the flat projection
    // gives for the same state — and the library cannot be handed an empty one at all, so the
    // copy is built only when there is something in it (see `refreshSurface`).
    const points = cloud ?? surfaceCloud()
    if (points) {
      const camera = chart ? chart.getCameraPosition() : cameraFromProps()
      copy = createGraph3d(
        offscreen,
        points.points,
        buildOptions(points, width, plotHeight, false, camera),
      )
      // The export has no expand button over it, and the key is put where the screen's is anyway:
      // a picture of the chart that moves one of its parts is not a picture of the chart.
      copy.margin = LEGEND_MARGIN_PX
      copy.redraw()
    }

    const plot = document.createElement('canvas')
    plot.width = width
    plot.height = plotHeight
    const ctx = plot.getContext('2d')
    if (!ctx) throw new Error('Could not get a 2D context for the chart export.')
    // <p><b>The well is painted here, because the library's canvas is transparent.</b> It clears
    // rather than fills and puts its background on the frame element's CSS instead, so a copy of
    // the canvas alone carries the surface and nothing behind it. `composeChartPng` happens to
    // fill white first and would hide it, but an export that is right only because something
    // downstream fills in for it is one that breaks the day that changes.</p>
    ctx.fillStyle = theme(false).back
    ctx.fillRect(0, 0, width, plotHeight)
    if (copy) ctx.drawImage(copy.frame.canvas, 0, 0, width, plotHeight)

    return await composeChartPng({
      width,
      height,
      plotCanvas: plot,
      headerHeight: CHART_EXPORT_HEADER_PX,
      title: decayExportTitle(surface, props.mode, props.rangeDb),
    })
  } finally {
    destroyGraph3d(offscreen)
    offscreen.remove()
  }
}

defineExpose({ exportPng })
</script>

<template>
  <div ref="container" class="relative w-full">
    <!-- <p><b>Left, where every other chart puts it right — the colour key is in that corner and
         the library will not move it.</b> Its legend is drawn at `clientWidth - margin`, and the
         same `margin` is also the key's distance from the top, so pushing it left pushes it off
         the bottom. The button is the thing that can move, and the left of the plot is empty.</p> -->
    <ExpandToggle class="absolute right-2 top-2 z-10" :target="container" />

    <!-- <p><b>The host is the empty well when there is nothing to draw.</b> The library cannot be
         built with an empty surface at all (see `refreshSurface`), so with nothing measured there
         is no canvas — which left the message below floating in a strip where the chart should be,
         and made the whole column jump the moment a measurement landed. The same reasoning as the
         three FFT charts' `drawEmpty`, except that here the placeholder cannot be a real plot and
         so is a plain box of the height the chart will take, in the chart's own well colour rather
         than `--surface-plot`, so the empty box and the drawn one are one tone in either theme.

         <b>It has to be this element and not a sibling of it</b>, which is the whole of why the
         empty 3D chart came out half the sonogram's height. `useResultChartHeight` measures the
         chart's furniture as root minus *this* box, so a placeholder standing beside it is counted
         as furniture: the fit then has a whole chart's height already spoken for, and what is left
         is clamped to `MIN_RESULT_CHART_HEIGHT_PX` — 360 px against the sonogram's 680 on the same
         screen, and the chart built into it kept that height afterwards. On the host the sum is
         the one the flat chart already gets, its canvas being its own plot box.</p> -->
    <div
      ref="host"
      class="block w-full"
      :style="drawn ? undefined : { height: `${chartHeight}px`, backgroundColor: wellColour }"
    ></div>

    <p
      v-if="computing"
      class="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 rounded border border-neutral-300 bg-white/95 px-4 py-2 text-center text-xs text-neutral-700 shadow dark:border-neutral-700 dark:bg-neutral-900/95 dark:text-neutral-300"
    >
      Analysing…
    </p>
    <p
      v-else-if="error || failure"
      class="absolute inset-x-16 top-1/2 -translate-y-1/2 rounded border border-red-300 bg-white/95 px-3 py-2 text-center text-xs text-red-700 dark:border-red-800 dark:bg-neutral-900/95 dark:text-red-400"
    >
      {{ error || failure }}
    </p>
    <p
      v-else-if="!surface"
      class="absolute inset-x-0 top-1/2 -translate-y-1/2 text-center text-xs text-neutral-500 dark:text-neutral-400"
    >
      {{ emptyMessage }}
    </p>
  </div>
</template>
