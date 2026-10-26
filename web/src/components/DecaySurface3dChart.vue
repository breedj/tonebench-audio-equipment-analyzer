<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { DecayChartMode, DecayPalette, DecaySurface } from '../api/client'
import { formatHz } from '../measurements/decay'
import {
  decayCameraZoom,
  decayLightVector,
  decayMagnifiedZoom,
  DECAY_LIGHT_STRENGTH,
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
  /**
   * The five view controls of the panel's own collapsed section — where the surface sits, how
   * large it is drawn, and where the light comes from. See `measurements/decay3d.ts` for their
   * ranges; all five are stored with the measurement, which is what lets an awkward view be kept.
   */
  moveX: number
  moveY: number
  magnification: number
  /** How long each edge of the box is drawn; see DecayAxisLengths. */
  axisLengthX: number
  axisLengthY: number
  axisLengthZ: number
  lightAzimuthDeg: number
  lightElevationDeg: number
  rangeDb: number
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
/**
 * How many levels the colour key is labelled at. Six reads as a scale and fits the shortest chart
 * this screen draws; the ramp itself is 256 colours and is unaffected.
 */
const DECAY_KEY_STEPS = 6

const chartHeight = useResultChartHeight(container, host, { pageHeightPx: () => props.pageHeightPx })

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
  /** Full re-layout, then paint. Needed where a property is only read while the scene is laid out. */
  render(): number
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

type PeRuntime = Awaited<ReturnType<typeof peRuntime>>
let runtime: PeRuntime | null = null

/**
 * Construct the control and fill it. Separate from {@link onMounted} because the box's own shape
 * is read while the scene is laid out and cannot be changed afterwards — see the axis-length
 * watcher below — so the three length sliders rebuild rather than refresh.
 */
function build(): boolean {
  if (!runtime || !host.value) return false
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
    return true
  } catch (e) {
    failure.value = `The 3D chart could not be started: ${e instanceof Error ? e.message : String(e)}`
    return false
  }
}

/** Drop the control, listeners and all. The destroy is what releases the GPU context. */
function teardown() {
  control?.el.removeEventListener('pointermove', onPointerMove)
  control?.el.removeEventListener('pointerleave', clearHover)
  // Unconditionally: this is the one that leaks a GPU context, and the impulse screen's views are
  // switched constantly.
  control?.destroy()
  control = null
  chart = null
  blocks = null
}

onMounted(async () => {
  try {
    runtime = await peRuntime()
  } catch (e) {
    failure.value = `The 3D chart engine could not be loaded: ${e instanceof Error ? e.message : String(e)}`
    return
  }
  // The await above is long enough that the user can leave the view inside it.
  if (disposed || !host.value) return
  if (!build()) return

  observer = new ResizeObserver(() => resize())
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  disposed = true
  observer?.disconnect()
  observer = null
  teardown()
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
  // The panel's Move controls, in the engine's own viewport units. The resting lift is still done
  // with the margins below: `DxViewportY` is documented as a translation of up to three times the
  // size of the axes and measures as almost nothing here — the whole legal range of -3..+3 moves
  // the surface seven pixels on a 320-pixel plot — so it is the fine adjustment and the margins
  // are the coarse one. (An earlier 8 here was outside that range entirely, which is what made it
  // read as a knob with a measured optimum rather than one that barely works.)
  C.PePlot.Option.DxViewportX = props.moveX
  C.PePlot.Option.DxViewportY = props.moveY
  // Shift and drag translates the surface, as the vendor's own demo does it. The engine
  // implements the gesture; this is only its speed, and without it the gesture does nothing.
  C.PePlot.Option.DxViewportPanFactor = 10
  // Takes a margin off the top and gives it to the bottom, which moves the whole plot box up
  // inside the panel — the engine otherwise parks it on the floor with the top left empty.
  // Measured on a 547x320 plot: the gap above the content falls from 58 px to 47 and the gap
  // below rises from 4 to 15. Going further clips: at -150/150 the bottom gap is already 0.
  C.PeConfigure.ImageAdjustTop = -100
  C.PeConfigure.ImageAdjustBottom = 100
  C.PeConfigure.ImageAdjustRight = -200
  C.PePlot.Option.DxFOV = 1
  // Well below any zoom we set, so the wheel still has somewhere to go in both directions.
  C.PePlot.Option.DxZoomMin = -90
  C.PePlot.Option.DxZoomMax = -3
  C.PePlot.Option.DxZoom = decayMagnifiedZoom(decayCameraZoom(plotWidth(), chartHeight.value), props.magnification)

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
  // The colour key, as the vendor's own demo draws it and asked for 2026-10-26. A contour-style
  // legend is a ramp with levels against it, which is what the Canvas 2D chart welds to its own
  // level axis — it cannot be welded here, since the engine draws that axis, so this is the same
  // reading in the shape the engine has for it. Left or right only, which the property's own
  // documentation says; right, so it does not sit over the dB labels.
  C.PeLegend.Show = true
  C.PeLegend.ContourStyle = true
  C.PeLegend.Location = E.LegendLocation.Right
  const light = decayLightVector(props.lightAzimuthDeg, props.lightElevationDeg)
  C.PeFunction.SetLight(0, light.x, light.y, light.z)
  C.PePlot.Option.LightStrength = DECAY_LIGHT_STRENGTH
  C.PeString.MainTitle = ''
  C.PeString.SubTitle = ''

  C.PeGrid.Configure.AutoPadBeyondZeroX = true
  C.PeGrid.Configure.AutoPadBeyondZeroY = true
  C.PeGrid.Configure.AutoPadBeyondZeroZ = true

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
  // <p><b>Without this the Palette dropdown does nothing at all</b>, and does it silently: the
  // ramp written here reaches the renderer only when the vertices happen to be built afterwards,
  // which is true of the first draw and of nothing else. So the chart always opened on the right
  // palette and never changed — and the engine's own default contour ramp is a rainbow, so for a
  // while the chart looked Jet-coloured whatever was selected. Reported 2026-10-25; measured by
  // pushing a full grey ramp, which rendered byte-identically until this flag was set.</p>
  //
  // <p>Neither <c>ReinitializeResetImage</c> nor <c>Force3dxVerticeRebuild</c> is enough —
  // <c>Force3dxNewColors</c> is its own flag, documented as "force SubsetColors info to be
  // updated", and it is the one that carries a colour change across.</p>
  C.PeFunction.Force3dxNewColors = true
}

/** The engine's azimuth for a panel lean, wrapped into the 0-359 the engine accepts. */
function azimuthFor(rotationDeg: number): number {
  return ((Math.round(360 - rotationDeg) % 360) + 360) % 360
}

function applyCamera() {
  if (!chart) return
  // The panel's lean maps onto the engine's azimuth, and its depth onto the elevation. Both of the
  // panel's ranges are narrower than the engine's, so nothing has to be clamped here.
  //
  // <p><b>The depth axis is what picks the side</b>: from here the engine draws the smallest depth
  // coordinate at the <em>back</em>, so slice zero is where the Canvas 2D chart puts it and the
  // axis counts up towards the viewer with its own positive numbers. The cost is that the two
  // floor axes keep their handedness however the camera moves, so this side mirrors the across
  // axis — which <c>InvertedXAxis</c> in <c>applyData</c> puts back.</p>
  appliedRotation = props.rotationDeg
  appliedDepth = props.depthPercent
  // Wrapped into 0-359, or a lean outside the range the subtraction lands in leaves the engine's
  // own and it simply does not turn. Reported 2026-10-25 as the slider doing nothing below zero.
  chart.PeUserInterface.Scrollbar.DegreeOfRotation = azimuthFor(props.rotationDeg)
  chart.PeUserInterface.Scrollbar.ViewingHeight = Math.round(props.depthPercent * 0.6)
}

/**
 * The box, its grid numbers and its three axis labels — all of it, on or off together.
 *
 * <p>Off is what an empty chart wants: the engine plots its own built-in sample data whenever it
 * has none of ours, so leaving the axes on draws a labelled box around a dataset that is not a
 * measurement.</p>
 */
function showAxes(visible: boolean) {
  if (!chart || !enums) return
  const mode = visible ? enums.ShowAxis.All : enums.ShowAxis.Empty
  chart.PeGrid.Option.ShowXAxis = mode
  chart.PeGrid.Option.ShowYAxis = mode
  chart.PeGrid.Option.ShowZAxis = mode
  // The colour key is furniture too: a scale beside an empty plot is a key to nothing. Joost asked
  // for the axes to go on an empty measurement and keep the plot well, and this is the same ask.
  chart.PeLegend.Show = visible
  if (!visible) {
    chart.PeString.XAxisLabel = ''
    chart.PeString.YAxisLabel = ''
    chart.PeString.ZAxisLabel = ''
  }
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
    // With nothing to plot the engine falls back to its own sample data, and draws that: a box of
    // tick numbers from a dataset nobody asked for, under axes called "Y Axis" and "Z Axis". So an
    // empty chart is drawn as furniture-less — the plot well keeps its colour, which is what holds
    // the panel's shape, and the component's own "nothing measured yet" line is the only thing on
    // it. Reported 2026-10-25.
    showAxes(false)
    C.PeFunction.Force3dxVerticeRebuild = true
    C.PeFunction.ReinitializeResetImage()
    return
  }

  showAxes(true)

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

  // Two numbers, never three — GridAspectZ is left at its default on purpose, and decayGridAspect
  // says why: writing it as well states the box's shape twice, and contradicts it.
  const aspect = decayGridAspect({ x: props.axisLengthX, y: props.axisLengthY, z: props.axisLengthZ })
  C.PeGrid.Option.GridAspectX = aspect.x
  C.PeGrid.Option.GridAspectY = aspect.y

  // Whichever axis carries the frequency is scaled logarithmically, which is what puts true hertz
  // on its ticks. The height axis is in dB and the time axis starts at zero, so neither can be.
  C.PeGrid.Configure.XAxisScaleControl =
    mesh.acrossIsFrequency ? enums.ScaleControl.Log : enums.ScaleControl.Normal
  C.PeGrid.Configure.ZAxisScaleControl =
    mesh.depthIsFrequency ? enums.ScaleControl.Log : enums.ScaleControl.Normal
  C.PeGrid.Configure.YAxisScaleControl = enums.ScaleControl.Normal
  // <p><b>Always, and it is what makes the across axis read the way the Canvas 2D chart draws it</b>
  // — frequency increasing left to right in the by-frequency view, periods increasing left to
  // right in the by-period one. The camera sits on the side of the box that puts the depth axis
  // the right way round (see <c>azimuthFor</c>), and that side mirrors the across axis; this
  // un-mirrors it. What it takes to un-mirror differs with the scale, and <c>decayMesh</c> owns
  // the other half of it — see <c>DecayMesh.acrossNegated</c>, which is where the two cases are
  // written down.</p>
  //
  // <p><b>PeGrid.Option, not PeGrid.Configure</b>: the engine's own types put the three
  // inverted-axis flags under Option, a write to Configure lands on a property nothing reads, and
  // JavaScript accepts it in silence. That mistake is what had this whole family written up here
  // as inert for an afternoon. The Z one really is inert — <em>refused</em>, in fact: written true
  // it reads back false on this control, where X and Y take — so the depth axis cannot be reversed
  // this way at all, and the camera has to do it.</p>
  C.PeGrid.Option.InvertedXAxis = true
  // Plain hertz on the log axis — 1000, not 10³. The rest of the app writes 1k through formatHz,
  // but the engine owns this axis's formatting and an unambiguous number beats a wrong one.
  C.PeGrid.Configure.LogScaleExpLabels = false

  // Word for word what the Canvas 2D chart writes, so the two charts of one measurement cannot
  // disagree about what an axis is called.
  const timeLabel = `Time (${surface.sliceAxis === 'Periods' ? 'periods' : 'ms'})`
  C.PeString.XAxisLabel = mesh.acrossIsFrequency ? 'Frequency (Hz)' : timeLabel
  C.PeString.ZAxisLabel = mesh.depthIsFrequency ? 'Frequency (Hz)' : timeLabel
  // The engine's own, which means horizontal: it has no text-angle property anywhere in its API, so
  // this cannot be turned to read up the axis the way the Canvas 2D chart writes it. Drawing our
  // own rotated one in the template was tried and reverted — the height axis moves as the surface
  // is turned, and a label pinned to the left of the plot sits away from its own axis the moment
  // anyone drags it. A label that travels with the axis beats a label that reads the right way up.
  C.PeString.YAxisLabel = 'dB'

  // <p>The key's labels. The ramp is 256 colours and the legend will label every one of them
  // given the chance, which is a solid column of numbers — so this names a handful at known
  // heights and leaves the rest blank, which is what the property is for ("possibly define a few
  // labels at strategic colors to prevent overlapping").</p>
  //
  // <p>Index <c>i</c> of the ramp is the level a fraction <c>i / 255</c> up the scale, by
  // construction: <c>decaySubsetColors</c> fills it through <c>paletteIndex</c> over exactly that
  // fraction. So the arithmetic here and the colour of the surface cannot disagree.</p>
  C.PeString.ContourLabels.clear()
  for (let i = 0; i < DECAY_KEY_STEPS; i++) {
    const index = Math.round((i / (DECAY_KEY_STEPS - 1)) * 255)
    C.PeString.ContourLabels[index] = `${Math.round(-props.rangeDb + (index / 255) * props.rangeDb)}`
  }
  C.PeString.ContourLegendTitle = 'dB'

  C.PeFunction.Force3dxVerticeRebuild = true
  C.PeFunction.ReinitializeResetImage()
}

function resize() {
  if (!control || !chart) return
  const width = plotWidth()
  const height = chartHeight.value
  // The zoom follows the plot's shape, so a resize re-frames rather than only re-rasterising.
  chart.PePlot.Option.DxZoom = decayMagnifiedZoom(decayCameraZoom(width, height), props.magnification)
  control.setSize(width, height)
}

watch(chartHeight, () => resize())
watch(() => [props.surface, props.mode, props.rangeDb], () => applyData(), { deep: false })
watch(() => [props.palette, isDark.value], () => {
  applyTheme()
  chart?.PeFunction.ReinitializeResetImage()
})
/**
 * Where the surface sits, how big it is drawn and where the light comes from. They are applied by
 * `configure()`, which is where every engine property is written, so this re-runs that rather than
 * repeating five writes — and it is a separate watcher from the camera's because that one
 * short-circuits when the two angles have not moved, which none of these touch.
 */
watch(
  () => [props.moveX, props.moveY, props.magnification, props.lightAzimuthDeg, props.lightElevationDeg],
  () => {
    if (!chart) return
    configure()
    applyTheme()
    applyCamera()
    chart.PeFunction.Force3dxVerticeRebuild = true
    chart.PeFunction.ReinitializeResetImage()
  },
)

/**
 * <p><b>The three axis lengths rebuild the control, and nothing cheaper reaches them.</b> The box's
 * proportions are read while the scene is laid out and never looked at again: measured 2026-10-26,
 * moving any of the three through a reinitialise — and again through `render()`, with the vertices
 * forced to rebuild both times — left the picture <em>byte-identical</em>, the same pixels in the
 * same places. The same values applied at construction draw a different box, which is what says it
 * is the refresh rather than the values. (The first hour of that was spent on a worse fault
 * underneath it: all three aspect numbers were being written, which states the shape twice and
 * degenerates the box — see {@link decayGridAspect}.)</p>
 *
 * <p>A rebuild destroys a GPU context and creates another, which is not something to do lightly;
 * it is affordable here only because these three live in a collapsed section nobody opens on an
 * ordinary visit. Do not fold them into the watcher above.</p>
 */
watch(
  () => [props.axisLengthX, props.axisLengthY, props.axisLengthZ],
  () => {
    if (!control || disposed) return
    teardown()
    build()
  },
)

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
        // The shift-and-drag translation too, for the same reason as the camera angles: once the
        // surface has been moved, the props no longer describe what is on screen.
        viewportX: chart.PePlot.Option.DxViewportX,
        viewportY: chart.PePlot.Option.DxViewportY,
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
      chart.PePlot.Option.DxZoom = decayMagnifiedZoom(decayCameraZoom(width, plotHeight), props.magnification)
      // The live camera, not the props: once the surface has been dragged the props no longer
      // describe what is on screen, and a screenshot has to be the picture the user is looking at.
      if (live) {
        chart.PeUserInterface.Scrollbar.DegreeOfRotation = live.azimuth
        chart.PeUserInterface.Scrollbar.ViewingHeight = live.elevation
        chart.PePlot.Option.DxViewportX = live.viewportX
        chart.PePlot.Option.DxViewportY = live.viewportY
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
  // Set here too although the export copy's vertices are built after this and so would carry the
  // ramp anyway: a colour write that is not pushed is the fault above, and one of the two places
  // that writes this ramp remembering to push it is how that fault comes back.
  C.PeFunction.Force3dxNewColors = true
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
