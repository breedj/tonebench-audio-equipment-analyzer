<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { ImpedanceTrace } from '../composables/useImpedance'
import { fitTransform, niceTicks, type Viewport } from '../charts/planeTransform'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { CHART_EXPORT_HEADER_PX, composeChartPng, type ChartExportOptions } from '../charts/exportChart'
import ExpandToggle from './ExpandToggle.vue'
import { useAppTheme } from '../composables/useAppTheme'
import { formatHz } from '../response/format'
import { dbOhmsToOhms, formatOhms } from '../impedance/equivalent'
import { signedOhmTickLabel } from '../impedance/ohmAxis'
import { EXPORT_GRID_WIDTH_SCALE, exportGridStroke } from '../charts/palette'

/**
 * The complex-impedance locus: resistance across, reactance up, one point per measured column.
 *
 * <p><b>A canvas rather than uPlot</b>, for the reason ResponsePolarChart sets out: uPlot wants an
 * ascending x array, and this curve is parametric in frequency — R(f) is not monotonic, and for a
 * loudspeaker it doubles back on itself into a circle. The two things this chart needs beyond a
 * polyline, a nearest-point cursor and a true equal aspect, have to be hand-rolled either way.</p>
 *
 * <p><b>Equal aspect is not decoration here.</b> A resonance traces a circle in the R/X plane, and a
 * circle drawn on unequal axes is an ellipse — which reads as a lossy or asymmetric resonance that
 * the driver does not have. `fitTransform` takes the smaller scale on both axes and widens the
 * viewport rather than cropping it, so nothing is ever hidden to keep the aspect.</p>
 *
 * <p>This is the audio-standard view of a complex impedance, and the reason there is no Smith chart:
 * a Smith chart plots reflection coefficient normalised to a reference impedance, which is a
 * transmission-line idea. At audio frequencies there is no line to reflect off, so it would be a
 * curved grid over the same information.</p>
 */
const props = defineProps<{
  traces: ImpedanceTrace[]
  /** Only points inside the Bode chart's own frequency window are drawn, so the two views agree. */
  xFromHz: number
  xToHz: number
  thickLines: boolean
}>()

const { isDark } = useAppTheme()

const canvas = ref<HTMLCanvasElement | null>(null)
const overlay = ref<HTMLCanvasElement | null>(null)
const container = ref<HTMLDivElement | null>(null)
const root = ref<HTMLDivElement | null>(null)

// Fitted to the view — see charts/useResultChartHeight.ts; set as an inline style on the plot box
// so the ResizeObserver below keeps reading one number whatever changed.
const chartHeight = useResultChartHeight(root, container)
const INSETS = { left: 62, right: 18, top: 16, bottom: 34 }

const CURVE_COLOR = ['#111827', '#f3f4f6'] as const
const AXIS_COLOR = ['#4b5563', '#9ca3af'] as const
const GRID_COLOR = ['#e5e7eb', '#374151'] as const
const ZERO_COLOR = ['#9ca3af', '#6b7280'] as const
const MARKER_COLOR = ['#dc2626', '#f87171'] as const

// Which theme the drawing code reads — the screen's, or light for the duration of an export. Safe as
// module state only because the drawing is synchronous; see ResponsePolarChart's `paint`.
let paintDark = isDark.value
let paintExport = false

function color(pair: readonly string[]): string {
  return paintDark ? pair[1] : pair[0]
}

/**
 * The grid as this paint should draw it. An export is shown scaled down (see charts/palette.ts), so
 * its grid is darkened and widened; on screen both are left exactly as they were.
 */
function gridStroke(): string {
  return paintExport ? exportGridStroke(color(GRID_COLOR)) : color(GRID_COLOR)
}

function gridLineWidth(base = 1): number {
  return paintExport ? base * EXPORT_GRID_WIDTH_SCALE : base
}


interface PlanePoint {
  freqHz: number
  resistanceOhms: number
  reactanceOhms: number
}

const trace = computed(() => props.traces.find((t) => t.kind === 'Impedance') ?? null)

/**
 * The locus, from the column midpoints. The midpoint of min/max rather than the max: the max carries
 * the aggregation bias of a maximum over several bins, which on a closed curve shows up as a lumpy
 * outline rather than as a level offset.
 */
const points = computed<PlanePoint[]>(() => {
  const t = trace.value
  if (!t) return []

  const out: PlanePoint[] = []
  for (let i = 0; i < t.freqHz.length; i++) {
    const freqHz = t.freqHz[i]
    if (freqHz < props.xFromHz || freqHz > props.xToHz) continue

    const ohms = 0.5 * (dbOhmsToOhms(t.magnitudeMinDbOhms[i]) + dbOhmsToOhms(t.magnitudeMaxDbOhms[i]))
    const radians = (t.phaseDeg[i] * Math.PI) / 180

    out.push({
      freqHz,
      resistanceOhms: ohms * Math.cos(radians),
      reactanceOhms: ohms * Math.sin(radians),
    })
  }

  return out
})

/** Null means unzoomed — the natural extent of the data. */
const zoomViewport = ref<Viewport | null>(null)

const dataViewport = computed<Viewport>(() => {
  const p = points.value
  if (p.length === 0) return { xMin: -1, xMax: 1, yMin: -1, yMax: 1 }

  let xMin = Infinity
  let xMax = -Infinity
  let yMin = Infinity
  let yMax = -Infinity

  for (const point of p) {
    xMin = Math.min(xMin, point.resistanceOhms)
    xMax = Math.max(xMax, point.resistanceOhms)
    yMin = Math.min(yMin, point.reactanceOhms)
    yMax = Math.max(yMax, point.reactanceOhms)
  }

  // The X = 0 line is the reference every reading is taken against, so it is always in view even for
  // a curve that never crosses it.
  yMin = Math.min(yMin, 0)
  yMax = Math.max(yMax, 0)

  const padX = Math.max((xMax - xMin) * 0.08, 1e-3)
  const padY = Math.max((yMax - yMin) * 0.08, 1e-3)

  return { xMin: xMin - padX, xMax: xMax + padX, yMin: yMin - padY, yMax: yMax + padY }
})

const viewport = computed<Viewport>(() => zoomViewport.value ?? dataViewport.value)

const hover = ref<PlanePoint | null>(null)

function draw() {
  const el = canvas.value
  const box = container.value
  if (!el || !box) return

  const ratio = window.devicePixelRatio || 1
  const width = box.clientWidth || 800
  const height = box.clientHeight || chartHeight.value

  for (const c of [el, overlay.value]) {
    if (!c) continue
    c.width = Math.round(width * ratio)
    c.height = Math.round(height * ratio)
    c.style.width = `${width}px`
    c.style.height = `${height}px`
  }

  const ctx = el.getContext('2d')
  if (!ctx) return

  ctx.setTransform(ratio, 0, 0, ratio, 0, 0)
  ctx.clearRect(0, 0, width, height)
  renderScene(ctx, width, height, isDark.value)
  drawMarker()
}

/** The hovered point, on its own canvas so a mousemove costs one small redraw rather than the whole plot. */
/** The whole picture — grid, axes, the locus — onto any context, in CSS pixels. */
function renderScene(ctx: CanvasRenderingContext2D, width: number, height: number, dark: boolean, forExport = false) {
  paintDark = dark
  paintExport = forExport

  const transform = fitTransform(viewport.value, width, height, INSETS, true)
  const view = transform.viewport

  ctx.font = '11px ui-monospace, monospace'
  ctx.textBaseline = 'middle'

  // Grid and tick labels.
  ctx.strokeStyle = gridStroke()
  ctx.fillStyle = color(AXIS_COLOR)
  ctx.lineWidth = gridLineWidth()

  for (const x of niceTicks(view.xMin, view.xMax, 6)) {
    const px = transform.toScreenX(x)
    ctx.beginPath()
    ctx.moveTo(px, INSETS.top)
    ctx.lineTo(px, height - INSETS.bottom)
    ctx.stroke()

    ctx.textAlign = 'center'
    ctx.fillText(signedOhmTickLabel(x) ?? '', px, height - INSETS.bottom + 14)
  }

  for (const y of niceTicks(view.yMin, view.yMax, 6)) {
    const py = transform.toScreenY(y)
    ctx.beginPath()
    ctx.moveTo(INSETS.left, py)
    ctx.lineTo(width - INSETS.right, py)
    ctx.stroke()

    ctx.textAlign = 'right'
    ctx.fillText(signedOhmTickLabel(y) ?? '', INSETS.left - 6, py)
  }

  // The X = 0 line, drawn harder than the grid: above it a point is inductive, below it capacitive.
  if (view.yMin < 0 && view.yMax > 0) {
    const py = transform.toScreenY(0)
    ctx.strokeStyle = color(ZERO_COLOR)
    ctx.lineWidth = 1.5
    ctx.beginPath()
    ctx.moveTo(INSETS.left, py)
    ctx.lineTo(width - INSETS.right, py)
    ctx.stroke()
  }

  // Axis labels.
  ctx.fillStyle = color(AXIS_COLOR)
  ctx.textAlign = 'center'
  ctx.fillText('Resistance R (Ω)', (INSETS.left + width - INSETS.right) / 2, height - 8)
  ctx.save()
  ctx.translate(12, (INSETS.top + height - INSETS.bottom) / 2)
  ctx.rotate(-Math.PI / 2)
  ctx.fillText('Reactance X (Ω)', 0, 0)
  ctx.restore()

  // The locus.
  const p = points.value
  if (p.length > 1) {
    ctx.save()
    ctx.beginPath()
    ctx.rect(INSETS.left, INSETS.top, width - INSETS.left - INSETS.right, height - INSETS.top - INSETS.bottom)
    ctx.clip()

    ctx.strokeStyle = color(CURVE_COLOR)
    ctx.lineWidth = props.thickLines ? 2 : 1.25
    ctx.beginPath()
    ctx.moveTo(transform.toScreenX(p[0].resistanceOhms), transform.toScreenY(p[0].reactanceOhms))
    for (let i = 1; i < p.length; i++) {
      ctx.lineTo(transform.toScreenX(p[i].resistanceOhms), transform.toScreenY(p[i].reactanceOhms))
    }
    ctx.stroke()
    ctx.restore()
  }

}

/**
 * The chart as a PNG of exactly the requested size, light whatever the theme — see
 * ResponsePolarChart.exportPng, which explains the arrangement for a canvas chart.
 */
async function exportPng({ width, height }: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header
  const plot = document.createElement('canvas')
  plot.width = width
  plot.height = plotHeight
  const ctx = plot.getContext('2d')
  if (!ctx) throw new Error('Could not get a 2D context for the chart export.')

  try {
    renderScene(ctx, width, plotHeight, false, true)
  } finally {
    paintDark = isDark.value
    paintExport = false
  }

  return composeChartPng({
    width,
    height,
    plotCanvas: plot,
    headerHeight: header,
    title: 'Impedance locus — resistance vs reactance (Ω)',
    legend: [{ label: '|Z| locus', color: CURVE_COLOR[0] }],
  })
}

defineExpose({ exportPng })

function drawMarker() {
  const el = overlay.value
  const box = container.value
  if (!el || !box) return

  const ratio = window.devicePixelRatio || 1
  const width = box.clientWidth || 800
  const height = box.clientHeight || chartHeight.value

  const ctx = el.getContext('2d')
  if (!ctx) return

  ctx.setTransform(ratio, 0, 0, ratio, 0, 0)
  ctx.clearRect(0, 0, width, height)

  const point = hover.value
  if (!point) return

  const transform = fitTransform(viewport.value, width, height, INSETS, true)
  const px = transform.toScreenX(point.resistanceOhms)
  const py = transform.toScreenY(point.reactanceOhms)

  ctx.fillStyle = color(MARKER_COLOR)
  ctx.beginPath()
  ctx.arc(px, py, 4, 0, Math.PI * 2)
  ctx.fill()
}

function onMove(event: MouseEvent) {
  const box = container.value
  if (!box || points.value.length === 0) return

  const rect = box.getBoundingClientRect()
  const width = box.clientWidth || 800
  const height = box.clientHeight || chartHeight.value
  const transform = fitTransform(viewport.value, width, height, INSETS, true)

  const mx = event.clientX - rect.left
  const my = event.clientY - rect.top

  let best: PlanePoint | null = null
  let bestDistance = Infinity
  for (const point of points.value) {
    const dx = transform.toScreenX(point.resistanceOhms) - mx
    const dy = transform.toScreenY(point.reactanceOhms) - my
    const distance = dx * dx + dy * dy
    if (distance < bestDistance) {
      bestDistance = distance
      best = point
    }
  }

  // Only within reach — otherwise the readout names a point nowhere near the pointer.
  hover.value = bestDistance <= 30 * 30 ? best : null
  drawMarker()
}

function onLeave() {
  hover.value = null
  drawMarker()
}

const hoverText = computed(() => {
  const point = hover.value
  if (!point) return null

  const magnitude = Math.hypot(point.resistanceOhms, point.reactanceOhms)
  const kind = point.reactanceOhms > 0 ? 'inductive' : point.reactanceOhms < 0 ? 'capacitive' : 'resistive'

  return {
    freq: formatHz(point.freqHz),
    r: formatOhms(point.resistanceOhms),
    x: formatOhms(point.reactanceOhms),
    magnitude: formatOhms(magnitude),
    kind,
  }
})

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

// The zoom is dropped whenever the data or the frequency window changes: a viewport fitted to the
// previous curve is meaningless over a different one, and leaving it looks like an empty chart.
watch([() => props.traces, () => props.xFromHz, () => props.xToHz], () => {
  zoomViewport.value = null
  hover.value = null
  draw()
})

watch([isDark, () => props.thickLines], () => draw())
</script>

<template>
  <div ref="root" class="relative flex flex-col gap-2">
    <ExpandToggle class="absolute right-0 top-0" :target="root" />
    <div class="min-h-6 pr-7 flex items-start justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
      <span v-if="hoverText" class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 font-mono">
        <span class="text-neutral-800 dark:text-neutral-100">{{ hoverText.freq }}</span>
        <span>R <span class="text-neutral-800 dark:text-neutral-100">{{ hoverText.r }}</span></span>
        <span>X <span class="text-neutral-800 dark:text-neutral-100">{{ hoverText.x }}</span></span>
        <span>|Z| <span class="text-neutral-800 dark:text-neutral-100">{{ hoverText.magnitude }}</span></span>
        <span>{{ hoverText.kind }}</span>
      </span>
      <span v-else>Hover the curve for R, X and |Z| at a frequency</span>
      <span class="shrink-0">Above the line is inductive, below is capacitive</span>
    </div>

    <div
      ref="container"
      class="plot-surface relative w-full"
      :style="{ height: `${chartHeight}px` }"
      @mousemove="onMove"
      @mouseleave="onLeave"
    >
      <canvas ref="canvas" class="absolute inset-0" />
      <canvas ref="overlay" class="pointer-events-none absolute inset-0" />
    </div>

    <p v-if="points.length === 0" class="text-xs text-neutral-500 dark:text-neutral-400">
      No impedance measured yet. Press RUN.
    </p>
  </div>
</template>
