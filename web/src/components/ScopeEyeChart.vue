<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { DecayPalette, ScopeRecordResponse } from '../api/client'
import { useAppTheme } from '../composables/useAppTheme'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { CHART_EXPORT_HEADER_PX, composeChartPng, type ChartExportOptions } from '../charts/exportChart'
import { fitTransform, type PlaneTransform, type Viewport } from '../charts/planeTransform'
import ExpandToggle from './ExpandToggle.vue'
import { formatVolts } from '../scope/voltAxis'
import { formatScopeTime, scopeTimeScale, toChartMs } from '../scope/timeAxis'
import {
  EYE_DIVISIONS,
  EYE_TRACE_ALPHA,
  eyeAbsenceNote,
  eyeNote,
  eyeTruncationNote,
  eyeVoltWindow,
  formatFrequency,
} from '../scope/eye'
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
 * The oscilloscope's eye view — the window folded on the signal's own period, every cycle drawn on
 * top of every other.
 *
 * <p><b>Hand-drawn rather than uPlot, and forced rather than chosen</b>, like the X–Y view: this is
 * two hundred and fifty six traces sharing one x array, and uPlot's cost is per <em>series</em> —
 * each one its own config object, its own path cache and its own pass through the draw pipeline.
 * Two hundred and fifty six of those is a rebuild on every capture. What they are is a bundle of
 * loci over one window, which is what the plane charts here already draw.</p>
 *
 * <p><b>The traces are translucent and that is the measurement.</b> Where every cycle agrees, the
 * strokes land on each other and the line reads solid; where they disagree, the band is as faint as
 * the number of cycles that went through each part of it. An opaque stroke draws a hundred
 * overlapping cycles and one outlier as the same flat line, which is the one thing this picture must
 * not do.</p>
 *
 * <p><b>No annotations and no cursors</b>, for the X–Y view's reason: a note is stored against a
 * view in that view's own quantities, and this one's x axis is a <em>phase of the period</em> rather
 * than a time in the record — a note at 0.3 ms means a different place in the signal the moment the
 * frequency moves.</p>
 */

const props = withDefaults(
  defineProps<{
    record: ScopeRecordResponse | null
    /** The volts axis's scale, over eight divisions — the waveform view's own knob. */
    voltsPerDivision: number
    /** Shifts the volts window, as on the waveform view. */
    voltOffsetV?: number
    /** True while the run is armed or capturing. */
    running?: boolean
    /** How long the overlay is kept. See scope/persistence.ts. */
    persistence?: ScopePersistence
    intensityMode?: ScopeIntensityMode
    intensityPalette?: DecayPalette
    /** The open measurement, so a switch throws the accumulated picture away. */
    persistenceKey?: string | number
  }>(),
  {
    voltOffsetV: 0,
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

const INSETS = { left: 62, right: 18, top: 16, bottom: 36 }

/**
 * The overlay's colour, per captured channel.
 *
 * <p>The channel's own hue rather than a third one, unlike the X–Y figure's: an eye is of
 * <em>one</em> input, which the rail names, so blue really does mean the left input here.</p>
 */
const TRACE_COLORS = [
  ['#2563eb', '#60a5fa'],
  ['#ea580c', '#fb923c'],
] as const
const AXIS_COLOR = ['#4b5563', '#9ca3af'] as const
const GRID_COLOR = ['#e5e7eb', '#374151'] as const
const ZERO_COLOR = ['#9ca3af', '#6b7280'] as const
const SPREAD_COLOR = ['#b45309', '#fbbf24'] as const

function color(pair: readonly string[]): string {
  return isDark.value ? pair[1] : pair[0]
}

const eye = computed(() => props.record?.eye ?? null)

const traceColors = computed(() => TRACE_COLORS[eye.value?.channel === 1 ? 1 : 0])

/** The x axis is one fold, from its start, in whatever unit that span is read in. */
const spanMs = computed(() => toChartMs(eye.value?.foldSeconds ?? 0.001))
const timeScale = computed(() => scopeTimeScale(spanMs.value))

const viewport = computed<Viewport>(() => {
  const volts = eyeVoltWindow(props.voltsPerDivision, props.voltOffsetV)
  const span = eye.value?.foldSeconds ?? 0.001
  return { xMin: 0, xMax: span, yMin: volts.min, yMax: volts.max }
})

const absence = computed(() =>
  eyeAbsenceNote(
    eye.value,
    props.record !== null,
    props.record?.channels.find((c) => c.measurements.frequencyHz != null)?.measurements.frequencyHz ?? null,
  ),
)
const truncationNote = computed(() => (eye.value ? eyeTruncationNote(eye.value) : null))
const summary = computed(() => (eye.value ? eyeNote(eye.value) : null))

const hover = ref<string | null>(null)

// ---------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------

let intensity: ScopeIntensityBuffer | null = null
let intensityPixelCache: Uint8ClampedArray<ArrayBuffer> | undefined
let intensityFor = ''
let accumulatedRecord: ScopeRecordResponse | null = null
let scratch: HTMLCanvasElement | null = null

function plotBox(width: number, height: number) {
  const left = INSETS.left
  const top = INSETS.top
  const plotWidth = Math.max(1, Math.floor(width - INSETS.left - INSETS.right))
  const plotHeight = Math.max(1, Math.floor(height - INSETS.top - INSETS.bottom))
  return { left, top, width: plotWidth, height: plotHeight }
}

function currentSignature(width: number, height: number): string {
  const v = viewport.value
  const box = plotBox(width, height)
  return intensitySignature(
    box.width,
    box.height,
    v.xMin,
    v.xMax,
    v.yMin,
    v.yMax,
    `${props.persistenceKey}:${props.persistence}:${eye.value?.foldFrameCount ?? 0}`,
  )
}

function resetIntensity() {
  intensity = null
  intensityPixelCache = undefined
  intensityFor = ''
  accumulatedRecord = null
}

/**
 * Folds the overlay on screen into the accumulated picture.
 *
 * <p>Across records, like every other persistence surface here: within one record the folds are
 * already drawn over each other, so what this adds is how the <em>whole overlay</em> moved from
 * capture to capture — which is where an intermittent fault lives.</p>
 */
function accumulate(transform: PlaneTransform, width: number, height: number) {
  const figure = eye.value
  if (!figure || !persistenceDraws(props.persistence)) return
  if (props.record === accumulatedRecord) return

  const box = plotBox(width, height)
  const signature = currentSignature(width, height)
  if (!intensity || intensityFor !== signature) {
    intensity = new ScopeIntensityBuffer(box.width, box.height)
    intensityPixelCache = undefined
    intensityFor = signature
  }

  intensity.decay(persistenceDecay(props.persistence))

  const dx = figure.foldFrameCount > 1 ? figure.foldSeconds / (figure.foldFrameCount - 1) : 0
  for (const fold of figure.folds) {
    let px = 0
    let py = 0
    for (let i = 0; i < fold.length; i++) {
      const cx = transform.toScreenX(i * dx) - box.left
      const cy = transform.toScreenY(fold[i]) - box.top
      if (i > 0) intensity.addLine(px, py, cx, cy)
      else intensity.addLine(cx, cy, cx, cy)
      px = cx
      py = cy
    }
  }

  intensity.renormalise()
  accumulatedRecord = props.record
}

function paintIntensity(ctx: CanvasRenderingContext2D, box: { left: number; top: number; width: number; height: number }) {
  const buffer = intensity
  if (!buffer || !persistenceDraws(props.persistence) || buffer.peak <= 0) return

  intensityPixelCache = intensityPixels(
    buffer,
    props.intensityMode,
    props.intensityPalette,
    hexRgb(traceColors.value[0]),
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

function transformFor(width: number, height: number): PlaneTransform {
  const box = plotBox(width, height)
  return fitTransform(
    viewport.value,
    width,
    height,
    {
      left: box.left,
      right: Math.max(0, width - (box.left + box.width)),
      top: box.top,
      bottom: Math.max(0, height - (box.top + box.height)),
    },
    // Not equal aspect: the two axes are different quantities here, where the goniometer's are
    // both volts and its shape is the measurement.
    false,
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

  renderScene(ctx, width, height, false)
}

function renderScene(ctx: CanvasRenderingContext2D, width: number, height: number, forExport: boolean) {
  const t = transformFor(width, height)
  const v = t.viewport
  const ink = forExport ? AXIS_COLOR[0] : color(AXIS_COLOR)
  const grid = forExport ? GRID_COLOR[0] : color(GRID_COLOR)
  const zero = forExport ? ZERO_COLOR[0] : color(ZERO_COLOR)
  const spreadInk = forExport ? SPREAD_COLOR[0] : color(SPREAD_COLOR)
  const trace = forExport ? traceColors.value[0] : color(traceColors.value)

  const box = plotBox(width, height)
  const left = box.left
  const top = box.top
  const right = box.left + box.width
  const bottom = box.top + box.height

  // The graticule: eight divisions down, matching the V/div knob, and ten across, which is a
  // scope's own horizontal count. Not eight by eight -- the axes are different quantities here.
  ctx.save()
  ctx.strokeStyle = grid
  ctx.lineWidth = 1
  ctx.beginPath()
  for (let i = 0; i <= EYE_DIVISIONS; i++) {
    const y = top + ((bottom - top) * i) / EYE_DIVISIONS
    ctx.moveTo(left, Math.round(y) + 0.5)
    ctx.lineTo(right, Math.round(y) + 0.5)
  }
  for (let i = 0; i <= 10; i++) {
    const x = left + ((right - left) * i) / 10
    ctx.moveTo(Math.round(x) + 0.5, top)
    ctx.lineTo(Math.round(x) + 0.5, bottom)
  }
  ctx.stroke()
  ctx.restore()

  if (v.yMin < 0 && v.yMax > 0) {
    ctx.save()
    ctx.strokeStyle = zero
    ctx.lineWidth = 1
    ctx.beginPath()
    ctx.moveTo(left, Math.round(t.toScreenY(0)) + 0.5)
    ctx.lineTo(right, Math.round(t.toScreenY(0)) + 0.5)
    ctx.stroke()
    ctx.restore()
  }

  if (!forExport) paintIntensity(ctx, box)

  const figure = eye.value
  if (figure) {
    ctx.save()
    ctx.beginPath()
    ctx.rect(left, top, right - left, bottom - top)
    ctx.clip()

    const dx = figure.foldFrameCount > 1 ? figure.foldSeconds / (figure.foldFrameCount - 1) : 0
    const path = new Path2D()
    for (const fold of figure.folds) {
      for (let i = 0; i < fold.length; i++) {
        const px = t.toScreenX(i * dx)
        const py = t.toScreenY(fold[i])
        if (i === 0) path.moveTo(px, py)
        else path.lineTo(px, py)
      }
    }
    ctx.strokeStyle = trace
    ctx.lineWidth = 1
    // Translucent, and this is the reading rather than the styling: where every cycle agrees the
    // strokes land on each other and read solid, and where they do not the band is as faint as the
    // number of cycles through it. One opaque stroke draws a hundred cycles and one outlier alike.
    ctx.globalAlpha = EYE_TRACE_ALPHA
    ctx.stroke(path)
    ctx.restore()

    // Where the band is widest, as a dashed vertical: the figure beside it says how wide, and this
    // says at which phase -- which is what tells amplitude variation from timing.
    ctx.save()
    ctx.strokeStyle = spreadInk
    ctx.setLineDash([4, 3])
    ctx.lineWidth = 1
    ctx.beginPath()
    const x = Math.round(t.toScreenX(Math.min(figure.foldSeconds, Math.max(0, figure.spreadAtS)))) + 0.5
    ctx.moveTo(x, top)
    ctx.lineTo(x, bottom)
    ctx.stroke()
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
  for (let i = 0; i <= EYE_DIVISIONS; i++) {
    const value = v.yMax - ((v.yMax - v.yMin) * i) / EYE_DIVISIONS
    ctx.fillText(formatVolts(value, props.voltsPerDivision), left - 6, top + ((bottom - top) * i) / EYE_DIVISIONS)
  }

  const scale = timeScale.value
  const spanMs = toChartMs(v.xMax - v.xMin)
  ctx.textAlign = 'center'
  ctx.textBaseline = 'top'
  for (let i = 0; i <= 10; i += 2) {
    const seconds = v.xMin + ((v.xMax - v.xMin) * i) / 10
    ctx.fillText(
      formatScopeTime(toChartMs(seconds), spanMs, false),
      left + ((right - left) * i) / 10,
      bottom + 6,
    )
  }

  ctx.fillText(`Phase of one period (${scale.label})`, (left + right) / 2, bottom + 21)
  ctx.save()
  ctx.translate(14, (top + bottom) / 2)
  ctx.rotate(-Math.PI / 2)
  ctx.textBaseline = 'middle'
  ctx.textAlign = 'center'
  ctx.fillText(figure?.channel === 1 ? 'Right input (V)' : 'Left input (V)', 0, 0)
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
  if (!ctx) throw new Error('There is no eye diagram to export.')
  ctx.fillStyle = '#ffffff'
  ctx.fillRect(0, 0, width, plotHeight)
  renderScene(ctx, width, plotHeight, true)

  const figure = eye.value
  const detail = figure ? ` — ${figure.foldCount} cycles at ${formatFrequency(figure.periodHz)}` : ''
  return await composeChartPng({
    width,
    height,
    plotCanvas: off,
    headerHeight: header,
    title: `Oscilloscope · Eye${detail}`,
    legend: [
      { label: figure?.channel === 1 ? 'Right input' : 'Left input', color: TRACE_COLORS[figure?.channel === 1 ? 1 : 0][0] },
      { label: 'Widest spread', color: SPREAD_COLOR[0] },
    ],
  })
}

defineExpose({ exportPng })

// ---------------------------------------------------------------------------
// Hover
// ---------------------------------------------------------------------------

function onMove(event: MouseEvent) {
  const c = canvas.value
  if (!c) return
  const rect = c.getBoundingClientRect()
  const t = transformFor(rect.width, rect.height)
  const px = event.clientX - rect.left
  const py = event.clientY - rect.top
  const box = plotBox(rect.width, rect.height)
  if (px < box.left || px > box.left + box.width || py < box.top || py > box.top + box.height) {
    hover.value = null
    return
  }
  hover.value = `${formatScopeTime(toChartMs(t.toWorldX(px)), spanMs.value)} · ${formatVolts(t.toWorldY(py), props.voltsPerDivision)}`
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
watch(
  () => [props.voltsPerDivision, props.voltOffsetV, isDark.value, chartHeight.value] as const,
  () => draw(),
)
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
        <span class="font-medium text-neutral-700 dark:text-neutral-200">Eye</span>
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
      <canvas ref="canvas" class="plot-surface w-full rounded" @mousemove="onMove" @mouseleave="onLeave"></canvas>
      <p
        v-if="absence"
        class="pointer-events-none absolute inset-0 flex items-center justify-center px-6 text-center text-xs text-neutral-400 dark:text-neutral-600"
      >
        {{ running ? 'Waiting for a record…' : absence }}
      </p>
    </div>

    <p v-if="truncationNote" class="text-[11px] leading-snug text-amber-600 dark:text-amber-400">
      {{ truncationNote }}
    </p>
  </div>
</template>
