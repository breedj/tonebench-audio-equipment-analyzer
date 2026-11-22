<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { ScopeChannelSlice, ScopeRecordResponse } from '../api/client'
import { useAppTheme } from '../composables/useAppTheme'
import {
  MIN_STRIP_BOX_PX,
  stripBox,
  stripCentreOn,
  stripCoversAll,
  stripDrag,
  stripGrabAt,
  stripSeconds,
  type StripGeometry,
  type StripGrab,
} from '../scope/overviewStrip'
import type { ScopeTimeWindow } from '../scope/timeAxis'

/**
 * The zoom bar: the <b>whole record</b> drawn small, with a draggable box showing which part of it
 * the chart below is looking at.
 *
 * <p><b>A persistent navigator, not the chart's own drag-zoom.</b> The two write one window — the
 * display panel's pair — so dragging the box, dragging the plot and pressing Back all move the same
 * value and the box always shows where the chart really is. What the strip adds is the thing a zoom
 * takes away: once the chart is showing a window, nothing on it says where that window sits in the
 * record, and a scope without that is a scope you can get lost in.</p>
 *
 * <p>Drawn as a <b>min/max band</b> rather than a line, like everything else here that is
 * decimated: at this size a column spans hundreds of samples, and the maximum alone draws a noise
 * floor several dB above where it is. The band is <b>one joined region</b>, never a stroke per
 * column — see the note at the stroke, which is where both halves of a 2026-11-22 report came
 * from.</p>
 *
 * <p>The arithmetic — where a time sits, what a press grabs, what a drag leaves — is in
 * <code>scope/overviewStrip.ts</code>, because nothing in a <code>.vue</code> is testable here and
 * every one of those rules is the kind that is quietly wrong rather than visibly broken.</p>
 */

const props = withDefaults(
  defineProps<{
    /** The whole record, never the drawn slice. See useOscilloscope.overview. */
    overview: ScopeRecordResponse | null
    /** The window the chart is showing, in seconds relative to the trigger. */
    window: ScopeTimeWindow
    /** The axis the strip draws, which is the record's own span — or the settings' before one exists. */
    bounds: ScopeTimeWindow
    showLeft?: boolean
    showRight?: boolean
  }>(),
  { showLeft: true, showRight: true },
)

const emit = defineEmits<{
  /** A new window, for the one value the panel's boxes hold. */
  'update:window': [window: ScopeTimeWindow]
}>()

const { isDark } = useAppTheme()

/**
 * How tall the strip is, in CSS pixels.
 *
 * <p>Fixed, and the element is rendered whether or not there is a record — the furniture rule: the
 * chart's height is the view less this card's own, so a strip that appeared with the first capture
 * would take 72 px off the canvas at the moment the first trace arrived.</p>
 */
const STRIP_HEIGHT_PX = 56

/** The channel colours the chart uses, so one signal is one colour wherever it is drawn. */
const CHANNEL_COLORS = ['#3b82f6', '#f97316'] as const

const THEME = {
  dark: { well: '#15181d', grid: '#404040', axis: '#a3a3a3', marker: '#f59e0b', box: '#e5e5e5', shade: 'rgba(0,0,0,0.45)' },
  light: { well: '#ffffff', grid: '#e5e5e5', axis: '#525252', marker: '#b45309', box: '#404040', shade: 'rgba(0,0,0,0.10)' },
} as const

function theme() {
  return isDark.value ? THEME.dark : THEME.light
}

const canvas = ref<HTMLCanvasElement | null>(null)
const host = ref<HTMLDivElement | null>(null)
const widthPx = ref(600)

/** The strip's drawing area. Inset so the box's edges at either end are not half off the canvas. */
const INSET_PX = 4

const geometry = computed<StripGeometry>(() => ({
  left: INSET_PX,
  width: Math.max(1, widthPx.value - INSET_PX * 2),
}))

const box = computed(() => stripBox(props.window, props.bounds, geometry.value))

/** True while the chart is showing everything there is — then the box fills the strip and says so. */
const coversAll = computed(() => stripCoversAll(props.window, props.bounds))

/** The narrowest window a gesture here may leave — the minimum grip, converted at the record's own scale. */
const minSpanS = computed(() => {
  const span = props.bounds.toS - props.bounds.fromS
  if (!(span > 0)) return 0
  return (MIN_STRIP_BOX_PX / geometry.value.width) * span
})

/** One channel's min/max per column, ready to stroke. */
interface StripChannel {
  color: string
  x0: number
  dx: number
  min: readonly number[]
  max: readonly number[]
}

function columnsOf(slice: ScopeChannelSlice): { min: readonly number[]; max: readonly number[] } | null {
  // A raw slice has no columns: at this width the whole record is always decimated, but a record
  // short enough comes back raw and then the samples are both extremes of their own column.
  if (slice.raw) {
    const samples = slice.samples
    return samples ? { min: samples, max: samples } : null
  }
  if (!slice.min || !slice.max) return null
  return { min: slice.min, max: slice.max }
}

const channels = computed<StripChannel[]>(() => {
  const drawn = (props.overview?.channels ?? []).filter((slice) =>
    slice.channel === 1 ? props.showRight : props.showLeft,
  )
  const out: StripChannel[] = []
  for (const slice of drawn) {
    const columns = columnsOf(slice)
    if (!columns) continue
    out.push({
      color: CHANNEL_COLORS[slice.channel] ?? CHANNEL_COLORS[0],
      x0: slice.x0,
      dx: slice.dx,
      min: columns.min,
      max: columns.max,
    })
  }
  return out
})

/** The level axis the strip draws against: both extremes of everything on it, so nothing is clipped. */
const levelRange = computed(() => {
  let lo = Number.POSITIVE_INFINITY
  let hi = Number.NEGATIVE_INFINITY
  for (const channel of channels.value) {
    for (const v of channel.min) if (v < lo) lo = v
    for (const v of channel.max) if (v > hi) hi = v
  }
  if (!Number.isFinite(lo) || !Number.isFinite(hi) || hi <= lo) return null
  // A hair of margin, so a flat-topped trace does not sit exactly on the edge of the strip.
  const pad = (hi - lo) * 0.06
  return { lo: lo - pad, hi: hi + pad }
})

/** How heavy the trace is drawn. See the note at its stroke. */
const TRACE_WIDTH = 1.75

/**
 * How far the band's body is tinted, against its own opaque edges.
 *
 * <p>The chart's own figure, so one min/max band is drawn the same way wherever it appears — and
 * the reason it is not opaque is that this strip draws both inputs over each other with nothing
 * between them.</p>
 */
const BAND_ALPHA = 0.55

function draw() {
  const el = canvas.value
  if (!el) return

  const dpr = window.devicePixelRatio || 1
  const w = widthPx.value
  const h = STRIP_HEIGHT_PX
  if (el.width !== Math.round(w * dpr) || el.height !== Math.round(h * dpr)) {
    el.width = Math.round(w * dpr)
    el.height = Math.round(h * dpr)
  }

  const ctx = el.getContext('2d')
  if (!ctx) return

  const t = theme()
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  ctx.clearRect(0, 0, w, h)
  ctx.fillStyle = t.well
  ctx.fillRect(0, 0, w, h)

  const geo = geometry.value
  const span = props.bounds.toS - props.bounds.fromS

  // The trace: one joined region between the two outlines, which is the chart's own min/max band
  // rule arriving on the strip that never followed it.
  //
  // It was a vertical stroke per column, and that draws the band only while a column covers enough
  // samples to have some height. The strip is fetched at the chart's own column budget, so on a
  // short record a column is a sample or two -- and then the strokes are shorter than the gaps
  // between them and the trace reads as a row of dots, or, where the record is short enough to come
  // back *raw*, min is max, every stroke is a zero-length segment, and butt caps paint exactly
  // nothing. Both were reported together on 2026-11-22 ("the zoom bar shows no signal anymore when
  // set to smaller time/div", "it seems to draw a dashed line"): one cause, two faces of it, and the
  // empty one is simply the dotted one with the dots shrunk to zero. A region cannot have either --
  // where the band has no height it is the line that strokes its own edges.
  const range = levelRange.value
  if (range && span > 0) {
    const toY = (v: number) => h - ((v - range.lo) / (range.hi - range.lo)) * h
    for (const channel of channels.value) {
      const count = Math.min(channel.min.length, channel.max.length)
      if (count === 0) continue
      // Not rounded to half pixels the way a per-column stroke was: this is a continuous path, and
      // snapping each point to a pixel centre puts a stair on a slope that has none.
      const toX = (i: number) =>
        geo.left + ((channel.x0 + i * channel.dx - props.bounds.fromS) / span) * geo.width

      ctx.beginPath()
      ctx.moveTo(toX(0), toY(channel.max[0]!))
      for (let i = 1; i < count; i++) ctx.lineTo(toX(i), toY(channel.max[i]!))
      for (let i = count - 1; i >= 0; i--) ctx.lineTo(toX(i), toY(channel.min[i]!))
      ctx.closePath()

      // The body at alpha and both edges opaque, as the chart draws a band: two channels over each
      // other are then still two, where an opaque fill would be whichever was drawn last.
      ctx.fillStyle = channel.color
      ctx.globalAlpha = BAND_ALPHA
      ctx.fill()
      ctx.globalAlpha = 1

      // A little over a hairline (Joost, 2026-11-20), and it is the stroke that carries the whole
      // trace wherever the band has no height -- a raw slice, or a stretch where the signal is flat.
      ctx.strokeStyle = channel.color
      ctx.lineWidth = TRACE_WIDTH
      ctx.lineJoin = 'round'
      ctx.stroke()
    }
  }

  // The trigger, at t = 0 and on the record's own axis -- so it is where the strip says it is even
  // when the chart below is looking somewhere else entirely.
  if (span > 0) {
    const x = Math.round(geo.left + ((0 - props.bounds.fromS) / span) * geo.width) + 0.5
    if (x >= geo.left && x <= geo.left + geo.width) {
      ctx.strokeStyle = t.marker
      ctx.lineWidth = 1
      ctx.setLineDash([3, 3])
      ctx.beginPath()
      ctx.moveTo(x, 0)
      ctx.lineTo(x, h)
      ctx.stroke()
      ctx.setLineDash([])
    }
  }

  // Everything outside the window, dimmed. The shade rather than an outline alone is what makes the
  // picture readable at a glance: what is lit is what is on the chart.
  const b = box.value
  if (b && !coversAll.value) {
    ctx.fillStyle = t.shade
    ctx.fillRect(0, 0, b.fromX, h)
    ctx.fillRect(b.toX, 0, w - b.toX, h)
  }

  if (b) {
    ctx.strokeStyle = t.box
    ctx.lineWidth = 1
    ctx.strokeRect(Math.round(b.fromX) + 0.5, 0.5, Math.max(1, Math.round(b.toX - b.fromX)), h - 1)

    // The two grips, drawn as solid bars so the edges are something to aim at rather than a
    // hairline. Only when the box is not the whole strip: there is nothing to drag it to.
    if (!coversAll.value) {
      ctx.fillStyle = t.box
      ctx.fillRect(Math.round(b.fromX) - 1, h / 2 - 8, 3, 16)
      ctx.fillRect(Math.round(b.toX) - 2, h / 2 - 8, 3, 16)
    }
  }

  ctx.strokeStyle = t.grid
  ctx.lineWidth = 1
  ctx.strokeRect(0.5, 0.5, w - 1, h - 1)
}

// ---------------------------------------------------------------------------
// Dragging the box
// ---------------------------------------------------------------------------

const grab = ref<StripGrab>('outside')
const dragStart = ref<{ seconds: number; window: ScopeTimeWindow } | null>(null)

/** The cursor the strip offers, which is also how it says what a press would do. */
const cursorClass = computed(() => {
  if (!props.overview) return ''
  if (grab.value === 'from' || grab.value === 'to') return 'cursor-ew-resize'
  return 'cursor-pointer'
})

function pointerSeconds(event: PointerEvent): number | null {
  const el = canvas.value
  if (!el) return null
  const rect = el.getBoundingClientRect()
  return stripSeconds(event.clientX - rect.left, props.bounds, geometry.value)
}

function onPointerDown(event: PointerEvent) {
  const el = canvas.value
  if (!el || !props.overview) return
  const rect = el.getBoundingClientRect()
  const x = event.clientX - rect.left

  let taken = stripGrabAt(x, box.value)
  let startWindow = props.window

  // A press on the bare strip jumps the window there, keeping its span, and then goes on as a body
  // drag -- which is what a click on a zoom bar does on every bench instrument, and the one gesture
  // that moves a window a long way at once.
  if (taken === 'outside') {
    const seconds = stripSeconds(x, props.bounds, geometry.value)
    startWindow = stripCentreOn(seconds, props.window.toS - props.window.fromS, props.bounds)
    emit('update:window', startWindow)
    taken = 'body'
  }

  grab.value = taken
  dragStart.value = { seconds: stripSeconds(x, props.bounds, geometry.value), window: startWindow }
  el.setPointerCapture(event.pointerId)
  event.preventDefault()
}

function onPointerMove(event: PointerEvent) {
  const start = dragStart.value
  if (!start) {
    // Not dragging: the cursor still has to say what a press here would take hold of.
    const el = canvas.value
    if (!el || !props.overview) return
    const rect = el.getBoundingClientRect()
    grab.value = stripGrabAt(event.clientX - rect.left, box.value)
    return
  }

  const seconds = pointerSeconds(event)
  if (seconds === null) return
  emit('update:window', stripDrag(grab.value, start.window, seconds - start.seconds, props.bounds, minSpanS.value))
}

function onPointerUp(event: PointerEvent) {
  if (!dragStart.value) return
  dragStart.value = null
  canvas.value?.releasePointerCapture?.(event.pointerId)
}

function onPointerLeave() {
  if (!dragStart.value) grab.value = 'outside'
}

// ---------------------------------------------------------------------------

let observer: ResizeObserver | null = null

function fit() {
  const el = host.value
  if (!el) return
  widthPx.value = Math.max(1, Math.round(el.getBoundingClientRect().width))
  draw()
}

onMounted(() => {
  fit()
  observer = new ResizeObserver(fit)
  if (host.value) observer.observe(host.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
})

// Everything the picture is made of. A plain redraw: there is no plot instance to rebuild, which is
// the whole reason this is a canvas of its own rather than a second uPlot.
watch(
  [() => props.overview, () => props.window, () => props.bounds, () => props.showLeft, () => props.showRight, isDark],
  draw,
  { deep: false },
)
</script>

<template>
  <!-- The strip is the whole width of the chart below it, and nothing stands beside it: the
       record's own timebase and the window's length were a readout at the right-hand end until
       2026-11-21, and both are already on the chart's own status line a few pixels below
       ("2.58 ms/div ... -13.40 ms to 12.39 ms"). A navigator is read by its box, so the width
       is worth more to it than a second copy of two figures. -->
  <div>
    <div
      ref="host"
      class="relative"
      :style="{ height: `${STRIP_HEIGHT_PX}px` }"
      :title="
        overview
          ? 'The whole record. Drag the box to move the window the chart is showing, drag its edges to resize it, or click anywhere to jump there.'
          : 'The whole record, once something has been captured.'
      "
    >
      <canvas
        ref="canvas"
        class="block h-full w-full rounded"
        :class="cursorClass"
        :style="{ touchAction: 'none' }"
        @pointerdown="onPointerDown"
        @pointermove="onPointerMove"
        @pointerup="onPointerUp"
        @pointercancel="onPointerUp"
        @pointerleave="onPointerLeave"
      ></canvas>

      <p
        v-if="!overview"
        class="pointer-events-none absolute inset-0 grid place-items-center text-[11px] text-neutral-400 dark:text-neutral-600"
      >
        Nothing captured yet
      </p>
    </div>
  </div>
</template>
