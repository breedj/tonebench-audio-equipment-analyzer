<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { ScopeRecordResponse } from '../api/client'
import type { DecayPalette } from '../api/client'
import { useAppTheme } from '../composables/useAppTheme'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { CHART_EXPORT_HEADER_PX, composeChartPng, type ChartExportOptions } from '../charts/exportChart'
import { fitTransform, type PlaneTransform, type Viewport } from '../charts/planeTransform'
import ExpandToggle from './ExpandToggle.vue'
import { formatVolts } from '../scope/voltAxis'
import {
  correlationDescription,
  formatCorrelation,
  xyCursorText,
  xyPlotBox,
  xyTruncationNote,
  xyWindow,
} from '../scope/xy'
import {
  hexRgb,
  intensityPixels,
  intensitySignature,
  persistenceDecay,
  persistenceDraws,
  ScopeIntensityBuffer,
  type ScopeIntensityMode,
  type ScopePersistence,
} from '../scope/persistence'

/**
 * The oscilloscope's X–Y view — the left input against the right, with time thrown away.
 *
 * <p><b>Hand-drawn rather than uPlot, and that is forced rather than chosen.</b> uPlot plots a
 * function of x: one y per x, in increasing x. A Lissajous figure is neither — it revisits the
 * same x at several y, and it runs backwards half the time. What it is is a <em>locus</em>, which
 * is what the three plane charts here already draw, so this follows them and takes
 * <code>fitTransform</code>'s equal-aspect mapping with it.</p>
 *
 * <p><b>Equal aspect is the whole picture.</b> The first reading anyone takes off a goniometer is
 * whether the line sits at 45°, and unequal pixels per volt tilt that line by an amount no axis
 * label reveals — so a matched pair would read as a mismatched one. Both axes therefore take the
 * display rail's one V/div over eight divisions, and the plot area is square.</p>
 *
 * <p><b>No annotations, deliberately.</b> A measurement's notes are stored against a view and in
 * that view's own quantities, and this one's are volts against volts where the waveform view's are
 * milliseconds against volts. Sharing the waveform's view id would mean every note it holds is
 * skipped here on a quantity mismatch, which is a list of notes that cannot be drawn; a view of its
 * own is a second entry in the annotation and report machinery that nothing has asked for yet.</p>
 */

const props = withDefaults(
  defineProps<{
    record: ScopeRecordResponse | null
    /** Both axes' scale: one V/div over eight divisions, so the plot is square. */
    voltsPerDivision: number
    /** True while the run is armed or capturing. */
    running?: boolean
    /** How long the figure is kept. See scope/persistence.ts — this is where it pays most. */
    persistence?: ScopePersistence
    intensityMode?: ScopeIntensityMode
    intensityPalette?: DecayPalette
    /** The open measurement, so a switch throws the accumulated figure away. */
    persistenceKey?: string | number
  }>(),
  {
    running: false,
    persistence: 'Off',
    intensityMode: 'Single',
    intensityPalette: 'Jet',
    persistenceKey: 0,
  },
)

const { isDark } = useAppTheme()

const root = ref<HTMLDivElement | null>(null)
const container = ref<HTMLDivElement | null>(null)
const canvas = ref<HTMLCanvasElement | null>(null)
const chartHeight = useResultChartHeight(root, container)

const INSETS = { left: 62, right: 18, top: 16, bottom: 34 }

/**
 * The locus's colour.
 *
 * <p>A third hue rather than either channel's, for the math trace's reason: the figure belongs to
 * neither input, and painting it blue would read as the left one's.</p>
 */
const TRACE_COLOR = ['#0f766e', '#2dd4bf'] as const
const AXIS_COLOR = ['#4b5563', '#9ca3af'] as const
const GRID_COLOR = ['#e5e7eb', '#374151'] as const
const ZERO_COLOR = ['#9ca3af', '#6b7280'] as const
const GUIDE_COLOR = ['#cbd5e1', '#475569'] as const

function color(pair: readonly string[]): string {
  return isDark.value ? pair[1] : pair[0]
}

const xy = computed(() => props.record?.xy ?? null)

const viewport = computed<Viewport>(() => {
  const w = xyWindow(props.voltsPerDivision)
  return { xMin: w.min, xMax: w.max, yMin: w.min, yMax: w.max }
})

const truncationNote = computed(() => xyTruncationNote(xy.value))

const summary = computed(() => {
  const figure = xy.value
  if (!figure) return null
  return `r = ${formatCorrelation(figure.correlation)} · ${correlationDescription(figure.correlation)}`
})

const hover = ref<string | null>(null)

// ---------------------------------------------------------------------------
// Persistence — the same accumulator the waveform view uses
// ---------------------------------------------------------------------------

/**
 * The accumulated figure, in CSS pixels of the plot area.
 *
 * <p><b>This is where persistence pays most.</b> A single record's locus is a thin loop; a
 * goniometer is read as a <em>density</em>, which is what makes a wandering phase or an
 * intermittent channel visible at all. The buffer is the waveform view's own, and the figure is
 * accumulated as <em>lines</em> between consecutive pairs rather than as points, because at any
 * usable sample rate the pairs are pixels apart wherever the locus moves quickly.</p>
 */
let intensity: ScopeIntensityBuffer | null = null
let intensityPixelCache: Uint8ClampedArray<ArrayBuffer> | undefined
let intensityFor = ''
let accumulatedRecord: ScopeRecordResponse | null = null
let scratch: HTMLCanvasElement | null = null

function currentSignature(width: number, height: number): string {
  const v = viewport.value
  const box = xyPlotBox(width, height, INSETS)
  return intensitySignature(
    box.size,
    box.size,
    v.xMin,
    v.xMax,
    v.yMin,
    v.yMax,
    `${props.persistenceKey}:${props.persistence}`,
  )
}

function resetIntensity() {
  intensity = null
  intensityPixelCache = undefined
  intensityFor = ''
  accumulatedRecord = null
}

/**
 * Folds the figure on screen into the accumulated picture.
 *
 * <p>Called when a record arrives rather than from the draw, for the waveform view's reason: a
 * redraw happens for a dozen reasons and counting on any of them would make the brightness a
 * reading of how often the chart repainted.</p>
 */
function accumulate(transform: PlaneTransform, width: number, height: number) {
  const figure = xy.value
  if (!figure || !persistenceDraws(props.persistence)) return
  if (props.record === accumulatedRecord) return

  const box = xyPlotBox(width, height, INSETS)
  const signature = currentSignature(width, height)
  if (!intensity || intensityFor !== signature) {
    intensity = new ScopeIntensityBuffer(box.size, box.size)
    intensityPixelCache = undefined
    intensityFor = signature
  }

  intensity.decay(persistenceDecay(props.persistence))

  const n = Math.min(figure.x.length, figure.y.length)
  let px = 0
  let py = 0
  for (let i = 0; i < n; i++) {
    // Relative to the plot box, which is what a buffer cell is addressed in.
    const cx = transform.toScreenX(figure.x[i]) - box.left
    const cy = transform.toScreenY(figure.y[i]) - box.top
    if (i > 0) intensity.addLine(px, py, cx, cy)
    else intensity.addLine(cx, cy, cx, cy)
    px = cx
    py = cy
  }

  intensity.renormalise()
  accumulatedRecord = props.record
}

function paintIntensity(ctx: CanvasRenderingContext2D, box: { left: number; top: number; size: number }) {
  const buffer = intensity
  if (!buffer || !persistenceDraws(props.persistence) || buffer.peak <= 0) return

  intensityPixelCache = intensityPixels(
    buffer,
    props.intensityMode,
    props.intensityPalette,
    hexRgb(color(TRACE_COLOR)),
    intensityPixelCache,
  )

  if (!scratch) scratch = document.createElement('canvas')
  if (scratch.width !== buffer.width || scratch.height !== buffer.height) {
    scratch.width = buffer.width
    scratch.height = buffer.height
  }
  const scratchCtx = scratch.getContext('2d')
  if (!scratchCtx) return
  scratchCtx.putImageData(new ImageData(intensityPixelCache, buffer.width, buffer.height), 0, 0)

  ctx.save()
  ctx.imageSmoothingEnabled = false
  ctx.drawImage(scratch, box.left, box.top, buffer.width, buffer.height)
  ctx.restore()
}

// ---------------------------------------------------------------------------
// Drawing
// ---------------------------------------------------------------------------

/**
 * The world-to-pixel mapping, over the square box.
 *
 * <p>Equal aspect is asked for as well, but by then it is a no-op — a square window in a square
 * box — which is the point: it is passed so a future change to either cannot silently make the two
 * scales differ, where the shape <b>is</b> the measurement.</p>
 */
function transformFor(width: number, height: number): PlaneTransform {
  const box = xyPlotBox(width, height, INSETS)
  return fitTransform(
    viewport.value,
    width,
    height,
    {
      left: box.left,
      right: Math.max(0, width - (box.left + box.size)),
      top: box.top,
      bottom: Math.max(0, height - (box.top + box.size)),
    },
    true,
  )
}

function draw() {
  const c = canvas.value
  const host = container.value
  if (!c || !host) return

  const width = host.getBoundingClientRect().width || 600
  const height = chartHeight.value
  const dpr = window.devicePixelRatio || 1

  c.width = Math.round(width * dpr)
  c.height = Math.round(height * dpr)
  c.style.width = `${width}px`
  c.style.height = `${height}px`

  const ctx = c.getContext('2d')
  if (!ctx) return
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  ctx.clearRect(0, 0, width, height)

  renderScene(ctx, width, height, isDark.value)
}

function renderScene(
  ctx: CanvasRenderingContext2D,
  width: number,
  height: number,
  dark: boolean,
  forExport = false,
) {
  const t = transformFor(width, height)
  const v = t.viewport
  const ink = forExport ? AXIS_COLOR[0] : color(AXIS_COLOR)
  const grid = forExport ? GRID_COLOR[0] : color(GRID_COLOR)
  const zero = forExport ? ZERO_COLOR[0] : color(ZERO_COLOR)
  const guide = forExport ? GUIDE_COLOR[0] : color(GUIDE_COLOR)
  const trace = forExport ? TRACE_COLOR[0] : color(TRACE_COLOR)
  void dark

  const box = xyPlotBox(width, height, INSETS)
  const left = box.left
  const top = box.top
  const right = box.left + box.size
  const bottom = box.top + box.size

  // The graticule, eight by eight, so a division means the same on both axes.
  ctx.save()
  ctx.strokeStyle = grid
  ctx.lineWidth = 1
  ctx.beginPath()
  for (let i = 0; i <= 8; i++) {
    const x = left + ((right - left) * i) / 8
    const y = top + ((bottom - top) * i) / 8
    ctx.moveTo(Math.round(x) + 0.5, top)
    ctx.lineTo(Math.round(x) + 0.5, bottom)
    ctx.moveTo(left, Math.round(y) + 0.5)
    ctx.lineTo(right, Math.round(y) + 0.5)
  }
  ctx.stroke()
  ctx.restore()

  // The two 45 degree guides a reader lines the figure up against: in phase runs up to the right,
  // out of phase up to the left. Drawn as the world lines y = x and y = -x rather than as the
  // box's corners -- the same thing only because the box is square, which is why it is made so.
  const h = Math.min(Math.abs(v.xMin), Math.abs(v.yMin), v.xMax, v.yMax)
  ctx.save()
  ctx.strokeStyle = guide
  ctx.setLineDash([4, 4])
  ctx.lineWidth = 1
  ctx.beginPath()
  ctx.moveTo(t.toScreenX(-h), t.toScreenY(-h))
  ctx.lineTo(t.toScreenX(h), t.toScreenY(h))
  ctx.moveTo(t.toScreenX(-h), t.toScreenY(h))
  ctx.lineTo(t.toScreenX(h), t.toScreenY(-h))
  ctx.stroke()
  ctx.restore()

  ctx.save()
  ctx.strokeStyle = zero
  ctx.lineWidth = 1
  ctx.beginPath()
  ctx.moveTo(left, Math.round(t.toScreenY(0)) + 0.5)
  ctx.lineTo(right, Math.round(t.toScreenY(0)) + 0.5)
  ctx.moveTo(Math.round(t.toScreenX(0)) + 0.5, top)
  ctx.lineTo(Math.round(t.toScreenX(0)) + 0.5, bottom)
  ctx.stroke()
  ctx.restore()

  // The accumulated figure, under the live one — the waveform view's own order, so the newest
  // record is the brightest thing on the chart and the haze is everything before it.
  if (!forExport) paintIntensity(ctx, box)

  const figure = xy.value
  if (figure) {
    ctx.save()
    ctx.beginPath()
    ctx.rect(left, top, right - left, bottom - top)
    ctx.clip()

    const path = new Path2D()
    const n = Math.min(figure.x.length, figure.y.length)
    for (let i = 0; i < n; i++) {
      const px = t.toScreenX(figure.x[i])
      const py = t.toScreenY(figure.y[i])
      if (i === 0) path.moveTo(px, py)
      else path.lineTo(px, py)
    }
    ctx.strokeStyle = trace
    ctx.lineWidth = 1
    // Translucent, because the locus retraces itself every period: an opaque stroke draws a
    // thousand overlapping passes as one flat loop, where this lets the density show.
    ctx.globalAlpha = 0.55
    ctx.stroke(path)
    ctx.restore()
  }

  // The frame and the labels.
  ctx.save()
  ctx.strokeStyle = ink
  ctx.lineWidth = 1
  ctx.strokeRect(left + 0.5, top + 0.5, right - left - 1, bottom - top - 1)

  ctx.fillStyle = ink
  ctx.font = '11px system-ui, sans-serif'
  ctx.textAlign = 'right'
  ctx.textBaseline = 'middle'
  for (let i = 0; i <= 8; i++) {
    const value = v.yMax - ((v.yMax - v.yMin) * i) / 8
    ctx.fillText(formatVolts(value, props.voltsPerDivision), left - 6, top + ((bottom - top) * i) / 8)
  }

  ctx.textAlign = 'center'
  ctx.textBaseline = 'top'
  for (let i = 0; i <= 8; i += 2) {
    const value = v.xMin + ((v.xMax - v.xMin) * i) / 8
    ctx.fillText(formatVolts(value, props.voltsPerDivision), left + ((right - left) * i) / 8, bottom + 6)
  }

  ctx.textAlign = 'center'
  ctx.fillText('Left input (X)', (left + right) / 2, bottom + 20)
  ctx.save()
  ctx.translate(14, (top + bottom) / 2)
  ctx.rotate(-Math.PI / 2)
  ctx.textBaseline = 'middle'
  ctx.fillText('Right input (Y)', 0, 0)
  ctx.restore()
  ctx.restore()
}

async function exportPng({ width, height }: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header

  const off = document.createElement('canvas')
  off.width = width
  off.height = plotHeight
  const ctx = off.getContext('2d')
  if (!ctx) throw new Error('There is no X–Y figure to export.')
  ctx.fillStyle = '#ffffff'
  ctx.fillRect(0, 0, width, plotHeight)
  renderScene(ctx, width, plotHeight, false, true)

  const figure = xy.value
  const detail = figure ? ` — r = ${formatCorrelation(figure.correlation)}` : ''
  return await composeChartPng({
    width,
    height,
    plotCanvas: off,
    headerHeight: header,
    title: `Oscilloscope · X–Y${detail}`,
    legend: [{ label: 'Left against right', color: TRACE_COLOR[0] }],
  })
}

defineExpose({ exportPng })

// ---------------------------------------------------------------------------
// Hover
// ---------------------------------------------------------------------------

function onMove(event: MouseEvent) {
  const c = canvas.value
  const host = container.value
  if (!c || !host) return
  const rect = c.getBoundingClientRect()
  const width = rect.width
  const height = rect.height
  const t = transformFor(width, height)
  const px = event.clientX - rect.left
  const py = event.clientY - rect.top
  const box = xyPlotBox(width, height, INSETS)
  if (px < box.left || px > box.left + box.size || py < box.top || py > box.top + box.size) {
    hover.value = null
    return
  }
  hover.value = xyCursorText(t.toWorldX(px), t.toWorldY(py), props.voltsPerDivision)
}

function onLeave() {
  hover.value = null
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

let observer: ResizeObserver | null = null

function redrawWithAccumulation() {
  const host = container.value
  if (!host) return
  const width = host.getBoundingClientRect().width || 600
  accumulate(transformFor(width, chartHeight.value), width, chartHeight.value)
  draw()
}

onMounted(() => {
  draw()
  observer = new ResizeObserver(() => draw())
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
})

watch(() => props.record, () => redrawWithAccumulation())
watch(() => [props.voltsPerDivision, isDark.value, chartHeight.value] as const, () => draw())
watch(
  () => [props.persistence, props.intensityMode, props.intensityPalette] as const,
  ([mode]) => {
    if (!persistenceDraws(mode)) resetIntensity()
    draw()
  },
)
watch(
  () => props.record === null,
  (empty) => {
    if (empty) resetIntensity()
  },
)
</script>

<template>
  <div ref="root" class="panel-surface flex flex-col gap-1 rounded-xl p-3">
    <div class="flex min-h-6 items-center justify-between gap-2">
      <div class="flex items-center gap-2 text-xs text-neutral-500 dark:text-neutral-400">
        <span class="font-medium text-neutral-700 dark:text-neutral-200">X–Y</span>
        <span v-if="summary">{{ summary }}</span>
        <span v-if="truncationNote" class="rounded bg-amber-500/15 px-1.5 py-0.5 text-amber-700 dark:text-amber-300">
          truncated
        </span>
      </div>
      <div class="flex items-center gap-2 text-xs text-neutral-500 dark:text-neutral-400">
        <span v-if="hover" class="font-mono">{{ hover }}</span>
        <ExpandToggle :target="root" />
      </div>
    </div>

    <div ref="container" class="relative w-full">
      <canvas
        ref="canvas"
        class="plot-surface w-full rounded"
        @mousemove="onMove"
        @mouseleave="onLeave"
      ></canvas>
      <p
        v-if="!xy"
        class="pointer-events-none absolute inset-0 flex items-center justify-center text-xs text-neutral-400 dark:text-neutral-600"
      >
        {{ running ? 'Waiting for a record…' : 'Capture both inputs to draw an X–Y figure.' }}
      </p>
    </div>

    <p v-if="truncationNote" class="text-[11px] leading-snug text-amber-600 dark:text-amber-400">
      {{ truncationNote }}
    </p>
  </div>
</template>
