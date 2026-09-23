<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { OverlayEntry } from '../composables/useMeasurements'
import type { ResponsePhaseKindName } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { responseOverlayDash } from '../measurements/sweepOverlayStyles'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { CHART_EXPORT_HEADER_PX, composeChartPng, type ChartExportOptions } from '../charts/exportChart'
import ExpandToggle from './ExpandToggle.vue'
import ToolbarIcon from './ToolbarIcon.vue'
import { fitTransform, niceTicks, type PlaneTransform, type Viewport } from '../charts/planeTransform'
import { toComplexCurve, type ComplexPoint } from '../response/complexPlane'
import { stabilityMargins } from '../response/margins'
import { evaluateModel, fitRational, suggestOrder, type FitResult } from '../response/rationalFit'
import {
  fitRootPoints,
  measuredBand,
  nearestRoot,
  poleZeroViewport,
  visibleRoots,
  type RootPoint,
} from '../response/poleZeroMap'
import {
  MIN_LINEAR_EXTENT,
  criticalPhases,
  nicholsPoint,
  nyquistPoint,
  symmetricExtent,
  type NyquistRadiusMode,
  type PlanePoint,
} from '../response/projection'
import { formatHz } from '../response/format'
import { useAppTheme } from '../composables/useAppTheme'

/**
 * The complex-plane views of a response measurement: Nyquist (Re/Im), Nichols (phase/gain) and
 * the pole–zero map (the fitted model's roots in the s-plane, in hertz).
 *
 * Hand-drawn on a canvas rather than built on uPlot, and that is forced rather than chosen. uPlot
 * wants an ascending x array — `FrequencyResponseChart` gives it log frequency and its cursor
 * indexes `u.data[0]` by x — while both of these curves are *parametric* in frequency: Re(f) is not
 * monotonic, and neither is unwrapped phase. A custom `paths` builder can draw the polyline, but the
 * two things this chart actually needs beyond that — a nearest-point cursor and a true equal aspect
 * — have to be hand-rolled either way, which leaves a uPlot instance with a fake x axis and its own
 * cursor. So: a canvas.
 *
 * One component for all three projections. Nyquist and Nichols differ in exactly three things — the
 * point map, the decorations, and whether the aspect is locked — and share the polyline, the
 * overlays, the hover readout, the zoom and the theme. The pole–zero map draws no contour at all:
 * it is a different plane (s, not G(jω) — see response/poleZeroMap.ts), so it shares the canvas,
 * the grid, the zoom and the legend, and draws the roots where the others draw the curve.
 */
const props = defineProps<{
  traces: ResponseTrace[]
  overlays: OverlayEntry[]
  projection: 'Nyquist' | 'Nichols' | 'PoleZero'
  radiusMode: NyquistRadiusMode
  radiusFloorDb: number
  /** Adds 180°: the summing-junction inversion the measurement cannot see. */
  invertSign: boolean
  /**
   * Draw the fitted model over the measurement on Nyquist and Nichols. The pole-zero map ignores
   * it: there the fit is not an overlay on the measurement, it is the content.
   */
  showFittedModel: boolean
  /** Pole-zero map: leave out the roots the fit placed outside the measured band - see visibleRoots. */
  hideOutOfBandRoots: boolean
  /** Puts back the bulk delay the chirp estimator removed. Null latency means there is none. */
  restoreDelay: boolean
  latencySamples: number | null
  sampleRateHz: number | null
  phaseKind: ResponsePhaseKindName
  xFromHz: number
  xToHz: number
  thickLines: boolean
  showDataPoints: boolean
  runLabel?: string
  /**
   * The fitted rational model — the pole/zero panel's — drawn over the measurement as a dashed
   * curve on Nyquist and Nichols, and as the roots themselves on the pole–zero map. The dashed
   * curve is what makes the pole/zero table falsifiable: a list of roots whose model does not track
   * the measured contour is a description of nothing, and only the residual shows it. Null when no
   * panel is mounted (a report render); the map then fits its own, see `effectiveFit`.
   */
  fit?: FitResult | null
}>()

const { isDark } = useAppTheme()

const root = ref<HTMLDivElement | null>(null)
const container = ref<HTMLDivElement | null>(null)
const canvas = ref<HTMLCanvasElement | null>(null)

// Fitted to the view — see charts/useResultChartHeight.ts; set as an inline style on the plot box
// so the ResizeObserver below keeps reading one number whatever changed.
const chartHeight = useResultChartHeight(root, container)
const INSETS = { left: 56, right: 16, top: 16, bottom: 34 }

const CURVE_COLOR = ['#111827', '#f3f4f6'] as const
const CRITICAL_COLOR = ['#dc2626', '#f87171'] as const
const UNIT_COLOR = ['#2563eb', '#60a5fa'] as const
const GRID_COLOR = ['#e5e7eb', '#374151'] as const
const AXIS_COLOR = ['#4b5563', '#9ca3af'] as const
const MARKER_COLOR = ['#d97706', '#fbbf24'] as const
const FIT_COLOR = ['#7c3aed', '#a78bfa'] as const

/**
 * What the drawing code reads instead of the DOM: which theme, and the canvas size in CSS pixels.
 * Set by renderScene for the duration of one synchronous paint — the screen's, or the export's,
 * which draws the same scene light and at a fixed size onto a canvas of its own. Safe as module
 * state only because everything here draws synchronously; see charts/palette.ts for the uPlot case.
 */
let paint = { dark: isDark.value, width: 800, height: 400 }

function color(pair: readonly string[]): string {
  return paint.dark ? pair[1] : pair[0]
}

/**
 * Only the Gain curve carries a relationship between two channels, and only that relationship is a
 * transfer function. A stepped run's Left and Right traces hold an all-zero phase array by
 * construction — the same trap `FrequencyResponseChart`'s `phaseTrace` documents, which there drew
 * a flat 0° line and here would draw a tidy straight line along the positive real axis.
 */
const gainTrace = computed(() => props.traces.find((t) => t.kind === 'Gain') ?? null)

/** A chirp's low-coherence bins whip around the origin and manufacture phantom crossings; a stepped run's coherence is a constant 1. */
const MIN_COHERENCE = 0.5

const curveOptions = computed(() => ({
  fromHz: props.xFromHz,
  toHz: props.xToHz,
  invertSign: props.invertSign,
  minCoherence: MIN_COHERENCE,
  latencySamples: props.restoreDelay ? props.latencySamples : null,
  sampleRateHz: props.sampleRateHz,
}))

const curve = computed<ComplexPoint[]>(() =>
  gainTrace.value ? toComplexCurve(gainTrace.value, curveOptions.value) : [],
)

/**
 * Stored runs, restricted twice over: to the Gain curve, for the reason above, and to runs whose
 * own phase kind is one this plot can represent. An overlay is drawn without the live run's delay
 * correction — `StoredResponseTrace` carries neither a latency nor a sample rate — which is why the
 * header says so when the live curve is being corrected.
 */
const overlayCurves = computed(() =>
  props.overlays
    .filter((o) => o.visible && o.responseTraces?.length)
    .map((overlay) => {
      const trace = overlay.responseTraces?.find((t) => t.kind === 'Gain')
      if (!trace) return null
      const points = toComplexCurve(trace, { ...curveOptions.value, latencySamples: null })
      if (points.length < 2) return null
      return { id: overlay.id, name: overlay.detail.name, points }
    })
    .filter((entry): entry is { id: number; name: string; points: ComplexPoint[] } => entry !== null),
)

const margins = computed(() => stabilityMargins(curve.value))

/**
 * The fit the map draws. The panel's when there is one, so the map and the table can never
 * disagree; otherwise — a report rendering a stored result, where no panel exists — the same fit
 * the panel would make at its automatic order, from the same curve.
 */
const effectiveFit = computed<FitResult | null>(() => {
  if (props.fit) return props.fit
  if (props.projection !== 'PoleZero' || curve.value.length < 6) return null
  const order = suggestOrder(curve.value, 8).order
  return fitRational(curve.value, order, order)
})

const band = computed(() => measuredBand(curve.value))

/** Every root of the fit; `roots` is what the map actually draws. */
const allRoots = computed<RootPoint[]>(() => (effectiveFit.value ? fitRootPoints(effectiveFit.value) : []))
const roots = computed<RootPoint[]>(() => visibleRoots(allRoots.value, band.value, props.hideOutOfBandRoots))
const hiddenRootCount = computed(() => allRoots.value.length - roots.value.length)

function toPlane(point: ComplexPoint): PlanePoint {
  return props.projection === 'Nyquist'
    ? nyquistPoint(point, props.radiusMode, props.radiusFloorDb)
    : nicholsPoint(point)
}

/** A root's place on the map: σ/2π along x, ω/2π up y. */
function rootToPlane(root: RootPoint): PlanePoint {
  return { x: root.re, y: root.im, clamped: false }
}

const equalAspect = computed(() => props.projection !== 'Nichols')

/**
 * The window the chart opens on, before any zoom.
 *
 * Nyquist fits the data, floored at ±MIN_LINEAR_EXTENT so a curve that lives near the unit circle
 * still shows it and the −1 marker. It opened on a *fixed* ±2 at first, and that drew an empty pair
 * of axes for the ordinary case: an amplifier measured input-to-output sits at |G| = its gain, so a
 * 26.8 dB stage put every point ~22 units out and clipped the lot. See MIN_LINEAR_EXTENT.
 *
 * Nichols needs no floor of its own: both of its axes are already logarithmic in the physical
 * quantity, so it fits to the data with a margin.
 */
function baseViewport(): Viewport {
  if (props.projection === 'PoleZero') return poleZeroViewport(roots.value)

  const points = [...curve.value, ...overlayCurves.value.flatMap((o) => o.points)]

  if (props.projection === 'Nyquist') {
    // Measured in *plane* coordinates, so the dB radius map's own compression is accounted for
    // rather than the raw |G| it was applied to.
    const e = symmetricExtent(points.map(toPlane), MIN_LINEAR_EXTENT)
    return { xMin: -e, xMax: e, yMin: -e, yMax: e }
  }

  if (points.length === 0) return { xMin: -360, xMax: 0, yMin: -40, yMax: 20 }

  let xMin = Infinity
  let xMax = -Infinity
  let yMin = Infinity
  let yMax = -Infinity
  for (const p of points) {
    xMin = Math.min(xMin, p.phaseDeg)
    xMax = Math.max(xMax, p.phaseDeg)
    yMin = Math.min(yMin, p.magDb)
    yMax = Math.max(yMax, p.magDb)
  }

  // The critical point must always be in view — a Nichols chart that fitted tightly to a
  // comfortable loop would crop the one marker the reader is looking for.
  xMin = Math.min(xMin, -190)
  xMax = Math.max(xMax, -170)
  yMin = Math.min(yMin, -6)
  yMax = Math.max(yMax, 6)

  const padX = Math.max(15, (xMax - xMin) * 0.05)
  const padY = Math.max(3, (yMax - yMin) * 0.08)
  return { xMin: xMin - padX, xMax: xMax + padX, yMin: yMin - padY, yMax: yMax + padY }
}

/** Null while unzoomed; a dragged box replaces it, and Reset clears it. */
const zoomViewport = ref<Viewport | null>(null)
const zoomed = computed(() => zoomViewport.value !== null)

function resetZoom() {
  zoomViewport.value = null
  draw()
}

// A zoom belongs to the window it was drawn in. Changing projection, radius map or sign moves every
// point, so a box dragged around the old contour frames nothing in the new one.
watch(
  () => [props.projection, props.radiusMode, props.radiusFloorDb, props.invertSign, props.restoreDelay],
  () => {
    zoomViewport.value = null
  },
)

let transform: PlaneTransform | null = null

// --- drawing ---------------------------------------------------------------

function draw() {
  const el = canvas.value
  const box = container.value
  if (!el || !box) return

  const dpr = window.devicePixelRatio || 1
  const width = box.clientWidth || 800
  const height = box.clientHeight || chartHeight.value

  // Only resize when it actually changed: assigning width/height clears the canvas, so doing it
  // every frame is both a wasted allocation and a guaranteed flash.
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
  transform = renderScene(ctx, width, height, isDark.value, zoomViewport.value ?? baseViewport(), true)
  ctx.restore()
}

/** The whole picture — grid, decorations, overlays, the curve, the fit, the crossings — onto any context. */
function renderScene(
  ctx: CanvasRenderingContext2D,
  width: number,
  height: number,
  dark: boolean,
  viewport: Viewport,
  showHover: boolean,
): PlaneTransform {
  paint = { dark, width, height }
  const t = fitTransform(viewport, width, height, INSETS, equalAspect.value)

  drawGrid(ctx, width, height, t)
  drawDecorations(ctx, t)

  if (props.projection === 'PoleZero') {
    drawPoleZeroMap(ctx, t)
    drawLegend(ctx, t)
    if (showHover) drawRootHover(ctx, t)
    return t
  }

  for (const overlay of overlayCurves.value) {
    drawCurve(ctx, overlay.points, t, {
      stroke: color(CURVE_COLOR),
      width: 1,
      dash: responseOverlayDash(props.overlays, overlay.id) ?? [6, 3],
      alpha: 0.7,
      points: false,
    })
  }
  drawCurve(ctx, curve.value, t, {
    stroke: color(CURVE_COLOR),
    width: props.thickLines ? 2 : 1,
    dash: [],
    alpha: 1,
    points: props.showDataPoints,
  })
  drawFittedModel(ctx, t)
  drawCrossings(ctx, t)
  drawLegend(ctx, t)
  if (showHover) drawHover(ctx, t)
  return t
}

/**
 * The chart as a PNG of exactly the requested size, light whatever the theme — the same arrangement
 * as FrequencyResponseChart.exportPng. A canvas chart needs no offscreen component: the scene is
 * drawn straight onto the export's own canvas at 1:1.
 */
async function exportPng({ width, height, captureZoom = false, primaryLabel }: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header
  const plot = document.createElement('canvas')
  plot.width = width
  plot.height = plotHeight
  const ctx = plot.getContext('2d')
  if (!ctx) throw new Error('Could not get a 2D context for the chart export.')

  try {
    renderScene(ctx, width, plotHeight, false, (captureZoom ? zoomViewport.value : null) ?? baseViewport(), false)
  } finally {
    paint = { ...paint, dark: isDark.value }
  }

  const title =
    props.projection === 'Nyquist'
      ? `Nyquist — real vs imaginary (${props.radiusMode === 'Linear' ? 'linear' : 'log'} radius)`
      : props.projection === 'Nichols'
        ? 'Nichols — gain (dB) vs phase (°)'
        : `Poles and zeros — s-plane in Hz${effectiveFit.value ? `, order ${effectiveFit.value.denominatorOrder}` : ''}` +
          (hiddenRootCount.value > 0 ? `, ${hiddenRootCount.value} out of band hidden` : '')
  const legend =
    props.projection === 'PoleZero'
      ? [
          { label: 'Pole', color: CRITICAL_COLOR[0] },
          { label: 'Zero', color: UNIT_COLOR[0] },
        ]
      : [
          { label: primaryLabel ?? 'Response', color: CURVE_COLOR[0] },
          ...overlayCurves.value.map((o) => ({
            label: o.name,
            color: CURVE_COLOR[0],
            dash: responseOverlayDash(props.overlays, o.id) ?? [6, 3],
          })),
          ...(props.fit && props.showFittedModel ? [{ label: 'Fitted model', color: FIT_COLOR[0], dash: [5, 3] }] : []),
        ]
  return composeChartPng({ width, height, plotCanvas: plot, headerHeight: header, title, legend })
}

// --- legend ----------------------------------------------------------------

interface LegendEntry {
  label: string
  color: string
  /** How the sample is drawn: a line (solid or dashed), one of the marker glyphs, or nothing - a note. */
  sample: 'line' | 'dashed' | 'cross' | 'ring' | 'pole' | 'zero' | 'band' | 'none'
  dash?: number[]
}

/**
 * What every mark on the plot is. On the canvas rather than in the DOM so an export carries it,
 * and drawn last so it sits over the curve. Only marks actually present are listed — a legend that
 * names a fitted model on a plot with none is a puzzle of its own.
 */
function legendEntries(t: PlaneTransform): LegendEntry[] {
  if (props.projection === 'PoleZero') {
    return [
      { label: 'Pole', color: color(CRITICAL_COLOR), sample: 'pole' },
      { label: 'Zero', color: color(UNIT_COLOR), sample: 'zero' },
      { label: 'Measured band (jω axis)', color: color(MARKER_COLOR), sample: 'band' },
      ...(hiddenRootCount.value > 0
        ? [{ label: `${hiddenRootCount.value} out of band, hidden`, color: color(AXIS_COLOR), sample: 'none' as const }]
        : []),
    ]
  }

  const nyquist = props.projection === 'Nyquist'
  const entries: LegendEntry[] = [{ label: props.runLabel === 'RUN SWEEP' ? 'Measured (stepped)' : 'Measured', color: color(CURVE_COLOR), sample: 'line' }]
  for (const o of overlayCurves.value) {
    entries.push({ label: o.name, color: color(CURVE_COLOR), sample: 'dashed', dash: responseOverlayDash(props.overlays, o.id) ?? [6, 3] })
  }
  if (props.fit && props.showFittedModel) {
    entries.push({ label: 'Fitted model', color: color(FIT_COLOR), sample: 'dashed', dash: [5, 3] })
  }
  entries.push({ label: nyquist ? 'Unit circle, |G| = 1' : '0 dB', color: color(UNIT_COLOR), sample: 'dashed', dash: [4, 3] })
  // Nichols draws no −180° markers at all when the window holds too many turns (see criticalPhases).
  if (nyquist || criticalPhases(t.viewport.xMin, t.viewport.xMax).length > 0) {
    entries.push({ label: nyquist ? '−1 (critical point)' : '−180° (critical phase)', color: color(CRITICAL_COLOR), sample: 'cross' })
  }
  const { gainCrossoverHz, phaseCrossoverHz } = margins.value
  if (gainCrossoverHz != null || phaseCrossoverHz != null) {
    entries.push({ label: 'Crossings: |G| = 1, −180°', color: color(MARKER_COLOR), sample: 'ring' })
  }
  return entries
}

function drawLegend(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const entries = legendEntries(t)
  if (entries.length === 0) return

  const rowHeight = 15
  const padding = 6
  const sampleWidth = 22
  ctx.save()
  ctx.font = '11px ui-sans-serif, system-ui, sans-serif'
  let textWidth = 0
  for (const e of entries) textWidth = Math.max(textWidth, ctx.measureText(e.label).width)
  const boxWidth = padding * 2 + sampleWidth + 6 + textWidth
  const boxHeight = padding * 2 + rowHeight * entries.length
  const x = paint.width - INSETS.right - boxWidth - 6
  const y = INSETS.top + 6

  ctx.fillStyle = paint.dark ? 'rgba(23, 23, 23, 0.85)' : 'rgba(255, 255, 255, 0.88)'
  ctx.strokeStyle = color(GRID_COLOR)
  ctx.lineWidth = 1
  ctx.beginPath()
  ctx.rect(x + 0.5, y + 0.5, boxWidth, boxHeight)
  ctx.fill()
  ctx.stroke()

  ctx.textAlign = 'left'
  ctx.textBaseline = 'middle'
  entries.forEach((e, i) => {
    const cy = y + padding + rowHeight * i + rowHeight / 2
    const sx = x + padding
    const cx = sx + sampleWidth / 2
    ctx.strokeStyle = e.color
    ctx.fillStyle = e.color
    ctx.lineWidth = 1.5
    ctx.setLineDash(e.sample === 'dashed' ? (e.dash ?? [4, 3]) : [])
    ctx.beginPath()
    switch (e.sample) {
      case 'line':
      case 'dashed':
        ctx.moveTo(sx, cy)
        ctx.lineTo(sx + sampleWidth, cy)
        ctx.stroke()
        break
      case 'cross':
      case 'pole':
        ctx.moveTo(cx - 4, cy - 4)
        ctx.lineTo(cx + 4, cy + 4)
        ctx.moveTo(cx + 4, cy - 4)
        ctx.lineTo(cx - 4, cy + 4)
        ctx.stroke()
        break
      case 'ring':
      case 'zero':
        ctx.arc(cx, cy, 4, 0, Math.PI * 2)
        ctx.stroke()
        break
      case 'band':
        ctx.lineWidth = 4
        ctx.moveTo(sx + 4, cy)
        ctx.lineTo(sx + sampleWidth - 4, cy)
        ctx.stroke()
        break
      case 'none':
        break
    }
    ctx.setLineDash([])
    ctx.fillStyle = color(AXIS_COLOR)
    ctx.fillText(e.label, sx + sampleWidth + 6, cy)
  })
  ctx.restore()
}

// --- pole–zero map -----------------------------------------------------------

/**
 * The roots of the fitted model as × (poles) and ○ (zeros), conjugates included, with the measured
 * band drawn as a heavy segment on the jω axis — the only stretch of the axis the data spoke for.
 * A root well outside it is the fit's extrapolation, exactly as the panel's "out of band" flag
 * says, and here that reads directly off the picture.
 */
function drawPoleZeroMap(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  ctx.save()
  clipToPlot(ctx, paint.width, paint.height)

  const measured = curve.value
  if (measured.length >= 2) {
    const lowHz = measured[0].freqHz
    const highHz = measured[measured.length - 1].freqHz
    ctx.strokeStyle = color(MARKER_COLOR)
    ctx.lineWidth = 4
    ctx.globalAlpha = 0.6
    ctx.beginPath()
    for (const sign of [1, -1]) {
      ctx.moveTo(t.toScreenX(0), t.toScreenY(sign * lowHz))
      ctx.lineTo(t.toScreenX(0), t.toScreenY(sign * highHz))
    }
    ctx.stroke()
    ctx.globalAlpha = 1
  }

  ctx.lineWidth = 1.5
  for (const root of roots.value) {
    const plane = rootToPlane(root)
    const px = t.toScreenX(plane.x)
    const py = t.toScreenY(plane.y)
    ctx.beginPath()
    if (root.kind === 'pole') {
      ctx.strokeStyle = color(CRITICAL_COLOR)
      ctx.moveTo(px - 5, py - 5)
      ctx.lineTo(px + 5, py + 5)
      ctx.moveTo(px + 5, py - 5)
      ctx.lineTo(px - 5, py + 5)
    } else {
      ctx.strokeStyle = color(UNIT_COLOR)
      ctx.arc(px, py, 5, 0, Math.PI * 2)
    }
    ctx.stroke()
  }
  ctx.restore()
}

function clipToPlot(ctx: CanvasRenderingContext2D, width: number, height: number) {
  ctx.beginPath()
  ctx.rect(INSETS.left, INSETS.top, width - INSETS.left - INSETS.right, height - INSETS.top - INSETS.bottom)
  ctx.clip()
}

function drawGrid(ctx: CanvasRenderingContext2D, width: number, height: number, t: PlaneTransform) {
  const { viewport } = t
  const xTicks = niceTicks(viewport.xMin, viewport.xMax, 8)
  const yTicks = niceTicks(viewport.yMin, viewport.yMax, 6)

  ctx.save()
  ctx.strokeStyle = color(GRID_COLOR)
  ctx.lineWidth = 1
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
  for (const x of xTicks) ctx.fillText(tickLabel(x), t.toScreenX(x), height - INSETS.bottom + 6)

  ctx.textAlign = 'right'
  ctx.textBaseline = 'middle'
  for (const y of yTicks) ctx.fillText(tickLabel(y), INSETS.left - 6, t.toScreenY(y))

  // Axis captions, so the three projections are never confused for one another.
  ctx.textAlign = 'center'
  ctx.textBaseline = 'bottom'
  ctx.fillText(
    props.projection === 'Nyquist' ? 'Re' : props.projection === 'Nichols' ? 'Phase (°)' : 'σ / 2π (Hz)',
    (INSETS.left + width - INSETS.right) / 2,
    height - 2,
  )
  ctx.save()
  ctx.translate(10, (INSETS.top + height - INSETS.bottom) / 2)
  ctx.rotate(-Math.PI / 2)
  ctx.textBaseline = 'top'
  ctx.fillText(props.projection === 'Nyquist' ? 'Im' : props.projection === 'Nichols' ? 'Gain (dB)' : 'jω / 2π (Hz)', 0, 0)
  ctx.restore()
  ctx.restore()
}

function tickLabel(v: number): string {
  if (v === 0) return '0'
  const abs = Math.abs(v)
  // The map's axes are hertz, and "1e+4" is not how anyone reads a frequency axis.
  if (props.projection === 'PoleZero' && abs >= 1000) return `${Number((v / 1000).toPrecision(3))}k`
  if (abs >= 1000 || abs < 0.01) return v.toExponential(0)
  return String(Number(v.toPrecision(3)))
}

function drawDecorations(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const { width, height } = paint

  ctx.save()
  clipToPlot(ctx, width, height)

  if (props.projection === 'PoleZero') {
    // The σ and jω axes; the left half-plane is where a stable pole lives, and nothing else here
    // needs a decoration — the roots are the content.
    ctx.strokeStyle = color(AXIS_COLOR)
    ctx.lineWidth = 1
    ctx.beginPath()
    ctx.moveTo(INSETS.left, t.toScreenY(0))
    ctx.lineTo(width - INSETS.right, t.toScreenY(0))
    ctx.moveTo(t.toScreenX(0), INSETS.top)
    ctx.lineTo(t.toScreenX(0), height - INSETS.bottom)
    ctx.stroke()
  } else if (props.projection === 'Nyquist') {
    // The origin axes.
    ctx.strokeStyle = color(AXIS_COLOR)
    ctx.lineWidth = 1
    ctx.beginPath()
    ctx.moveTo(INSETS.left, t.toScreenY(0))
    ctx.lineTo(width - INSETS.right, t.toScreenY(0))
    ctx.moveTo(t.toScreenX(0), INSETS.top)
    ctx.lineTo(t.toScreenX(0), height - INSETS.bottom)
    ctx.stroke()

    // The unit circle: |G| = 1 under either radius map, so it always means the gain crossover.
    ctx.strokeStyle = color(UNIT_COLOR)
    ctx.setLineDash([4, 3])
    ctx.beginPath()
    ctx.ellipse(t.toScreenX(0), t.toScreenY(0), t.scaleX, t.scaleY, 0, 0, Math.PI * 2)
    ctx.stroke()
    ctx.setLineDash([])

    drawCritical(ctx, t.toScreenX(-1), t.toScreenY(0), '−1')
  } else {
    // Every turn, not just −180: the phase axis is unwrapped, so a three-pole loop is already past
    // −270° and a single marker at −180 would sit outside the curve entirely.
    for (const phase of criticalPhases(t.viewport.xMin, t.viewport.xMax)) {
      drawCritical(ctx, t.toScreenX(phase), t.toScreenY(0), `${phase}°`)
    }

    ctx.strokeStyle = color(UNIT_COLOR)
    ctx.setLineDash([4, 3])
    ctx.beginPath()
    ctx.moveTo(INSETS.left, t.toScreenY(0))
    ctx.lineTo(width - INSETS.right, t.toScreenY(0))
    ctx.stroke()
    ctx.setLineDash([])
  }

  ctx.restore()
}

function drawCritical(ctx: CanvasRenderingContext2D, px: number, py: number, label: string) {
  ctx.save()
  ctx.strokeStyle = color(CRITICAL_COLOR)
  ctx.fillStyle = color(CRITICAL_COLOR)
  ctx.lineWidth = 1.5
  ctx.beginPath()
  ctx.moveTo(px - 5, py - 5)
  ctx.lineTo(px + 5, py + 5)
  ctx.moveTo(px + 5, py - 5)
  ctx.lineTo(px - 5, py + 5)
  ctx.stroke()

  ctx.font = '11px ui-sans-serif, system-ui, sans-serif'
  ctx.textAlign = 'left'
  ctx.textBaseline = 'bottom'
  ctx.fillText(label, px + 7, py - 5)
  ctx.restore()
}

interface CurveStyle {
  stroke: string
  width: number
  dash: number[]
  alpha: number
  points: boolean
}

function drawCurve(ctx: CanvasRenderingContext2D, points: ComplexPoint[], t: PlaneTransform, style: CurveStyle) {
  if (points.length === 0) return

  ctx.save()
  clipToPlot(ctx, paint.width, paint.height)
  ctx.globalAlpha = style.alpha
  ctx.strokeStyle = style.stroke
  ctx.lineWidth = style.width
  ctx.setLineDash(style.dash)
  ctx.lineJoin = 'round'

  ctx.beginPath()
  points.forEach((point, i) => {
    const plane = toPlane(point)
    const px = t.toScreenX(plane.x)
    const py = t.toScreenY(plane.y)
    if (i === 0) ctx.moveTo(px, py)
    else ctx.lineTo(px, py)
  })
  ctx.stroke()
  ctx.setLineDash([])

  if (style.points) {
    ctx.fillStyle = style.stroke
    for (const point of points) {
      const plane = toPlane(point)
      ctx.beginPath()
      ctx.arc(t.toScreenX(plane.x), t.toScreenY(plane.y), 1.75, 0, Math.PI * 2)
      ctx.fill()
    }
  }

  drawDirectionArrow(ctx, points, t, style.stroke)
  ctx.restore()
}

/**
 * One arrowhead at the middle of the curve, pointing the way frequency increases.
 *
 * A Nyquist contour is unreadable without it: the encirclement count is a *signed* quantity, and
 * nothing else on the picture says which way round the curve was traversed.
 */
function drawDirectionArrow(
  ctx: CanvasRenderingContext2D,
  points: ComplexPoint[],
  t: PlaneTransform,
  stroke: string,
) {
  if (points.length < 4) return
  const mid = Math.floor(points.length / 2)
  const a = toPlane(points[mid - 1])
  const b = toPlane(points[mid + 1])
  const ax = t.toScreenX(a.x)
  const ay = t.toScreenY(a.y)
  const bx = t.toScreenX(b.x)
  const by = t.toScreenY(b.y)
  const angle = Math.atan2(by - ay, bx - ax)
  if (!Number.isFinite(angle)) return

  const px = t.toScreenX(toPlane(points[mid]).x)
  const py = t.toScreenY(toPlane(points[mid]).y)
  const size = 7

  ctx.save()
  ctx.fillStyle = stroke
  ctx.beginPath()
  ctx.moveTo(px, py)
  ctx.lineTo(px - size * Math.cos(angle - 0.4), py - size * Math.sin(angle - 0.4))
  ctx.lineTo(px - size * Math.cos(angle + 0.4), py - size * Math.sin(angle + 0.4))
  ctx.closePath()
  ctx.fill()
  ctx.restore()
}

/**
 * The fitted model, sampled on its own dense log grid across the measured band.
 *
 * Densely rather than at the measured frequencies because a 50-point stepped sweep would draw the
 * model as the same polygon as the data, which hides exactly the disagreement this curve exists to
 * reveal.
 */
function drawFittedModel(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const model = props.showFittedModel ? props.fit?.model : null
  const measured = curve.value
  if (!model || measured.length < 2) return

  const lowHz = measured[0].freqHz
  const highHz = measured[measured.length - 1].freqHz
  if (!(lowHz > 0) || !(highHz > lowHz)) return

  const steps = 400
  const ratio = Math.log(highHz / lowHz)

  ctx.save()
  clipToPlot(ctx, paint.width, paint.height)
  ctx.strokeStyle = color(FIT_COLOR)
  ctx.lineWidth = 1.5
  ctx.setLineDash([5, 3])
  ctx.lineJoin = 'round'
  ctx.beginPath()

  for (let i = 0; i <= steps; i++) {
    const freqHz = lowHz * Math.exp((ratio * i) / steps)
    const h = evaluateModel(model, freqHz)
    if (!Number.isFinite(h.re) || !Number.isFinite(h.im)) continue

    const magDb = 20 * Math.log10(Math.hypot(h.re, h.im))
    const phaseDeg = (Math.atan2(h.im, h.re) * 180) / Math.PI
    const plane = toPlane({ freqHz, re: h.re, im: h.im, magDb, phaseDeg })

    if (i === 0) ctx.moveTo(t.toScreenX(plane.x), t.toScreenY(plane.y))
    else ctx.lineTo(t.toScreenX(plane.x), t.toScreenY(plane.y))
  }

  ctx.stroke()
  ctx.setLineDash([])
  ctx.restore()
}

/** Rings at the crossings the margin readout is quoting, so a number in the panel has a place on the plot. */
function drawCrossings(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const { gainCrossoverHz, phaseCrossoverHz } = margins.value

  ctx.save()
  clipToPlot(ctx, paint.width, paint.height)
  ctx.strokeStyle = color(MARKER_COLOR)
  ctx.lineWidth = 1.5

  for (const freqHz of [gainCrossoverHz, phaseCrossoverHz]) {
    if (freqHz == null) continue
    const point = nearestByFrequency(freqHz)
    if (!point) continue
    const plane = toPlane(point)
    ctx.beginPath()
    ctx.arc(t.toScreenX(plane.x), t.toScreenY(plane.y), 4.5, 0, Math.PI * 2)
    ctx.stroke()
  }
  ctx.restore()
}

function nearestByFrequency(freqHz: number): ComplexPoint | null {
  let best: ComplexPoint | null = null
  let bestDistance = Infinity
  for (const point of curve.value) {
    const distance = Math.abs(Math.log(point.freqHz) - Math.log(freqHz))
    if (distance < bestDistance) {
      bestDistance = distance
      best = point
    }
  }
  return best
}

// --- hover -----------------------------------------------------------------

const hover = ref<ComplexPoint | null>(null)
const hoverRoot = ref<RootPoint | null>(null)

function drawRootHover(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const root = hoverRoot.value
  if (!root) return
  const plane = rootToPlane(root)
  ctx.save()
  ctx.strokeStyle = color(AXIS_COLOR)
  ctx.lineWidth = 1
  ctx.beginPath()
  ctx.arc(t.toScreenX(plane.x), t.toScreenY(plane.y), 9, 0, Math.PI * 2)
  ctx.stroke()
  ctx.restore()
}

function drawHover(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const point = hover.value
  if (!point) return
  const plane = toPlane(point)

  ctx.save()
  ctx.fillStyle = color(CRITICAL_COLOR)
  ctx.beginPath()
  ctx.arc(t.toScreenX(plane.x), t.toScreenY(plane.y), 3.5, 0, Math.PI * 2)
  ctx.fill()
  ctx.restore()
}

/** Nearest point by *screen* distance — the curve doubles back on itself, so nearest-in-x is meaningless here. */
function nearestPoint(px: number, py: number): ComplexPoint | null {
  if (!transform) return null
  let best: ComplexPoint | null = null
  let bestDistance = Infinity

  for (const point of curve.value) {
    const plane = toPlane(point)
    const dx = transform.toScreenX(plane.x) - px
    const dy = transform.toScreenY(plane.y) - py
    const distance = dx * dx + dy * dy
    if (distance < bestDistance) {
      bestDistance = distance
      best = point
    }
  }

  // Beyond about 30 px the "nearest" point is not what the pointer is over.
  return bestDistance <= 900 ? best : null
}

// --- zoom ------------------------------------------------------------------

const dragStart = ref<{ x: number; y: number } | null>(null)
const dragNow = ref<{ x: number; y: number } | null>(null)

function localPoint(event: PointerEvent): { x: number; y: number } {
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
  return { x: event.clientX - rect.left, y: event.clientY - rect.top }
}

function onPointerDown(event: PointerEvent) {
  const point = localPoint(event)
  dragStart.value = point
  dragNow.value = point
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
}

function onPointerMove(event: PointerEvent) {
  const point = localPoint(event)
  if (dragStart.value) {
    dragNow.value = point
    draw()
    drawDragBox()
    return
  }
  if (props.projection === 'PoleZero') {
    hoverRoot.value = transform
      ? nearestRoot(roots.value, point.x, point.y, (r) => ({ x: transform!.toScreenX(r.re), y: transform!.toScreenY(r.im) }))
      : null
    hover.value = null
  } else {
    hover.value = nearestPoint(point.x, point.y)
    hoverRoot.value = null
  }
  draw()
}

function onPointerUp(event: PointerEvent) {
  const start = dragStart.value
  const point = localPoint(event)
  dragStart.value = null
  dragNow.value = null

  if (start && transform) {
    // A click, not a drag: too small a box would zoom to a meaningless sliver.
    if (Math.abs(point.x - start.x) > 8 && Math.abs(point.y - start.y) > 8) {
      const xs = [transform.toWorldX(start.x), transform.toWorldX(point.x)].sort((a, b) => a - b)
      const ys = [transform.toWorldY(start.y), transform.toWorldY(point.y)].sort((a, b) => a - b)
      zoomViewport.value = { xMin: xs[0], xMax: xs[1], yMin: ys[0], yMax: ys[1] }
    }
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
    props.projection,
    props.radiusMode,
    props.radiusFloorDb,
    props.invertSign,
    props.restoreDelay,
    props.xFromHz,
    props.xToHz,
    props.thickLines,
    props.showDataPoints,
    props.fit,
    props.showFittedModel,
    props.hideOutOfBandRoots,
    isDark.value,
  ],
  () => draw(),
  { deep: true },
)

defineExpose({ margins, curve, exportPng })
</script>

<template>
  <div ref="root" class="relative flex flex-col gap-2">
    <ExpandToggle class="absolute right-0 top-0" :target="root" />
    <div class="min-h-6 pr-7 flex items-start justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
      <span v-if="hover" class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 font-mono">
        <span class="text-neutral-800 dark:text-neutral-100">{{ formatHz(hover.freqHz) }}</span>
        <span>{{ hover.magDb.toFixed(2) }} dB</span>
        <span>{{ hover.phaseDeg.toFixed(1) }}°</span>
        <span>Re {{ hover.re.toFixed(4) }}</span>
        <span>Im {{ hover.im.toFixed(4) }}</span>
      </span>
      <span v-else-if="hoverRoot" class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 font-mono">
        <span class="text-neutral-800 dark:text-neutral-100">{{ hoverRoot.kind === 'pole' ? 'Pole' : 'Zero' }} {{ formatHz(hoverRoot.singularity.freqHz) }}</span>
        <span v-if="hoverRoot.singularity.q !== null">Q {{ hoverRoot.singularity.q.toFixed(3) }}</span>
        <span>ζ {{ hoverRoot.singularity.zeta.toFixed(3) }}</span>
        <span>σ/2π {{ formatHz(hoverRoot.re) }}</span>
        <span v-if="hoverRoot.im !== 0">jω/2π {{ hoverRoot.im < 0 ? '−' : '' }}{{ formatHz(Math.abs(hoverRoot.im)) }}</span>
        <span v-if="hoverRoot.singularity.isRhp" class="text-amber-600 dark:text-amber-400">right half-plane</span>
      </span>
      <span v-else-if="props.projection === 'PoleZero'">
        {{
          roots.length > 0
            ? 'Hover a pole or zero for its frequency, Q and damping'
            : allRoots.length === 0
              ? 'No fitted roots to plot'
              : 'Every fitted root is outside the measured band'
        }}
      </span>
      <span v-else>Hover the plot for values at a frequency</span>

      <button
        v-if="zoomed"
        type="button"
        class="inline-flex shrink-0 items-center gap-1 rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Back to the window this projection opens on."
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
        @pointerleave="hover = null; hoverRoot = null"
      />
    </div>

    <p v-if="curve.length === 0" class="text-xs text-neutral-500 dark:text-neutral-400">
      <template v-if="traces.length === 0 && overlays.length === 0">
        No response measured yet. Press {{ props.runLabel ?? 'RUN' }}.
      </template>
      <template v-else-if="phaseKind === 'None'">
        This run measured no phase, so it has no complex response to plot. Turn the right-channel
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
