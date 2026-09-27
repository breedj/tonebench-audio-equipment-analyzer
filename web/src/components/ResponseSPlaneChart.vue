<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { OverlayEntry } from '../composables/useMeasurements'
import type { DecayPalette, ResponsePhaseKindName, SurfaceModeName as SurfaceMode } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { CHART_EXPORT_HEADER_PX, composeChartPng, type ChartExportOptions } from '../charts/exportChart'
import { EXPORT_GRID_WIDTH_SCALE, exportGridStroke } from '../charts/palette'
import ExpandToggle from './ExpandToggle.vue'
import ToolbarIcon from './ToolbarIcon.vue'
import { fitTransform, niceTicks, thinPositions, type PlaneTransform, type Viewport } from '../charts/planeTransform'
import { dragZoomViewport } from '../charts/dragZoom'
import { useResponseComplexCurve } from '../composables/useResponseComplexCurve'
import { fitRootPoints, nearestRoot, visibleRoots, type RootPoint } from '../response/poleZeroMap'
import { fitRational, suggestOrder, POOR_FIT_DB, type FitResult } from '../response/rationalFit'
import {
  contourLevels,
  evaluateField,
  marchingSquares,
  modelLevelDb,
  passbandReferenceDb,
  relativeLevelLabel,
  REFERENCE_LEVEL_EPSILON_DB,
  surfaceWindow,
  type SurfaceField,
  type SurfaceWindow,
} from '../response/surfaceField'
import {
  axisTickHz,
  mappedRadius,
  sPlaneMap,
  surfaceViewport,
  toHz,
  toMapped,
  type SPlaneScale,
} from '../response/splaneScale'
import { levelIndex, lutColor, paletteLut } from '../measurements/decay'
import { formatHz } from '../response/format'
import { useAppTheme } from '../composables/useAppTheme'

/**
 * |H(s)| over the s-plane: the fitted rational model's magnitude as a heat map with iso-dB contours,
 * the roots on top, and the measured band marked on the jω axis.
 *
 * **A separate component from `ResponsePolarChart` rather than a fourth projection inside it.** That
 * file already branches three ways through its scene, viewport, grid, decorations, legend, tick
 * labels and export, and this view differs in every one of them while adding a raster, a contour
 * pass and a colour key. What the two genuinely share is factored out instead — `planeTransform`,
 * `dragZoom`, `useResponseComplexCurve`, `splaneScale`, `surfaceField` — which is also what makes
 * the arithmetic testable, since nothing in a `.vue` file is.
 *
 * **What is on screen is the model, not the measurement.** There is no measured value anywhere off
 * the jω axis, so the picture is only ever as good as the fit. Three things therefore ride on the
 * chart and not in a panel somewhere: the fitted order and residual, an amber warning when the fit
 * is poor or the roots are extrapolated or any root is in the right half-plane, and the measured
 * band drawn heavily on the axis with faint arcs at its two ends. The jω axis is also the check —
 * the field's own slice along it *is* the Bode magnitude, which `surfaceField.test.ts` pins.
 */

const props = defineProps<{
  traces: ResponseTrace[]
  overlays: OverlayEntry[]
  /** The panel's fit, so the picture and the pole/zero table can never disagree. */
  fit?: FitResult | null
  /** The stored order, for a headless report render where no panel exists. */
  fitOrder?: { poles: number; zeros: number } | null
  /**
   * Which way the same field is laid out: a flat heat map of the plane, or an oblique relief in
   * which a pole is a spike. One computed field serves both — see `drawRelief`.
   */
  mode: SurfaceMode
  scale: SPlaneScale
  /** dB above and below the passband reference that the colour ramp spans. */
  surfaceTopDb: number
  surfaceBottomDb: number
  palette: DecayPalette
  showContours: boolean
  /** Style only, shared with the other response charts. */
  thickLines: boolean
  hideOutOfBandRoots: boolean
  invertSign: boolean
  restoreDelay: boolean
  latencySamples: number | null
  sampleRateHz: number | null
  phaseKind: ResponsePhaseKindName
  xFromHz: number
  xToHz: number
  runLabel?: string
}>()

const { isDark } = useAppTheme()

const root = ref<HTMLDivElement | null>(null)
const container = ref<HTMLDivElement | null>(null)
const canvas = ref<HTMLCanvasElement | null>(null)

// Fitted to the view — see charts/useResultChartHeight.ts. First in setup, so a first paint on mount
// happens at the right height.
const chartHeight = useResultChartHeight(root, container)

/** The right inset is wider than the polar chart's because the colour key lives in it. */
const INSETS = { left: 56, right: 76, top: 16, bottom: 34 }

const POLE_COLOR = ['#dc2626', '#f87171'] as const
const ZERO_COLOR = ['#2563eb', '#60a5fa'] as const
const AXIS_COLOR = ['#4b5563', '#9ca3af'] as const
const MARKER_COLOR = ['#d97706', '#fbbf24'] as const
const SLICE_COLOR = ['#111827', '#f3f4f6'] as const
const CONTOUR_COLOR = ['rgba(17,24,39,0.55)', 'rgba(243,244,246,0.55)'] as const
const CONTOUR_REFERENCE_COLOR = ['rgba(17,24,39,0.9)', 'rgba(255,255,255,0.9)'] as const

/**
 * What the drawing code reads instead of the DOM: the theme, the canvas size in CSS pixels, and the
 * device pixel ratio. Set for the duration of one synchronous paint — the screen's, or the export's,
 * which draws the same scene light at a fixed size and at a ratio of 1. The ratio has to travel this
 * way rather than being read from `window` inside the raster code, because `putImageData` works in
 * backing-store pixels and the export's canvas has none.
 */
let paint = { dark: isDark.value, width: 800, height: 400, dpr: 1 }
let paintExport = false

function color(pair: readonly string[]): string {
  return paint.dark ? pair[1]! : pair[0]!
}

function gridStroke(): string {
  return paintExport ? exportGridStroke(color(AXIS_COLOR)) : color(AXIS_COLOR)
}

function gridLineWidth(base = 1): number {
  return paintExport ? base * EXPORT_GRID_WIDTH_SCALE : base
}

const { readCurve, band } = useResponseComplexCurve(() => ({
  traces: props.traces,
  overlays: props.overlays,
  xFromHz: props.xFromHz,
  xToHz: props.xToHz,
  invertSign: props.invertSign,
  restoreDelay: props.restoreDelay,
  latencySamples: props.latencySamples,
  sampleRateHz: props.sampleRateHz,
}))

/**
 * The fit the surface draws: the panel's when there is one, and otherwise — a report rendering a
 * stored result, where no panel exists — the same fit the panel would have made. Mirrors
 * `ResponsePolarChart.effectiveFit`, including its properness clamp.
 */
const effectiveFit = computed<FitResult | null>(() => {
  if (props.fit) return props.fit
  if (readCurve.value.length < 6) return null
  if (props.fitOrder) {
    const poles = Math.max(1, Math.min(10, props.fitOrder.poles))
    return fitRational(readCurve.value, Math.max(0, Math.min(poles, props.fitOrder.zeros)), poles)
  }
  const order = suggestOrder(readCurve.value, 8).order
  return fitRational(readCurve.value, order, order)
})

const map = computed(() => sPlaneMap(props.scale, band.value))
const allRoots = computed<RootPoint[]>(() => (effectiveFit.value ? fitRootPoints(effectiveFit.value) : []))
const roots = computed<RootPoint[]>(() => visibleRoots(allRoots.value, band.value, props.hideOutOfBandRoots))
const hiddenRootCount = computed(() => allRoots.value.length - roots.value.length)
const rhpRootCount = computed(() => allRoots.value.filter((r) => r.singularity.isRhp).length)

const window_ = computed<SurfaceWindow | null>(() => {
  const fit = effectiveFit.value
  if (!fit) return null
  return surfaceWindow(passbandReferenceDb(fit.model, band.value), props.surfaceTopDb, props.surfaceBottomDb)
})

/**
 * The order and residual, always on screen rather than hover-gated.
 *
 * A surface with no figure of merit beside it invites being read as a measurement; this is the line
 * that says what it actually is.
 */
const fitSummary = computed(() => {
  const fit = effectiveFit.value
  if (!fit) return null
  const b = band.value
  const bandText = b ? ` · band ${formatHz(b.lowHz)}–${formatHz(b.highHz)}` : ''
  return `Fitted model, order ${fit.numeratorOrder}/${fit.denominatorOrder} · ${fit.rmsErrorDb.toFixed(3)} dB rms${bandText}`
})

/** Warned about on the chart, because a surface is a far more persuasive picture than a table. */
const warning = computed(() => {
  const fit = effectiveFit.value
  if (!fit) return null
  if (fit.rmsErrorDb > POOR_FIT_DB) {
    return `The model is ${fit.rmsErrorDb.toFixed(2)} dB rms from the measurement, so this surface describes the fit rather than the DUT.`
  }
  if (allRoots.value.length > 0 && roots.value.length === 0) {
    return 'Every fitted root is outside the measured band, so the whole surface is extrapolation.'
  }
  if (rhpRootCount.value > 0) {
    return `${rhpRootCount.value} right-half-plane root${rhpRootCount.value === 1 ? '' : 's'}: the surface shows a growing exponential this DUT probably does not have.`
  }
  if (!props.restoreDelay && props.latencySamples !== null) {
    return `The chirp's removed delay (${props.latencySamples} samples) is still out, so the curve being fitted carries a phase lead that is not rational in s. Tick Restore removed delay before reading these roots.`
  }
  return null
})

/**
 * The window before any zoom — square, origin-centred, fitted to the roots *and* to the top of the
 * measured band. `splaneScale.surfaceViewport` owns that arithmetic and is tested; see there for why
 * it deliberately differs from `poleZeroViewport`.
 */
function baseViewport(): Viewport {
  return surfaceViewport(map.value, roots.value, band.value)
}

/**
 * The unzoomed window for whichever view is on screen.
 *
 * The slice is not a picture of the plane — its axes are frequency and *linear* magnitude — so it has
 * a window of its own, and the two are not interchangeable. Every caller that reaches for a base
 * window has to come through here: `renderScene`'s slice branch used to call `sliceViewport` itself
 * and ignore the window it was handed, so a dragged box was stored, flipped the header to "Reset
 * zoom", and was then thrown away on the very next paint — the slice looked as though a zoom simply
 * bounced back to the original view. Reported 2026-09-27.
 */
function baseViewportForMode(): Viewport {
  return props.mode === 'Slice' ? sliceViewport() : baseViewport()
}

/**
 * The palette a paint should use: the prop, except during a monochrome export.
 *
 * Declared before `exportPng` reads it rather than beside it, since a `const` in a temporal dead zone
 * is the kind of ordering trap that only fires on the one code path nobody exercises by hand.
 */
const exportPalette = ref<DecayPalette | null>(null)
const activePalette = computed<DecayPalette>(() => exportPalette.value ?? props.palette)

/** Null while unzoomed; a dragged box replaces it, and Reset clears it. */
const zoomViewport = ref<Viewport | null>(null)
const zoomed = computed(() => zoomViewport.value !== null)

function resetZoom() {
  zoomViewport.value = null
  draw()
}

/**
 * A zoom belongs to the window it was drawn in, and here that window moves for one more reason than
 * on the polar chart: `sPlaneMap`'s reference frequency comes from the measured band, so changing the
 * plotted window — or the traces — re-warps every point under `LogModulus`. Not cleared when the
 * *fit* changes, because the plane does not move when the order does.
 *
 * `mode` is in the list because the slice's axes are frequency and linear magnitude while the map's
 * are σ and jω: carrying a zoom across that switch would apply one view's numbers to the other's
 * plot. Harmless while the slice ignored its window; load-bearing now that it honours it.
 *
 * What is watched of the traces is the **measured band**, not the traces themselves. The band is the
 * whole of what the plane's geometry depends on — it is `sPlaneMap`'s reference frequency, and it is
 * what `surfaceViewport` fits to — while the curve's *values* change on every pass of a continuous
 * run without moving the plane by a hair. Watching the array meant a RUN threw the zoom away on
 * every pass, on all three views, which is indistinguishable from zoom not working at all. Same
 * reasoning that already keeps the fit out of this list.
 *
 * The source is **joined into a string**, and that is not tidiness. Reading `band.value` tracks the
 * computed, and therefore the traces behind it, so the effect still re-runs on every pass — and a
 * getter returning a fresh array is never `Object.is`-equal to the last one, so Vue fires the
 * callback on every one of those runs whatever the contents. (The old `deep: true` had the same
 * effect for the same reason.) A string compares by value, so an unchanged band is a no-op.
 */
watch(
  () =>
    [
      props.mode,
      props.scale,
      props.invertSign,
      props.restoreDelay,
      props.xFromHz,
      props.xToHz,
      band.value?.lowHz ?? null,
      band.value?.highHz ?? null,
    ].join('|'),
  () => {
    zoomViewport.value = null
  },
)

let transform: PlaneTransform | null = null

// --- the field -------------------------------------------------------------

/** About this many CSS pixels per evaluated cell, whatever the device pixel ratio. */
const CELL_CSS_PX = 2

let cachedField: { key: string; field: SurfaceField } | null = null

/**
 * The evaluated field for one window, cached.
 *
 * The cache is what makes a drag free: the pointer handler redraws on every move so the box follows
 * the cursor, but the viewport does not change until the drag is released, so re-evaluating the model
 * on each of those frames would be pure waste.
 */
function fieldFor(viewport: Viewport, cols: number, rows: number): SurfaceField | null {
  const fit = effectiveFit.value
  if (!fit) return null
  const m = map.value
  const key = [
    fit.model.num.join(','),
    fit.model.den.join(','),
    fit.model.omegaScale,
    viewport.xMin,
    viewport.xMax,
    viewport.yMin,
    viewport.yMax,
    cols,
    rows,
    m.scale,
    m.refHz,
  ].join('|')
  if (cachedField && cachedField.key === key) return cachedField.field
  const field = evaluateField(fit.model, m, viewport, cols, rows)
  cachedField = { key, field }
  return field
}

// --- drawing ---------------------------------------------------------------

function draw() {
  const el = canvas.value
  const box = container.value
  if (!el || !box) return

  const dpr = window.devicePixelRatio || 1
  const width = box.clientWidth || 800
  const height = box.clientHeight || chartHeight.value

  // Only resize when it actually changed: assigning width/height clears the canvas.
  if (el.width !== Math.round(width * dpr) || el.height !== Math.round(height * dpr)) {
    el.width = Math.round(width * dpr)
    el.height = Math.round(height * dpr)
    el.style.width = `${width}px`
    el.style.height = `${height}px`
  }

  const ctx = el.getContext('2d')
  if (!ctx) return

  ctx.save()
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  ctx.clearRect(0, 0, width, height)
  transform = renderScene(ctx, width, height, isDark.value, zoomViewport.value ?? baseViewportForMode(), true, false, dpr)
  ctx.restore()
}

function renderScene(
  ctx: CanvasRenderingContext2D,
  width: number,
  height: number,
  dark: boolean,
  viewport: Viewport,
  showHover: boolean,
  forExport: boolean,
  dpr: number,
): PlaneTransform {
  paint = { dark, width, height, dpr }
  paintExport = forExport

  // The slice is not a picture of the plane, so it gets its own axes: frequency and magnitude, no
  // equal-aspect lock, and none of the plane's decorations.
  if (props.mode === 'Slice') {
    // `viewport` — the caller's, so a dragged box zooms here too. It arrives in *this* view's
    // coordinates because that is what the transform returned below was built from, which is also
    // why a mode change has to clear the zoom (see the watcher on it).
    const st = fitTransform(viewport, width, height, INSETS, false)
    drawSliceAxes(ctx, width, height, st)
    drawSlice(ctx, st)
    drawLegend(ctx)
    return st
  }

  const t = fitTransform(viewport, width, height, INSETS, true)
  const w = window_.value
  const relief = props.mode === 'Relief'
  if (w) {
    if (relief) {
      drawRelief(ctx, t, w)
    } else {
      // The field is computed once and handed on, rather than each pass reaching for the cache: the
      // contours must be drawn over exactly the grid the raster used, not whatever was cached last.
      const field = drawHeatmap(ctx, t, w)
      if (field && props.showContours) drawContours(ctx, t, w, field)
    }
  }
  // The relief draws its own roots, on top of their spikes, and has no flat plane for an axis cross
  // or a band segment to lie on — those belong to the map view of the plane.
  if (!relief) {
    drawAxes(ctx, t)
    drawBand(ctx, t)
    drawRoots(ctx, t)
    drawAxisLabels(ctx, width, height, t)
  }
  if (w) drawColourKey(ctx, width, height, w)
  drawLegend(ctx)
  if (showHover) drawRootHover(ctx, t)
  return t
}

function plotBox(width: number, height: number) {
  return {
    x: INSETS.left,
    y: INSETS.top,
    w: Math.max(1, width - INSETS.left - INSETS.right),
    h: Math.max(1, height - INSETS.top - INSETS.bottom),
  }
}

function clipToPlot(ctx: CanvasRenderingContext2D) {
  const box = plotBox(paint.width, paint.height)
  ctx.beginPath()
  ctx.rect(box.x, box.y, box.w, box.h)
  ctx.clip()
}

/**
 * The jω-axis slice: |H| against frequency, on linear axes, with the measurement over it.
 *
 * This is the one line of the plane where the model and the measurement are the same thing, so it is
 * both the most ordinary view here and the one that falsifies the other two: if the solid curve and
 * the faint one part company, the surface beside them is describing a model that does not fit.
 *
 * Linear in both axes rather than dB against log f, which is what the Bode view already is. A linear
 * magnitude is how a filter is specified and how a textbook draws one — a passband at 1, a stopband
 * at 0, and a corner you can point at — and it is the only place in this app that shows it that way.
 *
 * It does not use the plane's transform: the axes are frequency and magnitude, not σ and jω, so it
 * builds its own and hands it back for the cursor and the drag-zoom to share.
 */
function sliceViewport(): Viewport {
  const fit = effectiveFit.value
  const b = band.value
  const highHz = b?.highHz ?? props.xToHz
  let peak = 1
  if (fit) {
    for (let i = 0; i <= SLICE_SAMPLES; i++) {
      const level = modelLevelDb(fit.model, 0, (highHz * i) / SLICE_SAMPLES)
      if (Number.isFinite(level)) peak = Math.max(peak, Math.pow(10, level / 20))
    }
  }
  for (const p of readCurve.value) peak = Math.max(peak, Math.pow(10, p.magDb / 20))
  // From zero on both axes: a linear magnitude plot that did not include 0 would misstate every
  // ratio the eye reads off it, which is the whole reason to draw one.
  return { xMin: 0, xMax: highHz > 0 ? highHz : 1, yMin: 0, yMax: peak * 1.08 }
}

/** How finely the slice is sampled — one point per two pixels is past what a screen can show. */
const SLICE_SAMPLES = 600

function drawSlice(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const fit = effectiveFit.value
  if (!fit) return

  const box = plotBox(paint.width, paint.height)
  ctx.save()
  clipToPlot(ctx)

  // The measurement first, so the model is read over it rather than under it.
  const measured = readCurve.value
  if (measured.length > 1) {
    ctx.strokeStyle = color(MARKER_COLOR)
    ctx.lineWidth = paintExport ? 2.5 : props.thickLines ? 2 : 1.5
    ctx.globalAlpha = 0.55
    ctx.beginPath()
    measured.forEach((p, i) => {
      const x = t.toScreenX(p.freqHz)
      const y = t.toScreenY(Math.pow(10, p.magDb / 20))
      if (i === 0) ctx.moveTo(x, y)
      else ctx.lineTo(x, y)
    })
    ctx.stroke()
    ctx.globalAlpha = 1
  }

  ctx.strokeStyle = color(SLICE_COLOR)
  ctx.lineWidth = paintExport ? 2.5 : props.thickLines ? 2 : 1.5
  ctx.beginPath()
  for (let i = 0; i <= SLICE_SAMPLES; i++) {
    const f = t.viewport.xMin + ((t.viewport.xMax - t.viewport.xMin) * i) / SLICE_SAMPLES
    const level = modelLevelDb(fit.model, 0, f)
    const y = t.toScreenY(Number.isFinite(level) ? Math.pow(10, level / 20) : t.viewport.yMax)
    const x = t.toScreenX(f)
    if (i === 0) ctx.moveTo(x, y)
    else ctx.lineTo(x, y)
  }
  ctx.stroke()

  // Where the data stops. Everything right of it is the model extrapolating, exactly as on the map.
  if (band.value && band.value.highHz < t.viewport.xMax) {
    const x = t.toScreenX(band.value.highHz)
    ctx.strokeStyle = color(MARKER_COLOR)
    ctx.setLineDash([4, 3])
    ctx.lineWidth = gridLineWidth(1)
    ctx.beginPath()
    ctx.moveTo(x, box.y)
    ctx.lineTo(x, box.y + box.h)
    ctx.stroke()
    ctx.setLineDash([])
  }
  ctx.restore()
}

/** Linear axes for the slice: frequency across, magnitude up. */
function drawSliceAxes(ctx: CanvasRenderingContext2D, width: number, height: number, t: PlaneTransform) {
  const xTicks = niceTicks(t.viewport.xMin, t.viewport.xMax, 8)
  const yTicks = niceTicks(t.viewport.yMin, t.viewport.yMax, 6)

  ctx.save()
  ctx.strokeStyle = gridStroke()
  ctx.lineWidth = gridLineWidth()
  ctx.beginPath()
  for (const x of xTicks) {
    const px = Math.round(t.toScreenX(x)) + 0.5
    ctx.moveTo(px, INSETS.top)
    ctx.lineTo(px, height - INSETS.bottom)
  }
  for (const y of yTicks) {
    const py = Math.round(t.toScreenY(y)) + 0.5
    ctx.moveTo(INSETS.left, py)
    ctx.lineTo(width - INSETS.right, py)
  }
  ctx.stroke()

  ctx.fillStyle = color(AXIS_COLOR)
  ctx.font = '11px ui-sans-serif, system-ui, sans-serif'
  ctx.textAlign = 'center'
  ctx.textBaseline = 'top'
  for (const tick of thinPositions(xTicks.map((x) => ({ at: t.toScreenX(x), v: x })), 34)) {
    ctx.fillText(tickLabel(tick.v), tick.at, height - INSETS.bottom + 6)
  }
  ctx.textAlign = 'right'
  ctx.textBaseline = 'middle'
  for (const tick of thinPositions(yTicks.map((y) => ({ at: t.toScreenY(y), v: y })), 14)) {
    ctx.fillText(String(Number(tick.v.toPrecision(3))), INSETS.left - 6, tick.at)
  }

  ctx.textAlign = 'center'
  ctx.textBaseline = 'bottom'
  ctx.fillText('frequency (Hz)', (INSETS.left + width - INSETS.right) / 2, height - 2)
  ctx.save()
  ctx.translate(10, (INSETS.top + height - INSETS.bottom) / 2)
  ctx.rotate(-Math.PI / 2)
  ctx.textBaseline = 'top'
  ctx.fillText('|H(jω)|', 0, 0)
  ctx.restore()
  ctx.restore()
}

/** Rows and columns the relief is built from — coarse, because it is drawn as profiles, not pixels. */
const RELIEF_GRID = 72

/**
 * The relief: the same field as an oblique surface, profile by profile, back to front.
 *
 * Hidden-line removal is the painter's algorithm — each row is filled down to the foot of the plot
 * before its profile is stroked, so a nearer row paints over whatever stood behind it. The same
 * arrangement `DecaySurfaceChart.drawWaterfall` uses, and for the same reason: a surface of this kind
 * needs no depth buffer if its rows are drawn in depth order.
 *
 * It shows the *same numbers* as the heat map — one field, one colour window, one palette — and
 * differs only in how they are laid out. A pole is a spike here and a bright core there; neither is
 * more true, and the spike is the one that reads as a pole to anyone who has seen the textbook
 * picture.
 */
function drawRelief(ctx: CanvasRenderingContext2D, t: PlaneTransform, w: SurfaceWindow): SurfaceField | null {
  const fit = effectiveFit.value
  const field = fieldFor(t.viewport, RELIEF_GRID, RELIEF_GRID)
  if (!field || !fit) return null

  const box = plotBox(paint.width, paint.height)
  const lut = paletteLut(activePalette.value, paintExport ? false : paint.dark)

  // The plane recedes up and to the right; what is left after the shear and the depth is the room a
  // spike has to rise into.
  const shearX = box.w * 0.26
  const depthY = box.h * 0.26
  const planeW = box.w - shearX
  const levelH = box.h * 0.4
  const originX = box.x
  const originY = box.y + depthY + levelH

  const span = w.topDb - w.bottomDb
  const norm = (level: number) => {
    if (!Number.isFinite(level)) return level > 0 ? 1 : 0
    const n = (level - w.bottomDb) / span
    return n < 0 ? 0 : n > 1 ? 1 : n
  }

  ctx.save()
  clipToPlot(ctx)
  ctx.lineJoin = 'round'
  const bodyFill = paint.dark ? 'rgba(10,10,10,0.92)' : 'rgba(255,255,255,0.92)'

  for (let row = 0; row < field.rows; row++) {
    const front = row / (field.rows - 1)
    const baseX = originX + shearX * (1 - front)
    const baseY = originY - depthY * (1 - front)
    const rowBase = row * field.cols

    const points: { x: number; y: number; level: number }[] = []
    for (let col = 0; col < field.cols; col++) {
      const level = field.levelDb[rowBase + col]!
      points.push({ x: baseX + (col / (field.cols - 1)) * planeW, y: baseY - norm(level) * levelH, level })
    }

    // Fill first, to the foot of the plot: everything under this profile belongs to it and has to
    // hide the profiles standing behind it.
    ctx.beginPath()
    ctx.moveTo(points[0]!.x, points[0]!.y)
    for (const p of points) ctx.lineTo(p.x, p.y)
    ctx.lineTo(points[points.length - 1]!.x, box.y + box.h)
    ctx.lineTo(points[0]!.x, box.y + box.h)
    ctx.closePath()
    ctx.fillStyle = bodyFill
    ctx.fill()

    // Then the profile, each segment in its own level's colour — what ties the relief to the heat
    // map and to the colour key beside it.
    ctx.lineWidth = paintExport ? 1.4 : 1
    for (let i = 1; i < points.length; i++) {
      const a = points[i - 1]!
      const b = points[i]!
      ctx.strokeStyle = lutColor(lut, levelIndex((a.level + b.level) / 2, w.topDb, w.bottomDb))
      ctx.beginPath()
      ctx.moveTo(a.x, a.y)
      ctx.lineTo(b.x, b.y)
      ctx.stroke()
    }
  }

  // The roots sit on top of their own spikes: a pole saturates the window, so its marker belongs at
  // the apex rather than buried somewhere inside the surface.
  for (const rootPoint of roots.value) {
    const p = toMapped(map.value, rootPoint.re, rootPoint.im)
    const u = (p.x - t.viewport.xMin) / (t.viewport.xMax - t.viewport.xMin)
    const v = (t.viewport.yMax - p.y) / (t.viewport.yMax - t.viewport.yMin)
    if (u < 0 || u > 1 || v < 0 || v > 1) continue
    const baseX = originX + shearX * (1 - v)
    const baseY = originY - depthY * (1 - v)
    const px = baseX + u * planeW
    const py = baseY - norm(modelLevelDb(fit.model, rootPoint.re, rootPoint.im)) * levelH
    ctx.strokeStyle = paint.dark ? 'rgba(0,0,0,0.65)' : 'rgba(255,255,255,0.8)'
    ctx.lineWidth = 4
    strokeGlyph(ctx, rootPoint, px, py)
    ctx.strokeStyle = rootPoint.kind === 'pole' ? color(POLE_COLOR) : color(ZERO_COLOR)
    ctx.lineWidth = 1.75
    strokeGlyph(ctx, rootPoint, px, py)
  }

  ctx.restore()
  return field
}

/**
 * The raster.
 *
 * Two things here are dictated by `putImageData` and are the traps `DecaySurfaceChart.drawSonogram`
 * documents. It works in **backing-store pixels and ignores the canvas transform**, so the image is
 * built at `plot × dpr` and blitted at a rounded `inset × dpr`; and it **overwrites alpha rather than
 * compositing**, which is harmless here only because every pixel of the image is filled opaque.
 */
function drawHeatmap(ctx: CanvasRenderingContext2D, t: PlaneTransform, w: SurfaceWindow): SurfaceField | null {
  const { dpr } = paint
  const box = plotBox(paint.width, paint.height)
  const pxW = Math.max(1, Math.round(box.w * dpr))
  const pxH = Math.max(1, Math.round(box.h * dpr))

  const cellPx = Math.max(1, Math.round(CELL_CSS_PX * dpr))
  const cols = Math.max(1, Math.ceil(pxW / cellPx))
  const rows = Math.max(1, Math.ceil(pxH / cellPx))

  const field = fieldFor(t.viewport, cols, rows)
  if (!field) return null

  const lut = paletteLut(activePalette.value, paintExport ? false : paint.dark)
  const image = ctx.createImageData(pxW, pxH)
  const data = image.data

  for (let py = 0; py < pxH; py++) {
    const row = Math.min(rows - 1, Math.floor(py / cellPx))
    const rowBase = row * cols
    let offset = py * pxW * 4
    for (let px = 0; px < pxW; px++) {
      const col = Math.min(cols - 1, Math.floor(px / cellPx))
      const index = levelIndex(field.levelDb[rowBase + col]!, w.topDb, w.bottomDb) * 3
      data[offset++] = lut[index]!
      data[offset++] = lut[index + 1]!
      data[offset++] = lut[index + 2]!
      data[offset++] = 255
    }
  }

  ctx.putImageData(image, Math.round(box.x * dpr), Math.round(box.y * dpr))
  return field
}

/**
 * Iso-dB contours, from marching squares over the same field.
 *
 * They are what makes the picture quantitative, and — because the colour ramp desaturates to a
 * near-useless grey, which `decay.ts` records — they are also what keeps a black-and-white report
 * readable. Drawn in cell coordinates mapped back to the plot, so they land wherever the warp puts
 * them without any special handling.
 */
function drawContours(
  ctx: CanvasRenderingContext2D,
  _t: PlaneTransform,
  w: SurfaceWindow,
  field: SurfaceField,
) {
  const box = plotBox(paint.width, paint.height)
  const cellW = box.w / field.cols
  const cellH = box.h / field.rows

  ctx.save()
  clipToPlot(ctx)
  for (const level of contourLevels(w)) {
    const segments = marchingSquares(field, level, 2)
    if (segments.length === 0) continue
    const isReference = Math.abs(level - w.referenceDb) < REFERENCE_LEVEL_EPSILON_DB
    ctx.strokeStyle = isReference ? color(CONTOUR_REFERENCE_COLOR) : color(CONTOUR_COLOR)
    ctx.lineWidth = gridLineWidth(isReference ? 1.75 : 1)
    ctx.beginPath()
    for (let i = 0; i < segments.length; i += 4) {
      ctx.moveTo(box.x + (segments[i]! + 0.5) * cellW, box.y + (segments[i + 1]! + 0.5) * cellH)
      ctx.lineTo(box.x + (segments[i + 2]! + 0.5) * cellW, box.y + (segments[i + 3]! + 0.5) * cellH)
    }
    ctx.stroke()
  }
  ctx.restore()
}

/** The σ and jω axes. The left half-plane is where a stable pole lives. */
function drawAxes(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const { width, height } = paint
  ctx.save()
  clipToPlot(ctx)
  ctx.strokeStyle = gridStroke()
  ctx.lineWidth = gridLineWidth(1)
  ctx.beginPath()
  ctx.moveTo(INSETS.left, t.toScreenY(0))
  ctx.lineTo(width - INSETS.right, t.toScreenY(0))
  ctx.moveTo(t.toScreenX(0), INSETS.top)
  ctx.lineTo(t.toScreenX(0), height - INSETS.bottom)
  ctx.stroke()
  ctx.restore()
}

/**
 * The measured band: a heavy segment on the jω axis, mirrored to ±, plus a faint arc at each end.
 *
 * The arcs mark the annulus the data actually spoke for. Dimming everything outside it was the
 * alternative and would have greyed out most of the picture, which is where the roots usually are.
 */
function drawBand(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const b = band.value
  if (!b) return
  const m = map.value

  ctx.save()
  clipToPlot(ctx)

  const lowRho = mappedRadius(m, b.lowHz)
  const highRho = mappedRadius(m, b.highHz)

  ctx.strokeStyle = color(MARKER_COLOR)
  ctx.globalAlpha = 0.25
  ctx.lineWidth = gridLineWidth(1)
  ctx.setLineDash([3, 3])
  for (const rho of [lowRho, highRho]) {
    const radiusPx = rho * t.scaleX
    if (!(radiusPx > 0.5)) continue
    ctx.beginPath()
    ctx.arc(t.toScreenX(0), t.toScreenY(0), radiusPx, 0, Math.PI * 2)
    ctx.stroke()
  }
  ctx.setLineDash([])

  ctx.globalAlpha = 0.6
  ctx.lineWidth = 4
  ctx.beginPath()
  for (const sign of [1, -1]) {
    ctx.moveTo(t.toScreenX(0), t.toScreenY(sign * lowRho))
    ctx.lineTo(t.toScreenX(0), t.toScreenY(sign * highRho))
  }
  ctx.stroke()
  ctx.restore()
}

function rootToPlane(root: RootPoint) {
  return toMapped(map.value, root.re, root.im)
}

function drawRoots(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  ctx.save()
  clipToPlot(ctx)
  ctx.lineWidth = 1.75
  for (const root of roots.value) {
    const p = rootToPlane(root)
    const px = t.toScreenX(p.x)
    const py = t.toScreenY(p.y)
    // A white underlay, because a × in the middle of a saturated pole is otherwise invisible.
    ctx.strokeStyle = paint.dark ? 'rgba(0,0,0,0.65)' : 'rgba(255,255,255,0.8)'
    ctx.lineWidth = 4
    strokeGlyph(ctx, root, px, py)
    ctx.strokeStyle = root.kind === 'pole' ? color(POLE_COLOR) : color(ZERO_COLOR)
    ctx.lineWidth = 1.75
    strokeGlyph(ctx, root, px, py)
  }
  ctx.restore()
}

function strokeGlyph(ctx: CanvasRenderingContext2D, root: RootPoint, px: number, py: number) {
  ctx.beginPath()
  if (root.kind === 'pole') {
    ctx.moveTo(px - 5, py - 5)
    ctx.lineTo(px + 5, py + 5)
    ctx.moveTo(px + 5, py - 5)
    ctx.lineTo(px - 5, py + 5)
  } else {
    ctx.arc(px, py, 5, 0, Math.PI * 2)
  }
  ctx.stroke()
}

/**
 * Axis ticks and captions.
 *
 * The ticks are *frequencies* placed through the warp rather than `niceTicks` over the drawing
 * coordinates, because under `LogModulus` an equal screen step is not an equal frequency step — see
 * `axisTickHz`. No gridlines over the raster: the contours are the grid, and a second set of lines on
 * a heat map is noise.
 */
function drawAxisLabels(ctx: CanvasRenderingContext2D, width: number, height: number, t: PlaneTransform) {
  const m = map.value
  const extent = Math.max(Math.abs(t.viewport.xMax), Math.abs(t.viewport.yMax))
  const ticks = axisTickHz(m, extent)

  ctx.save()
  ctx.fillStyle = color(AXIS_COLOR)
  ctx.font = '11px ui-sans-serif, system-ui, sans-serif'
  ctx.textAlign = 'center'
  ctx.textBaseline = 'top'

  const box = plotBox(width, height)

  // Zero first so it survives a crowd, then outward — see `thinPositions`. Under `LogModulus` a
  // decade near the origin occupies a few pixels by design, so without thinning the labels there
  // paint a smear of overlapping text.
  const signed = (toPos: (v: number) => number) => [
    { at: toPos(0), label: '0' },
    ...ticks.flatMap((hz) =>
      [1, -1].map((sign) => ({ at: toPos(sign * mappedRadius(m, hz)), label: `${sign < 0 ? '−' : ''}${tickLabel(hz)}` })),
    ),
  ]

  for (const tick of thinPositions(
    signed((v) => t.toScreenX(v)).filter((p) => p.at >= box.x && p.at <= box.x + box.w),
    30,
  )) {
    ctx.fillText(tick.label, tick.at, height - INSETS.bottom + 6)
  }

  ctx.textAlign = 'right'
  ctx.textBaseline = 'middle'
  for (const tick of thinPositions(
    signed((v) => t.toScreenY(v)).filter((p) => p.at >= box.y && p.at <= box.y + box.h),
    14,
  )) {
    ctx.fillText(tick.label, INSETS.left - 6, tick.at)
  }

  ctx.textAlign = 'center'
  ctx.textBaseline = 'bottom'
  ctx.fillText('σ / 2π (Hz)', (INSETS.left + width - INSETS.right) / 2, height - 2)
  ctx.save()
  ctx.translate(10, (INSETS.top + height - INSETS.bottom) / 2)
  ctx.rotate(-Math.PI / 2)
  ctx.textBaseline = 'top'
  ctx.fillText('jω / 2π (Hz)', 0, 0)
  ctx.restore()
  ctx.restore()
}

function tickLabel(hz: number): string {
  if (hz >= 1000) return `${Number((hz / 1000).toPrecision(3))}k`
  return String(Number(hz.toPrecision(3)))
}

/**
 * The colour key, up the right inset.
 *
 * Labelled from `contourLevels` — the same list the contour lines are drawn from — so the numbers
 * beside the ramp and the lines on the plot cannot disagree, which is the rule `decay.ts` states for
 * the waterfall's key.
 */
function drawColourKey(ctx: CanvasRenderingContext2D, width: number, height: number, w: SurfaceWindow) {
  const lut = paletteLut(activePalette.value, paintExport ? false : paint.dark)
  const box = plotBox(width, height)
  const keyW = 10
  const keyX = width - INSETS.right + 12
  const top = box.y
  const bottom = box.y + box.h

  ctx.save()
  for (let py = top; py < bottom; py++) {
    const fraction = 1 - (py - top) / Math.max(1, bottom - top - 1)
    const levelDb = w.bottomDb + fraction * (w.topDb - w.bottomDb)
    ctx.fillStyle = lutColor(lut, levelIndex(levelDb, w.topDb, w.bottomDb))
    ctx.fillRect(keyX, py, keyW, 1)
  }
  ctx.strokeStyle = gridStroke()
  ctx.lineWidth = gridLineWidth(1)
  ctx.strokeRect(keyX + 0.5, top + 0.5, keyW, bottom - top - 1)

  ctx.fillStyle = color(AXIS_COLOR)
  ctx.font = '10px ui-sans-serif, system-ui, sans-serif'
  ctx.textAlign = 'left'
  ctx.textBaseline = 'middle'
  for (const level of contourLevels(w)) {
    const fraction = (level - w.bottomDb) / Math.max(1e-9, w.topDb - w.bottomDb)
    const py = bottom - 1 - fraction * (bottom - top - 1)
    ctx.fillText(relativeLevelLabel(level, w.referenceDb), keyX + keyW + 4, py)
  }

  // `textBaseline` is what keeps this caption on the canvas. After the -90° rotation the local +y
  // axis points to screen *right*, so 'top' — which grows the glyphs along local +y — drew them off
  // the right edge from x = width - 4; 'bottom' grows them the other way, back into the plot. The
  // left caption in drawAxisLabels has the mirror problem and so keeps 'top'.
  ctx.save()
  ctx.translate(width - 3, (top + bottom) / 2)
  ctx.rotate(-Math.PI / 2)
  ctx.textAlign = 'center'
  ctx.textBaseline = 'bottom'
  ctx.fillText('dB re passband', 0, 0)
  ctx.restore()
  ctx.restore()
}

/** What every mark means, on the canvas so an export carries it. */
function drawLegend(ctx: CanvasRenderingContext2D) {
  const entries: { label: string; sample: 'pole' | 'zero' | 'band' | 'slice' | 'none' }[] =
    props.mode === 'Slice'
      ? [
          { label: 'Fitted model', sample: 'slice' },
          { label: 'Measured', sample: 'band' },
          { label: 'The jω axis of the surface — where model and measurement meet', sample: 'none' },
        ]
      : [
          { label: 'Pole', sample: 'pole' },
          { label: 'Zero', sample: 'zero' },
          { label: 'Measured band', sample: 'band' },
          { label: 'Fitted model — |H(σ+jω)|, not measured off the axis', sample: 'none' },
        ]
  if (props.mode !== 'Slice' && hiddenRootCount.value > 0) {
    entries.push({ label: `${hiddenRootCount.value} out of band, hidden`, sample: 'none' })
  }

  ctx.save()
  ctx.font = '11px ui-sans-serif, system-ui, sans-serif'
  ctx.textBaseline = 'middle'
  ctx.textAlign = 'left'

  const lineHeight = 14
  const widths = entries.map((e) => ctx.measureText(e.label).width + 22)
  const boxWidth = Math.max(...widths) + 8
  const boxHeight = entries.length * lineHeight + 8
  const x = INSETS.left + 8
  const y = INSETS.top + 8

  ctx.fillStyle = paint.dark ? 'rgba(10,10,10,0.72)' : 'rgba(255,255,255,0.82)'
  ctx.strokeStyle = gridStroke()
  ctx.lineWidth = gridLineWidth(1)
  ctx.beginPath()
  ctx.rect(x, y, boxWidth, boxHeight)
  ctx.fill()
  ctx.stroke()

  entries.forEach((entry, i) => {
    const cy = y + 4 + lineHeight * i + lineHeight / 2
    const sx = x + 6
    ctx.lineWidth = 1.5
    if (entry.sample === 'pole') {
      ctx.strokeStyle = color(POLE_COLOR)
      ctx.beginPath()
      ctx.moveTo(sx, cy - 4)
      ctx.lineTo(sx + 8, cy + 4)
      ctx.moveTo(sx + 8, cy - 4)
      ctx.lineTo(sx, cy + 4)
      ctx.stroke()
    } else if (entry.sample === 'zero') {
      ctx.strokeStyle = color(ZERO_COLOR)
      ctx.beginPath()
      ctx.arc(sx + 4, cy, 4, 0, Math.PI * 2)
      ctx.stroke()
    } else if (entry.sample === 'band') {
      ctx.strokeStyle = color(MARKER_COLOR)
      ctx.lineWidth = 4
      ctx.globalAlpha = 0.6
      ctx.beginPath()
      ctx.moveTo(sx, cy)
      ctx.lineTo(sx + 8, cy)
      ctx.stroke()
      ctx.globalAlpha = 1
    } else if (entry.sample === 'slice') {
      ctx.strokeStyle = color(SLICE_COLOR)
      ctx.lineWidth = 2
      ctx.beginPath()
      ctx.moveTo(sx, cy)
      ctx.lineTo(sx + 8, cy)
      ctx.stroke()
    }
    ctx.fillStyle = color(AXIS_COLOR)
    ctx.fillText(entry.label, sx + 14, cy)
  })
  ctx.restore()
}

// --- hover and zoom --------------------------------------------------------

const hoverRoot = ref<RootPoint | null>(null)
const hoverPoint = ref<{ sigmaHz: number; omegaHz: number; levelDb: number } | null>(null)
const dragStart = ref<{ x: number; y: number } | null>(null)
const dragNow = ref<{ x: number; y: number } | null>(null)

function localPoint(event: PointerEvent) {
  const el = canvas.value!
  const rect = el.getBoundingClientRect()
  return { x: event.clientX - rect.left, y: event.clientY - rect.top }
}

function onPointerDown(event: PointerEvent) {
  const point = localPoint(event)
  dragStart.value = point
  dragNow.value = point
  canvas.value?.setPointerCapture(event.pointerId)
}

function onPointerMove(event: PointerEvent) {
  const point = localPoint(event)
  if (dragStart.value) {
    dragNow.value = point
    draw()
    drawDragBox()
    return
  }
  updateHover(point)
  draw()
}

function updateHover(point: { x: number; y: number }) {
  const t = transform
  const fit = effectiveFit.value
  if (!t || !fit) {
    hoverRoot.value = null
    hoverPoint.value = null
    return
  }
  // On the slice the axes are frequency and magnitude, so the readout is the frequency under the
  // cursor and the model's level there — not a point in the plane.
  if (props.mode === 'Slice') {
    hoverRoot.value = null
    const freqHz = t.toWorldX(point.x)
    hoverPoint.value = { sigmaHz: 0, omegaHz: freqHz, levelDb: modelLevelDb(fit.model, 0, freqHz) }
    return
  }
  hoverRoot.value = nearestRoot(roots.value, point.x, point.y, (r) => {
    const p = rootToPlane(r)
    return { x: t.toScreenX(p.x), y: t.toScreenY(p.y) }
  })
  if (hoverRoot.value) {
    hoverPoint.value = null
    return
  }
  const s = toHz(map.value, t.toWorldX(point.x), t.toWorldY(point.y))
  hoverPoint.value = {
    sigmaHz: s.reHz,
    omegaHz: s.imHz,
    levelDb: modelLevelDb(fit.model, s.reHz, s.imHz),
  }
}

function onPointerUp(event: PointerEvent) {
  const start = dragStart.value
  const point = localPoint(event)
  dragStart.value = null
  dragNow.value = null
  if (start && transform) {
    const selected = dragZoomViewport(start, point, transform)
    if (selected) zoomViewport.value = selected
  }
  draw()
}

function drawDragBox() {
  const el = canvas.value
  const start = dragStart.value
  const now = dragNow.value
  if (!el || !start || !now) return
  const ctx = el.getContext('2d')
  if (!ctx) return
  const dpr = window.devicePixelRatio || 1
  ctx.save()
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  ctx.strokeStyle = color(AXIS_COLOR)
  ctx.setLineDash([4, 3])
  ctx.lineWidth = 1
  ctx.strokeRect(start.x, start.y, now.x - start.x, now.y - start.y)
  ctx.restore()
}

function drawRootHover(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const root = hoverRoot.value
  if (!root) return
  const p = rootToPlane(root)
  ctx.save()
  clipToPlot(ctx)
  ctx.strokeStyle = root.kind === 'pole' ? color(POLE_COLOR) : color(ZERO_COLOR)
  ctx.lineWidth = 1
  ctx.beginPath()
  ctx.arc(t.toScreenX(p.x), t.toScreenY(p.y), 9, 0, Math.PI * 2)
  ctx.stroke()
  ctx.restore()
}

// --- export ----------------------------------------------------------------

/**
 * The chart as a PNG, light whatever the theme and at a device ratio of 1.
 *
 * `monochrome` is honoured *here* rather than left to the report's post-process: desaturating a jet
 * ramp gives a near-useless grey, which `decay.ts:135-142` records, so a monochrome export is painted
 * with the `Grey` palette instead and keeps its contrast.
 */
async function exportPng({
  width,
  height,
  captureZoom = false,
  monochrome = false,
}: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header
  const plot = document.createElement('canvas')
  plot.width = width
  plot.height = plotHeight
  const ctx = plot.getContext('2d')
  if (!ctx) throw new Error('Could not get a 2D context for the chart export.')

  try {
    if (monochrome) exportPalette.value = 'Grey'
    renderScene(
      ctx,
      width,
      plotHeight,
      false,
      (captureZoom ? zoomViewport.value : null) ?? baseViewportForMode(),
      false,
      true,
      1,
    )
  } finally {
    exportPalette.value = null
    paint = { ...paint, dark: isDark.value, dpr: window.devicePixelRatio || 1 }
    // The cache now holds the export's geometry, which is not the screen's; drop it so the next
    // on-screen paint evaluates its own field rather than stretching this one.
    cachedField = null
  }

  const fit = effectiveFit.value
  const order = fit ? `, order ${fit.numeratorOrder}/${fit.denominatorOrder}` : ''
  const scaleName = props.scale === 'LogModulus' ? 'log-modulus' : 'linear'
  const title = `|H(s)| surface — s-plane in Hz, ${scaleName} radius${order}`
  return composeChartPng({ width, height, plotCanvas: plot, headerHeight: header, title })
}

// --- lifecycle -------------------------------------------------------------

let observer: ResizeObserver | null = null

onMounted(() => {
  draw()
  observer = new ResizeObserver(() => draw())
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
})

watch(
  () => [
    props.traces,
    props.overlays,
    props.fit,
    props.fitOrder,
    // `mode` belongs here for the same reason every other prop does, and its absence was invisible:
    // switching Map/Relief changed the prop but nothing repainted, so the new mode only appeared
    // once some *other* control happened to trigger a draw.
    props.mode,
    props.scale,
    props.surfaceTopDb,
    props.surfaceBottomDb,
    props.palette,
    props.showContours,
    props.hideOutOfBandRoots,
    props.invertSign,
    props.restoreDelay,
    props.xFromHz,
    props.xToHz,
    isDark.value,
  ],
  () => draw(),
  { deep: true },
)

// `curve` is the *read* curve: the pole-zero panel fits whatever the plot presents as run 0.
defineExpose({ curve: readCurve, exportPng })
</script>

<template>
  <div ref="root" class="relative flex flex-col gap-2">
    <ExpandToggle class="absolute right-0 top-0" :target="root" />
    <div class="min-h-6 pr-7 flex items-start justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
      <span v-if="hoverRoot" class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 font-mono">
        <span class="text-neutral-800 dark:text-neutral-100">
          {{ hoverRoot.kind === 'pole' ? 'Pole' : 'Zero' }} {{ formatHz(hoverRoot.singularity.freqHz) }}
        </span>
        <span v-if="hoverRoot.singularity.q !== null">Q {{ hoverRoot.singularity.q.toFixed(3) }}</span>
        <span>ζ {{ hoverRoot.singularity.zeta.toFixed(3) }}</span>
        <span v-if="hoverRoot.singularity.isRhp" class="text-amber-600 dark:text-amber-400">right half-plane</span>
      </span>
      <span v-else-if="hoverPoint" class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 font-mono">
        <span class="text-neutral-800 dark:text-neutral-100">
          {{ Number.isFinite(hoverPoint.levelDb) ? `${hoverPoint.levelDb.toFixed(2)} dB` : '∞' }}
        </span>
        <span>σ/2π {{ hoverPoint.sigmaHz < 0 ? '−' : '' }}{{ formatHz(Math.abs(hoverPoint.sigmaHz)) }}</span>
        <span>jω/2π {{ hoverPoint.omegaHz < 0 ? '−' : '' }}{{ formatHz(Math.abs(hoverPoint.omegaHz)) }}</span>
      </span>
      <span v-else-if="fitSummary">{{ fitSummary }}</span>
      <span v-else>No fitted model to draw</span>

      <button
        v-if="zoomed"
        type="button"
        class="inline-flex shrink-0 items-center gap-1 rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Back to the window this view opens on."
        @click="resetZoom"
      >
        <ToolbarIcon name="reset-zoom" />
        Reset zoom
      </button>
      <span v-else class="shrink-0">Drag a box to zoom</span>
    </div>

    <div ref="container" class="relative w-full" :style="{ height: `${chartHeight}px` }">
      <canvas
        ref="canvas"
        class="h-full w-full touch-none"
        @pointerdown="onPointerDown"
        @pointermove="onPointerMove"
        @pointerup="onPointerUp"
        @pointerleave="hoverRoot = null; hoverPoint = null"
      />
    </div>

    <p v-if="warning" class="text-xs text-amber-600 dark:text-amber-400">{{ warning }}</p>

    <p v-if="readCurve.length === 0" class="text-xs text-neutral-500 dark:text-neutral-400">
      <template v-if="traces.length === 0 && overlays.length === 0">
        No response measured yet. Press {{ props.runLabel ?? 'RUN' }}.
      </template>
      <template v-else-if="phaseKind === 'None'">
        This run measured no phase, so it has no complex response to model. Turn the right-channel
        generator on and make it the reference.
      </template>
      <template v-else-if="phaseKind === 'ChannelDifference'">
        This run's gain is measured against the generator while its phase is measured against the
        right input — two different ratios, which cannot be combined into one complex response. Make
        the right channel the reference.
      </template>
      <template v-else> Nothing inside the plotted frequency window. </template>
    </p>
  </div>
</template>
