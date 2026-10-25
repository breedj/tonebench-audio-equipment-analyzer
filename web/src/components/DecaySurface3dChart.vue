<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { DecayChartMode, DecayPalette, DecaySurface } from '../api/client'
import { formatHz } from '../measurements/decay'
import {
  decayCameraZoom,
  decayExportTitle,
  decayGridAspect,
  decayMesh,
  decaySubsetColors,
  peColor,
} from '../measurements/decay3d'
import { PE_CONTROL_DEFAULTS, peRuntime } from '../charts/peModule'
import { CHART_EXPORT_HEADER_PX, composeChartPng, nextPaint, type ChartExportOptions } from '../charts/exportChart'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { useAppTheme } from '../composables/useAppTheme'
import ExpandToggle from './ExpandToggle.vue'

/**
 * <p>The decay surface as a lit, rotatable, zoomable 3D mesh, drawn by the ProEssentials
 * WebAssembly engine on WebGPU.</p>
 *
 * <p>It takes the same props as <c>DecaySurfaceChart</c> and exposes the same <c>exportPng</c>, so
 * the two are interchangeable at the call site; <c>App.vue</c> picks between them on
 * <c>decaySurfaceGpuAvailable()</c>. It is never the only chart — see <c>peCapability.ts</c> for
 * why a bench without WebGPU must be given the Canvas 2D one rather than this.</p>
 *
 * <p>The arithmetic is in <c>measurements/decay3d.ts</c>, which reads the surface through the same
 * <c>orientSurface</c> and <c>levelAt</c> the Canvas 2D chart uses, so the two cannot disagree
 * about which way round a waterfall goes or where a ragged slice begins.</p>
 */
const props = defineProps<{
  surface: DecaySurface | null
  mode: DecayChartMode
  palette: DecayPalette
  coloredCurves: boolean
  filled: boolean
  rotationDeg: number
  depthPercent: number
  rangeDb: number
  thickLines: boolean
  computing: boolean
  error: string | null
  emptyMessage: string
}>()

/**
 * <p><b>This chart does not emit its view back, and the Canvas 2D one does.</b> That is not an
 * oversight: the two quantities are not the same. The panel's Rotation is a lean of an oblique
 * projection, bounded to ±60°, while the engine's azimuth is a camera that turns through a whole
 * circle. Writing a dragged azimuth back through the panel's field would clamp it, and the next
 * prop watcher would write the clamped value into the engine — so the surface would spring back
 * part way through every turn.</p>
 *
 * <p>So the fields set the view, and from the first drag the engine's camera owns it. What that
 * costs is one thing and it is paid for in {@link exportPng}: an export reads the <em>live</em>
 * camera rather than rebuilding it from the props, so a screenshot is still the picture on
 * screen.</p>
 */

const container = ref<HTMLDivElement | null>(null)
const host = ref<HTMLDivElement | null>(null)
// First in setup, so the control is built at the height it will keep rather than resized a frame
// later — see the composable's own remarks.
const chartHeight = useResultChartHeight(container, host)

const { isDark } = useAppTheme()

const hover = ref<string | null>(null)
const failure = ref<string | null>(null)

type PeControlInstance = {
  handle: number
  canvas: HTMLCanvasElement
  el: HTMLElement
  attach<T>(api: unknown): T
  setSize(width: number, height: number): number
  invalidate(): number
  destroy(): void
}

/* eslint-disable @typescript-eslint/no-explicit-any */
type PeChart = any

let control: PeControlInstance | null = null
let chart: PeChart = null
let enums: PeChart = null
/** The wasm-heap blocks the engine plots straight out of. Re-allocated only when the shape changes. */
let blocks: { x: PeChart; y: PeChart; z: PeChart; rows: number; cols: number } | null = null
let disposed = false
let observer: ResizeObserver | null = null

/** The last view we wrote, so a prop watcher firing on an unchanged value does not fight a drag. */
let appliedRotation = Number.NaN
let appliedDepth = Number.NaN

const MIN_WIDTH = 160

function plotWidth(): number {
  return Math.max(MIN_WIDTH, Math.round(container.value?.clientWidth ?? MIN_WIDTH))
}

onMounted(async () => {
  let runtime
  try {
    runtime = await peRuntime()
  } catch (e) {
    failure.value = `The 3D chart engine could not be loaded: ${e instanceof Error ? e.message : String(e)}`
    return
  }
  // The await above is long enough that the user can leave the view inside it.
  if (disposed || !host.value) return

  enums = runtime.Enums
  try {
    control = new (window as unknown as { PeControl: new (el: HTMLElement, o: unknown) => PeControlInstance }).PeControl(
      host.value,
      { ...PE_CONTROL_DEFAULTS, module: runtime.module, width: plotWidth(), height: chartHeight.value },
    )
    if (!control.handle) throw new Error('the engine refused to create a 3D control')
    chart = control.attach(runtime.attachApi)
    configure()
    applyData()
    control.el.addEventListener('pointermove', onPointerMove)
    control.el.addEventListener('pointerleave', clearHover)
  } catch (e) {
    failure.value = `The 3D chart could not be started: ${e instanceof Error ? e.message : String(e)}`
    return
  }

  observer = new ResizeObserver(() => resize())
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  disposed = true
  observer?.disconnect()
  observer = null
  control?.el.removeEventListener('pointermove', onPointerMove)
  control?.el.removeEventListener('pointerleave', clearHover)
  // Unconditionally: this is the one that leaks a GPU context, and the impulse screen's views are
  // switched constantly.
  control?.destroy()
  control = null
  chart = null
  blocks = null
})

/** Everything that is not the data. Written once; only the palette and the camera change after. */
function configure() {
  const C = chart
  const E = enums
  C.PeSpecial.AutoImageReset = false

  // What makes it a lit surface rather than the engine's default plot.
  C.PePlot.PolyMode = E.PolyMode.SurfacePolygons
  C.PePlot.Method = E.ThreeDGraphPlottingMethod.Four
  C.PeColor.DxTransparencyMode = 0
  C.PePlot.Allow.WireFrame = false
  C.PePlot.Option.ShowContour = E.ShowContour.None

  // The camera. `DxFitControlShape` overrides the zoom outright and framed the surface badly at
  // every zoom tried, so the framing is set explicitly instead.
  C.PePlot.Option.DxFitControlShape = false
  C.PePlot.Option.DxViewportX = 0
  // Lifts the box off the bottom of the plot, where the engine otherwise parks it with the whole
  // top of the panel empty. Measured on a 601x586 plot: ink within 3 px of the bottom edge falls
  // from 190 px to 26 at this setting, with the top and right edges clear either way. It is not a
  // plain translation — the figure is a measured optimum, and both larger and smaller are worse.
  C.PePlot.Option.DxViewportY = 8
  C.PePlot.Option.DxFOV = 1
  // Well below any zoom we set, so the wheel still has somewhere to go in both directions.
  C.PePlot.Option.DxZoomMin = -900
  C.PePlot.Option.DxZoomMax = -3
  C.PePlot.Option.DxZoom = decayCameraZoom(plotWidth(), chartHeight.value)

  C.PeUserInterface.RotationDetail = E.RotationDetail.WireFrame
  C.PeUserInterface.Scrollbar.HorzScrollBar = false
  C.PeUserInterface.Scrollbar.VertScrollBar = false
  C.PeUserInterface.Scrollbar.MouseDraggingX = true
  C.PeUserInterface.Scrollbar.MouseDraggingY = true
  C.PeUserInterface.Scrollbar.MouseWheelFunction = E.MouseWheelFunction.HorizontalVerticalZoom
  C.PeUserInterface.Scrollbar.MouseWheelZoomEvents = true
  C.PeUserInterface.Scrollbar.MouseWheelZoomSmoothness = 2
  C.PeUserInterface.Scrollbar.MouseWheelZoomFactor = 25.0

  C.PeConfigure.BorderTypes = E.TABorder.NoBorder
  C.PeColor.BitmapGradientMode = false
  C.PeColor.GraphBmpStyle = E.BitmapStyle.NoBmp
  C.PeLegend.Show = false
  C.PeString.MainTitle = ''
  C.PeString.SubTitle = ''

  C.PeGrid.Configure.AutoPadBeyondZeroX = true
  C.PeGrid.Configure.AutoPadBeyondZeroY = true
  C.PeGrid.Configure.AutoPadBeyondZeroZ = true
  C.PeGrid.Option.ShowXAxis = E.ShowAxis.All
  C.PeGrid.Option.ShowYAxis = E.ShowAxis.All
  C.PeGrid.Option.ShowZAxis = E.ShowAxis.All

  // The hit test the readout needs. Without one of these the engine never builds its octree and
  // every GetHotSpot answers type -1 — which reads as "the pointer is never over the surface".
  // `HighlightColor` is also what marks the point under the pointer, the engine's own answer to
  // the Canvas 2D chart's ring; `PromptStyle = None` keeps the engine from drawing a tooltip of
  // its own beside our readout, which says the same thing in the app's own wording.
  C.PeUserInterface.HotSpot.Data = true
  C.PeUserInterface.Cursor.PromptTracking = true
  C.PeUserInterface.Cursor.HighlightColor = peColor(248, 250, 252)
  C.PeUserInterface.Cursor.PromptStyle = E.CursorPromptStyle.None

  C.PeData.DuplicateDataX = E.DuplicateData.PointIncrement
  C.PeData.DuplicateDataZ = E.DuplicateData.SubsetIncrement
  C.PeData.ComputeShader = true
  C.PeConfigure.RenderEngine = E.RenderEngine.Direct3D

  C.PeFunction.SetLight(0, -2.0, -7.0, -7.0)

  applyTheme()
  applyCamera()
}

function applyTheme() {
  if (!chart) return
  const C = chart
  const dark = isDark.value
  // The chart paints its own well rather than taking --surface-plot, for the reason the Canvas 2D
  // chart does: an export forces the light theme while the app may be dark.
  const ink = dark ? peColor(212, 212, 212) : peColor(64, 64, 64)
  const back = dark ? peColor(0x15, 0x18, 0x1d) : peColor(255, 255, 255)
  C.PeColor.GraphBackground = back
  C.PeColor.Desk = back
  C.PeColor.GraphForeground = ink
  C.PeColor.XAxis = ink
  C.PeColor.YAxis = ink
  C.PeColor.ZAxis = ink
  C.PeColor.Text = ink

  const colors = decaySubsetColors(props.palette, dark)
  C.PeColor.SubsetColors.clear()
  for (let i = 0; i < colors.length; i++) C.PeColor.SubsetColors[i] = colors[i]
}

function applyCamera() {
  if (!chart) return
  // The panel's lean maps onto the engine's azimuth, and its depth onto the elevation. Both of the
  // panel's ranges are narrower than the engine's, so nothing has to be clamped here.
  //
  // <p><b>360 rather than 180, which is which side of the box the camera sits on.</b> At 180 the
  // depth axis ran the wrong way: time zero came out at the front and the stack receded into the
  // past, where the Canvas 2D chart puts time zero at the back — <c>sliceOrigin(layout, 0)</c> is
  // the top-left corner and increasing depth comes forward. Reported 2026-10-25 on both surfaces,
  // the burst decay's Periods axis included, which is the same axis by another name.</p>
  //
  // <p>Note what this costs, because it is not free and no azimuth avoids it: the two floor axes
  // keep their handedness however the camera moves, so turning the box to put time the right way
  // round also mirrors the frequency axis. Frequency now increases right to left, where the Canvas
  // 2D chart has it left to right. Reversing the depth <em>data</em> instead would keep both, and
  // needs the engine's <c>InvertedZAxis</c> with negated values — which cannot work in the
  // by-period view, whose depth axis is a logarithmic frequency.</p>
  appliedRotation = props.rotationDeg
  appliedDepth = props.depthPercent
  chart.PeUserInterface.Scrollbar.DegreeOfRotation = Math.round(360 - props.rotationDeg)
  chart.PeUserInterface.Scrollbar.ViewingHeight = Math.round(props.depthPercent * 0.6)
}

/** The data, and the only place the mesh reaches the engine. */
function applyData() {
  if (!chart || !control) return
  const C = chart
  const surface = props.surface
  if (!surface || surface.slices.length === 0) {
    // Release the heap views before dropping our reference to them, or the engine keeps plotting
    // the surface that has just been cleared. `useDataAtLocation()` with no argument is the
    // documented release.
    if (blocks) {
      C.PeData.X.useDataAtLocation()
      C.PeData.Y.useDataAtLocation()
      C.PeData.Z.useDataAtLocation()
    }
    blocks = null
    C.PeData.Subsets = 1
    C.PeData.Points = 1
    C.PeFunction.Force3dxVerticeRebuild = true
    C.PeFunction.ReinitializeResetImage()
    return
  }

  const mesh = decayMesh(surface, props.mode, props.rangeDb)

  // Counts before the data, always: every write is bounds-checked against Subsets x Points, and a
  // write that arrives first is rejected in silence while the chart draws its own default surface.
  C.PeData.Subsets = mesh.rows
  C.PeData.Points = mesh.cols
  C.PeData.ScaleForYData = 0

  // Re-allocate only when the shape changes — these are views on the wasm heap the engine plots
  // straight out of, and churning them every pass is not what the zero-copy path is for.
  if (!blocks || blocks.rows !== mesh.rows || blocks.cols !== mesh.cols) {
    blocks = {
      x: C.PeData.X.allocate(mesh.cols),
      y: C.PeData.Y.allocate(mesh.rows * mesh.cols),
      z: C.PeData.Z.allocate(mesh.rows),
      rows: mesh.rows,
      cols: mesh.cols,
    }
  }
  blocks.x.set(mesh.x)
  blocks.y.set(mesh.y)
  blocks.z.set(mesh.z)
  C.PeData.X.useDataAtLocation(blocks.x, mesh.cols)
  C.PeData.Y.useDataAtLocation(blocks.y, mesh.rows * mesh.cols)
  C.PeData.Z.useDataAtLocation(blocks.z, mesh.rows)

  // The height axis is pinned to the range the panel asks for rather than auto-scaled, so the
  // colour ramp and the dB ticks mean the same thing here as on the Canvas 2D chart.
  C.PeGrid.Configure.ManualScaleControlY = enums.ManualScaleControl.MinMax
  C.PeGrid.Configure.ManualMinY = -props.rangeDb
  C.PeGrid.Configure.ManualMaxY = 0

  const aspect = decayGridAspect()
  C.PeGrid.Option.GridAspectX = aspect.x
  C.PeGrid.Option.GridAspectY = aspect.y
  C.PeGrid.Option.GridAspectZ = aspect.z

  // Whichever axis carries the frequency is scaled logarithmically, which is what puts true hertz
  // on its ticks. The height axis is in dB and the time axis starts at zero, so neither can be.
  C.PeGrid.Configure.XAxisScaleControl =
    mesh.acrossIsFrequency ? enums.ScaleControl.Log : enums.ScaleControl.Normal
  C.PeGrid.Configure.ZAxisScaleControl =
    mesh.depthIsFrequency ? enums.ScaleControl.Log : enums.ScaleControl.Normal
  C.PeGrid.Configure.YAxisScaleControl = enums.ScaleControl.Normal
  // Plain hertz on the log axis — 1000, not 10³. The rest of the app writes 1k through formatHz,
  // but the engine owns this axis's formatting and an unambiguous number beats a wrong one.
  C.PeGrid.Configure.LogScaleExpLabels = false

  // Word for word what the Canvas 2D chart writes, so the two charts of one measurement cannot
  // disagree about what an axis is called.
  const timeLabel = `Time (${surface.sliceAxis === 'Periods' ? 'periods' : 'ms'})`
  C.PeString.XAxisLabel = mesh.acrossIsFrequency ? 'Frequency (Hz)' : timeLabel
  C.PeString.ZAxisLabel = mesh.depthIsFrequency ? 'Frequency (Hz)' : timeLabel
  C.PeString.YAxisLabel = 'dB'

  C.PeFunction.Force3dxVerticeRebuild = true
  C.PeFunction.ReinitializeResetImage()
}

function resize() {
  if (!control || !chart) return
  const width = plotWidth()
  const height = chartHeight.value
  // The zoom follows the plot's shape, so a resize re-frames rather than only re-rasterising.
  chart.PePlot.Option.DxZoom = decayCameraZoom(width, height)
  control.setSize(width, height)
}

watch(chartHeight, () => resize())
watch(() => [props.surface, props.mode, props.rangeDb], () => applyData(), { deep: false })
watch(() => [props.palette, isDark.value], () => {
  applyTheme()
  chart?.PeFunction.ReinitializeResetImage()
})
watch(() => [props.rotationDeg, props.depthPercent], () => {
  if (props.rotationDeg === appliedRotation && props.depthPercent === appliedDepth) return
  applyCamera()
  chart?.PeFunction.ReinitializeResetImage()
})

/**
 * The readout, from the engine's own hit test — the Canvas 2D chart's picker replays its painter's
 * algorithm, which only works because that projection is oblique and the paint order is known.
 */
function onPointerMove() {
  if (!chart || !props.surface) return
  try {
    const point = chart.PeUserInterface.Cursor.LastMouseMove
    chart.PeFunction.GetHotSpot(point.x | 0, point.y | 0)
    const spot = chart.PeFunction.GetHotSpotData()
    if (spot.type !== enums.HotSpotType.DataPoint) { hover.value = null; return }

    const surface = props.surface
    const byPeriod = props.mode === 'WaterfallPeriods'
    // data1 is the subset (a curve), data2 the point along it. Which of those names a frequency
    // depends on the view; the readout does not — it always says frequency, then time, then level.
    const sliceIndex = byPeriod ? spot.data2 : spot.data1
    const column = byPeriod ? spot.data1 : spot.data2
    const slice = surface.slices[sliceIndex]
    const freqHz = surface.freqHz[column]
    if (!slice || freqHz === undefined) { hover.value = null; return }

    const level = blocks
      ? blocks.y.array[(byPeriod ? column * blocks.cols + sliceIndex : sliceIndex * blocks.cols + column)]
      : Number.NaN
    hover.value = Number.isNaN(level)
      ? `${formatHz(freqHz)} Hz — not measured this deep`
      : `${formatHz(freqHz)} Hz, ${slice.value.toFixed(2)} ${timeUnit()} → ${level.toFixed(1)} dB`
  } catch {
    hover.value = null
  }
}

function timeUnit(): string {
  return props.surface?.sliceAxis === 'Periods' ? 'periods' : 'ms'
}

function clearHover() {
  hover.value = null
}

/**
 * The chart as a PNG of the requested size, light whatever the theme — built from a second control
 * in a detached host rather than read off the one on screen, which is how the Canvas 2D chart does
 * it and what lets a 700x400 dark chart yield a 1600x900 light picture.
 *
 * <p>A second control on the same module is safe despite the single shared WebGPU canvas: measured
 * 2026-10-25, an offscreen control at 1600x860 drew its surface and the live one still had its own
 * after an <c>invalidate()</c>. The readback has to come a turn after the draw call, because the
 * GPU layer composites into the control's 2D canvas on the turn after.</p>
 */
async function exportPng({ width, height }: ChartExportOptions): Promise<Blob> {
  const runtime = await peRuntime()
  const plotHeight = height - CHART_EXPORT_HEADER_PX
  const surface = props.surface

  const offscreen = document.createElement('div')
  offscreen.style.cssText =
    `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(offscreen)

  const live = chart
    ? {
        azimuth: chart.PeUserInterface.Scrollbar.DegreeOfRotation,
        elevation: chart.PeUserInterface.Scrollbar.ViewingHeight,
      }
    : null

  let copy: PeControlInstance | null = null
  try {
    copy = new (window as unknown as { PeControl: new (el: HTMLElement, o: unknown) => PeControlInstance }).PeControl(
      offscreen,
      { ...PE_CONTROL_DEFAULTS, module: runtime.module, width, height: plotHeight },
    )
    const saved = { chart, enums }
    try {
      // configure()/applyData() write through the module-level `chart`, so point them at the copy
      // for the duration rather than keeping a second copy of every property write.
      chart = copy.attach(runtime.attachApi)
      enums = runtime.Enums
      configure()
      chart.PePlot.Option.DxZoom = decayCameraZoom(width, plotHeight)
      // The live camera, not the props: once the surface has been dragged the props no longer
      // describe what is on screen, and a screenshot has to be the picture the user is looking at.
      if (live) {
        chart.PeUserInterface.Scrollbar.DegreeOfRotation = live.azimuth
        chart.PeUserInterface.Scrollbar.ViewingHeight = live.elevation
      }
      forceLightTheme()
      applyDataForExport()
    } finally {
      chart = saved.chart
      enums = saved.enums
    }

    // One turn for the GPU layer to composite into the control's own 2D canvas.
    await nextPaint()

    const plot = document.createElement('canvas')
    plot.width = width
    plot.height = plotHeight
    const ctx = plot.getContext('2d')
    if (!ctx) throw new Error('Could not get a 2D context for the chart export.')
    ctx.drawImage(copy.canvas, 0, 0, width, plotHeight)

    return await composeChartPng({
      width,
      height,
      plotCanvas: plot,
      headerHeight: CHART_EXPORT_HEADER_PX,
      title: decayExportTitle(surface, props.mode, props.rangeDb),
    })
  } finally {
    copy?.destroy()
    offscreen.remove()
    // The GPU canvas is shared, so the live control has to be told to repaint itself.
    control?.invalidate()
  }
}

/** The export copy's own theme — light, whatever the app is showing. */
function forceLightTheme() {
  const C = chart
  const ink = peColor(64, 64, 64)
  const back = peColor(255, 255, 255)
  C.PeColor.GraphBackground = back
  C.PeColor.Desk = back
  C.PeColor.GraphForeground = ink
  C.PeColor.XAxis = ink
  C.PeColor.YAxis = ink
  C.PeColor.ZAxis = ink
  C.PeColor.Text = ink
  const colors = decaySubsetColors(props.palette, false)
  C.PeColor.SubsetColors.clear()
  for (let i = 0; i < colors.length; i++) C.PeColor.SubsetColors[i] = colors[i]
}

/** applyData against the export copy, which has blocks of its own. */
function applyDataForExport() {
  const saved = blocks
  blocks = null
  try {
    applyData()
  } finally {
    blocks = saved
  }
}

defineExpose({ exportPng })
</script>

<template>
  <div ref="container" class="relative w-full">
    <ExpandToggle class="absolute right-2 top-2 z-10" :target="container" />
    <div ref="host" class="block w-full"></div>

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

    <p v-if="hover" class="absolute left-2 top-1 font-mono text-[11px] text-neutral-500 dark:text-neutral-400">
      {{ hover }}
    </p>
  </div>
</template>
