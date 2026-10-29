<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { DecayChartMode, DecayPalette, DecaySurface } from '../api/client'
import { formatHz } from '../measurements/decay'
import {
  decayCameraZoom,
  decayLightVector,
  DECAY_LENS_FOV,
  DECAY_LIGHT_STRENGTH,
  decayExportTitle,
  decayGridAspect,
  decayMesh,
  decaySubsetColors,
  peColor,
} from '../measurements/decay3d'
import { PE_CONTROL_DEFAULTS, peRuntime } from '../charts/peModule'
import { thickenMeshLines } from '../charts/thickenMesh'
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
  /** How tall the box is drawn against its own floor; see DECAY_EXPLODE_Z. */
  explodeZ: number
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

/** How many lines the surface's mesh is reduced to; see where it is written. */
const DECAY_MESH_MAX_LINES = 20

/**
 * Widens the mesh in the composited image, which is the only way this engine will draw it wider
 * than a pixel — see `charts/thickenMesh.ts`, which has the measurements and why black identifies
 * a mesh pixel safely.
 *
 * <p>It runs a turn after the draw, because that is when the GPU layer composites into the
 * control's own 2D canvas; in a hidden pane no frame is delivered, nothing is composited and
 * nothing is thickened, which is the right answer there. Hovering does not undo it — the readout
 * is a hit test and repaints nothing (measured: 41 753 mesh pixels before a pointer move and
 * 41 753 after) — but the engine's own drag and wheel gestures do, so those re-run it.</p>
 */
function scheduleMeshThicken() {
  if (thickenFrame !== 0) cancelAnimationFrame(thickenFrame)
  thickenFrame = requestAnimationFrame(() => {
    thickenFrame = requestAnimationFrame(() => {
      thickenFrame = 0
      thickenMeshNow()
    })
  })
}

function thickenMeshNow() {
  const canvas = control?.canvas
  if (!canvas || disposed) return
  const ctx = canvas.getContext('2d')
  if (!ctx) return
  try {
    const image = ctx.getImageData(0, 0, canvas.width, canvas.height)
    // It reads the spacing off the mesh itself and declines where there is no room for a wider
    // line, which is the default framing — see thickenMesh.ts.
    thickenMeshLines(image)
    ctx.putImageData(image, 0, 0)
  } catch {
    // A canvas the browser will not let us read is one we leave as the engine drew it.
  }
}

let thickenFrame = 0

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
    // The two gestures the engine owns and repaints for itself; see onEngineGesture.
    control.el.addEventListener('pointerup', onEngineGesture)
    control.el.addEventListener('wheel', onEngineGesture, { passive: true })
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
  control?.el.removeEventListener('pointerup', onEngineGesture)
  control?.el.removeEventListener('wheel', onEngineGesture)
  if (thickenFrame !== 0) cancelAnimationFrame(thickenFrame)
  thickenFrame = 0
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
  // <p>How much of the canvas the plot box gets. Asked for 2026-10-28 as moving the axis labels
  // closer to the axes, "then we can make more use of the area" — and the first half of that is
  // not reachable: <b>the gap between the plot and its axis label is the engine's own and fixed</b>.
  // Measured against proessentials 11.0.1 over ImageAdjustBottom 0/100/200/300, the gap below the
  // content is 22/21/20/19 px — the label travels with the plot and the space between them does
  // not move. What these three <em>do</em> control is how much of the canvas the box is drawn in,
  // which is the half that was asked for.</p>
  //
  // <p>Measured as the drawn height of everything but the label, old (-100/100) against these
  // (-300/0): <b>292 → 321 px at 700x420, 389 → 428 at 900x560, 477 → 525 at 1100x686</b> — about
  // a tenth more box at every size this app draws — with 10 to 17 px still clear below the label.
  // At the 320 px floor (<c>MIN_RESULT_CHART_HEIGHT_PX</c>) the label is tight either way, and the
  // old pair was the worse of the two there: it put ink on row 0 and drew the box 3 px tall.</p>
  //
  // <p><b>The right margin is deliberately left alone.</b> It is where the contour legend is laid
  // out, and -200 is already enough to cost the legend its own labels — see <c>PeLegend</c>.</p>
  C.PeConfigure.ImageAdjustTop = -300
  C.PeConfigure.ImageAdjustBottom = 0
  C.PeConfigure.ImageAdjustRight = -200
  // <p>The wheel's own limits, and they have to bracket the zooms this chart sets or the gesture
  // has nowhere to go. The near end was <c>-3</c>, measured when the box was built from raw
  // lengths and the camera sat around -33; with the box reduced to ratios the camera lives at
  // about -3.3, so that ceiling left the wheel three tenths of a unit of travel and it could never
  // come in — reported 2026-10-26 as the wheel no longer zooming the way the vendor's own demo
  // does. Near enough to the focal point to push the axes out of the plot, which is the state that
  // report is asking for, and no nearer: past it the camera ends up inside the box.</p>
  C.PePlot.Option.DxZoomMin = -90
  C.PePlot.Option.DxZoomMax = -0.2
  // Both fixed: magnification is the size of the box now, not the camera — see
  // DECAY_VIEW_MAGNIFICATION, and decayGridAspect, which is where it lands.
  C.PePlot.Option.DxFOV = DECAY_LENS_FOV
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
  // The colour key, as the vendor's own demo draws it and asked for 2026-10-26. A contour-style
  // legend is a ramp with levels against it, which is what the Canvas 2D chart welds to its own
  // level axis — it cannot be welded here, since the engine draws that axis, so this is the same
  // reading in the shape the engine has for it. Left or right only, which the property's own
  // documentation says.
  //
  // <p><b>Left since 2026-10-28, and that is a fix as much as a preference.</b> Asked for in those
  // words — and measured on the way: on the right the ramp was drawn with <em>no numbers beside
  // it</em>, because <c>ImageAdjustRight = -200</c> gives the plot the strip the legend's own
  // labels are laid out in. Same picture with the margin at 0 and the labels are there. So a key
  // on the right reads as a bare colour bar; on the left, where nothing steals the margin, it
  // carries its dB figures. Verified against proessentials 11.0.1.</p>
  C.PeLegend.Show = true
  C.PeLegend.ContourStyle = true
  C.PeLegend.Location = E.LegendLocation.Left
  const light = decayLightVector(props.lightAzimuthDeg, props.lightElevationDeg)
  C.PeFunction.SetLight(0, light.x, light.y, light.z)
  C.PePlot.Option.LightStrength = DECAY_LIGHT_STRENGTH
  C.PeString.MainTitle = ''
  C.PeString.SubTitle = ''

  C.PeGrid.Configure.AutoPadBeyondZeroX = true
  C.PeGrid.Configure.AutoPadBeyondZeroY = true
  C.PeGrid.Configure.AutoPadBeyondZeroZ = true

  // The hit test the readout needs. Without `HotSpot.Data` the engine never builds its octree and
  // every GetHotSpot answers type -1 — which reads as "the pointer is never over the surface".
  //
  // <p><b>There is no highlight under the pointer, and that is what keeps the mesh.</b>
  // `HighlightColor` marks the point the engine's own way, and drawing it repaints the whole
  // surface — which throws away the wider mesh `thickenMeshLines` left in the composited image, so
  // the lines went thin the moment the pointer crossed the chart (reported 2026-10-29). Measured:
  // with a colour set, a real mouse move takes the mesh from 41 753 pixels back to 11 582; with it
  // transparent the move changes nothing, and `GetHotSpot` still answers `DataPoint` with the
  // subset and point. So the marker goes and the readout stays, which is the half that carries the
  // numbers. <b>Only a real pointer reproduces this</b> — a dispatched `PointerEvent` or
  // `MouseEvent` leaves the picture alone, which is why the first measurement of it was wrong.</p>
  //
  // **`PromptTracking` is off, and `PromptStyle = None` was not enough.** Tracking is what makes
  // the engine prompt the value under the pointer, and with the style set to None it printed the
  // bare number in a tooltip of its own — which over a cell this analysis could not reach is the
  // C formatting of a NaN, so a burst decay's whole unmeasured corner hovered as a box reading
  // `nan` beside our own readout saying "not measured this deep" (reported 2026-10-28). Nothing
  // here reads the engine's prompt: `GetHotSpot`/`GetHotSpotData` are called by hand in
  // onPointerMove, and the docs make those a separate enabler from tracking.
  C.PeUserInterface.HotSpot.Data = true
  C.PeUserInterface.Cursor.PromptTracking = false
  C.PeUserInterface.Cursor.HighlightColor = 0
  C.PeUserInterface.Cursor.PromptStyle = E.CursorPromptStyle.None

  C.PeData.DuplicateDataX = E.DuplicateData.PointIncrement
  C.PeData.DuplicateDataZ = E.DuplicateData.SubsetIncrement
  C.PeConfigure.RenderEngine = E.RenderEngine.Direct3D

  // <p><b>The mesh over the surface, and the compute shader is the price of it.</b> Asked for
  // 2026-10-28 — the Canvas 2D chart strokes a white line along every curve of a filled surface,
  // and the lit one had nothing. <c>ShowWireFrame</c> is the engine's iso-line grid and it is
  // free (2.5 ms a draw either way), but with <c>ComputeShader = true</c> it is drawn at full
  // data resolution: 400 columns of dark lines that bury the surface completely. <b>Turning the
  // shader off is also what makes <c>ShowWireframeMaxLines</c> work</b> — with it on, 10, 20, 25,
  // 50 and 80 render identically, which is why that property was written up here as inert. It is
  // not; it is simply ignored on the shader path.</p>
  //
  // <p>What the shader buys is dashed and dotted line styles under Direct3D, which this chart
  // never draws. What it costs is the draw call: measured five times each, <b>2.5–3.9 ms with it
  // on against 38.8–43.4 ms with it off</b>, and the wireframe itself is free in both — so the
  // 40 ms is the shader path and not the mesh. That is affordable because it is the same order as
  // the Canvas 2D chart's own 37.5 ms redraw, and because nothing here animates: the chart redraws
  // on a slider step, a resize or a new surface.</p>
  C.PeData.ComputeShader = false
  C.PePlot.Option.ShowWireFrame = true
  // <p><b>How many lines the grid is reduced to, and it has to be set.</b> The engine's own default
  // is 50, which is a readable grid on a 60-slice surface and a speckle on a 241-slice one — and
  // 241 is what an ordinary burst decay has (30 periods at 8 points each). Reported 2026-10-29 as
  // the mesh being hard to see, which is what a white line one pixel wide every few pixels looks
  // like once it has been anti-aliased into the surface under it.</p>
  //
  // <p>A constant rather than a figure derived from the data, deliberately: the mesh is furniture
  // and reads best at the same density whatever the analysis resolution is. Measured against
  // proessentials 11.0.1 at 241 slices x 400 columns — 12 is coarse, 20 is a clear grid, 30 is
  // busy but legible, 100 is his screenshot.</p>
  C.PePlot.Option.ShowWireframeMaxLines = DECAY_MESH_MAX_LINES
  // False, or the lines are coloured by height like the surface under them and vanish into it.
  // The colour itself is <c>PeColor.BarBorderColor</c> — see applyTheme.
  C.PePlot.Option.WireFrameContourColored = false

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

  // <p>The mesh's own colour, and <b>it is <c>BarBorderColor</c></b> — not
  // <c>SubsetColors[SurfaceColors.WireFrame]</c>, which the vendor's documentation names and which
  // does nothing at all here: measured 2026-10-28 by pushing magenta into each in turn and counting
  // the pixels that came back, 32000 and 32001 changed nothing and this changed the whole grid.</p>
  //
  // <p><b>Black, where the Canvas 2D chart's mesh is white, and the difference is the lighting.</b>
  // This engine lights the mesh along with the surface, so a white line is multiplied down towards
  // the colour underneath it and all but disappears over the bright middle of the ramp — reported
  // twice as the mesh being hard to see. Black is not shaded by anything.</p>
  //
  // <p>Measured 2026-10-29 as the mean luminance a mesh pixel moves against the same surface drawn
  // without one, split by how bright the surface under it is: <b>white 38.6 / 46.7 / 119.6 and
  // black 30.5 / 110.7 / 183.6</b> over the dark, middle and light thirds. Black is more than twice
  // as legible where it matters and a little behind only on the darkest floor, which is the part of
  // a decay nobody reads. Over the whole surface the figures are 65.1 against 99.0.</p>
  C.PeColor.BarBorderColor = peColor(0, 0, 0)

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

  // All three, and they agree with each other because the floor is square — decayGridAspect says
  // why that matters and what a box told two different things about its own shape does.
  const aspect = decayGridAspect(props.explodeZ, props.magnification)
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
  scheduleMeshThicken()
}

function resize() {
  if (!control || !chart) return
  const width = plotWidth()
  const height = chartHeight.value
  // The zoom follows the plot's shape, so a resize re-frames rather than only re-rasterising.
  chart.PePlot.Option.DxFOV = DECAY_LENS_FOV
  chart.PePlot.Option.DxZoom = decayCameraZoom(width, height)
  control.setSize(width, height)
}

watch(chartHeight, () => resize())

/**
 * The engine repaints on its own for the gestures it owns — drag to turn, wheel to zoom — and each
 * of those draws the mesh back at one pixel. Re-running on the pointer going up and on the wheel
 * settling puts it back without fighting the gesture itself.
 */
function onEngineGesture() {
  scheduleMeshThicken()
}
watch(() => [props.surface, props.mode, props.rangeDb], () => applyData(), { deep: false })
watch(() => [props.palette, isDark.value], () => {
  applyTheme()
  chart?.PeFunction.ReinitializeResetImage()
  scheduleMeshThicken()
})
/**
 * Where the surface sits, how big it is drawn and where the light comes from. They are applied by
 * `configure()`, which is where every engine property is written, so this re-runs that rather than
 * repeating five writes — and it is a separate watcher from the camera's because that one
 * short-circuits when the two angles have not moved, which none of these touch.
 */
watch(
  () => [props.moveX, props.moveY, props.lightAzimuthDeg, props.lightElevationDeg],
  () => {
    if (!chart) return
    configure()
    applyTheme()
    applyCamera()
    chart.PeFunction.Force3dxVerticeRebuild = true
    chart.PeFunction.ReinitializeResetImage()
    scheduleMeshThicken()
  },
)

/**
 * <p><b>Explode Z changes the box's shape, and the engine takes that without the control being
 * rebuilt.</b> The three axis-length sliders this replaced did need one, and the reason is worth
 * keeping: they were writing a contradictory aspect triple, which degenerates the box into a slab
 * that no later value moves — so the rebuild was working around a bug rather than around the
 * engine. With a square floor the triple is consistent by construction and the vendor's own
 * sequence is enough. Measured 2026-10-29 against proessentials 11.0.1: explode 5 / 15 / 26 / 40 /
 * 50 drew 25 176 / 29 055 / 33 304 / 38 817 / 41 964 pixels of surface through nothing but this,
 * at about 45 ms a step — the same order as any other redraw with the compute shader off.</p>
 */
watch(
  () => [props.explodeZ, props.magnification],
  () => {
    if (!chart) return
    const aspect = decayGridAspect(props.explodeZ, props.magnification)
    chart.PeGrid.Option.GridAspectX = aspect.x
    chart.PeGrid.Option.GridAspectY = aspect.y
    chart.PeGrid.Option.GridAspectZ = aspect.z
    // The camera goes back where the chart frames it. It is the wheel's from the first turn, and
    // putting it back is what makes Reset view — and any slider — undo a wheel zoom, which is the
    // behaviour magnification had while it was the camera rather than the box.
    chart.PePlot.Option.DxFOV = DECAY_LENS_FOV
    chart.PePlot.Option.DxZoom = decayCameraZoom(plotWidth(), chartHeight.value)
    // The vendor's own order, from its Explode Z handler: new colours, then the mesh, then a render.
    chart.PeFunction.Force3dxNewColors = true
    chart.PeFunction.Force3dxVerticeRebuild = true
    chart.PeFunction.ReinitializeResetImage()
    scheduleMeshThicken()
  },
)

watch(() => [props.rotationDeg, props.depthPercent], () => {
  if (props.rotationDeg === appliedRotation && props.depthPercent === appliedDepth) return
  applyCamera()
  chart?.PeFunction.ReinitializeResetImage()
  scheduleMeshThicken()
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
      chart.PePlot.Option.DxFOV = DECAY_LENS_FOV
      chart.PePlot.Option.DxZoom = decayCameraZoom(width, plotHeight)
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
    // The same widening the screen gets, on the copy's own image, so a report or a screenshot
    // carries the mesh the chart is read with rather than the engine's hairline.
    const copied = copy.canvas.getContext('2d')
    if (copied) {
      const image = copied.getImageData(0, 0, copy.canvas.width, copy.canvas.height)
      thickenMeshLines(image)
      copied.putImageData(image, 0, 0)
    }
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
