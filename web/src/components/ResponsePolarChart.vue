<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { OverlayEntry } from '../composables/useMeasurements'
import type { ResponsePhaseKindName } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { responseOverlayDash } from '../measurements/sweepOverlayStyles'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import ExpandToggle from './ExpandToggle.vue'
import { fitTransform, niceTicks, type PlaneTransform, type Viewport } from '../charts/planeTransform'
import { toComplexCurve, type ComplexPoint } from '../response/complexPlane'
import { stabilityMargins } from '../response/margins'
import { evaluateModel, type RationalModel } from '../response/rationalFit'
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
 * The complex-plane views of a response measurement: Nyquist (Re/Im) and Nichols (phase/gain).
 *
 * Hand-drawn on a canvas rather than built on uPlot, and that is forced rather than chosen. uPlot
 * wants an ascending x array — `FrequencyResponseChart` gives it log frequency and its cursor
 * indexes `u.data[0]` by x — while both of these curves are *parametric* in frequency: Re(f) is not
 * monotonic, and neither is unwrapped phase. A custom `paths` builder can draw the polyline, but the
 * two things this chart actually needs beyond that — a nearest-point cursor and a true equal aspect
 * — have to be hand-rolled either way, which leaves a uPlot instance with a fake x axis and its own
 * cursor. So: a canvas.
 *
 * One component for both projections. They differ in exactly three things — the point map, the
 * decorations, and whether the aspect is locked — and share the polyline, the overlays, the hover
 * readout, the zoom and the theme.
 */
const props = defineProps<{
  traces: ResponseTrace[]
  overlays: OverlayEntry[]
  projection: 'Nyquist' | 'Nichols'
  radiusMode: NyquistRadiusMode
  radiusFloorDb: number
  /** Adds 180°: the summing-junction inversion the measurement cannot see. */
  invertSign: boolean
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
   * The fitted rational model, drawn over the measurement as a dashed curve. This is what makes the
   * pole/zero table falsifiable: a list of roots whose model does not track the measured contour is
   * a description of nothing, and only the residual shows it.
   */
  fittedModel?: RationalModel | null
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

function color(pair: readonly string[]): string {
  return isDark.value ? pair[1] : pair[0]
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

function toPlane(point: ComplexPoint): PlanePoint {
  return props.projection === 'Nyquist'
    ? nyquistPoint(point, props.radiusMode, props.radiusFloorDb)
    : nicholsPoint(point)
}

const equalAspect = computed(() => props.projection === 'Nyquist')

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

  transform = fitTransform(zoomViewport.value ?? baseViewport(), width, height, INSETS, equalAspect.value)

  drawGrid(ctx, width, height, transform)
  drawDecorations(ctx, transform)
  for (const overlay of overlayCurves.value) {
    drawCurve(ctx, overlay.points, transform, {
      stroke: color(CURVE_COLOR),
      width: 1,
      dash: responseOverlayDash(props.overlays, overlay.id) ?? [6, 3],
      alpha: 0.7,
      points: false,
    })
  }
  drawCurve(ctx, curve.value, transform, {
    stroke: color(CURVE_COLOR),
    width: props.thickLines ? 2 : 1,
    dash: [],
    alpha: 1,
    points: props.showDataPoints,
  })
  drawFittedModel(ctx, transform)
  drawCrossings(ctx, transform)
  drawHover(ctx, transform)

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

  // Axis captions, so the two projections are never confused for one another.
  ctx.textAlign = 'center'
  ctx.textBaseline = 'bottom'
  ctx.fillText(
    props.projection === 'Nyquist' ? 'Re' : 'Phase (°)',
    (INSETS.left + width - INSETS.right) / 2,
    height - 2,
  )
  ctx.restore()
}

function tickLabel(v: number): string {
  if (v === 0) return '0'
  const abs = Math.abs(v)
  if (abs >= 1000 || abs < 0.01) return v.toExponential(0)
  return String(Number(v.toPrecision(3)))
}

function drawDecorations(ctx: CanvasRenderingContext2D, t: PlaneTransform) {
  const el = canvas.value
  if (!el) return
  const dpr = window.devicePixelRatio || 1
  const width = el.width / dpr
  const height = el.height / dpr

  ctx.save()
  clipToPlot(ctx, width, height)

  if (props.projection === 'Nyquist') {
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
  const el = canvas.value
  if (!el) return
  const dpr = window.devicePixelRatio || 1

  ctx.save()
  clipToPlot(ctx, el.width / dpr, el.height / dpr)
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
  const model = props.fittedModel
  const measured = curve.value
  if (!model || measured.length < 2) return
  const el = canvas.value
  if (!el) return
  const dpr = window.devicePixelRatio || 1

  const lowHz = measured[0].freqHz
  const highHz = measured[measured.length - 1].freqHz
  if (!(lowHz > 0) || !(highHz > lowHz)) return

  const steps = 400
  const ratio = Math.log(highHz / lowHz)

  ctx.save()
  clipToPlot(ctx, el.width / dpr, el.height / dpr)
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
  const el = canvas.value
  if (!el) return
  const dpr = window.devicePixelRatio || 1
  const { gainCrossoverHz, phaseCrossoverHz } = margins.value

  ctx.save()
  clipToPlot(ctx, el.width / dpr, el.height / dpr)
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
  hover.value = nearestPoint(point.x, point.y)
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
    props.fittedModel,
    isDark.value,
  ],
  () => draw(),
  { deep: true },
)

defineExpose({ margins, curve })
</script>

<template>
  <div ref="root" class="relative flex flex-col gap-2">
    <ExpandToggle class="absolute right-0 top-0" :target="root" />
    <div class="pr-7 flex items-start justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
      <span v-if="hover" class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 font-mono">
        <span class="text-neutral-800 dark:text-neutral-100">{{ formatHz(hover.freqHz) }}</span>
        <span>{{ hover.magDb.toFixed(2) }} dB</span>
        <span>{{ hover.phaseDeg.toFixed(1) }}°</span>
        <span>Re {{ hover.re.toFixed(4) }}</span>
        <span>Im {{ hover.im.toFixed(4) }}</span>
      </span>
      <span v-else>Hover the plot for values at a frequency</span>

      <button
        v-if="zoomed"
        type="button"
        class="shrink-0 rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Back to the window this projection opens on."
        @click="resetZoom"
      >
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
        @pointerleave="hover = null"
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
