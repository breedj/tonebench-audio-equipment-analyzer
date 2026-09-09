<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { ImpulseTraceJson } from '../api/client'
import { RESULT_CHART_HEIGHT_PX } from '../charts/dimensions'
import { useAppTheme } from '../composables/useAppTheme'
import { useZoomBounds } from '../composables/useZoomBounds'
import { formatImpulsePeak, impulseUnit } from '../measurements/impulseUnit'

/**
 * The impulse response itself, on a **linear** time axis with the impulse at t = 0.
 *
 * <para>Linear, not logarithmic, and that is the one thing that makes this chart unlike every other
 * one in the app: an impulse response is a waveform, its x axis is time, and a log grid would crush
 * the whole decay tail into the last few pixels.</para>
 *
 * <para>Min and max are both drawn — the max as the line, the min as a band beneath it — for the
 * same reason the spectrum charts do it. It matters more here than anywhere: a reflection can be a
 * single sample, and a column that showed only one side of the waveform would hide half of them.</para>
 */
const props = defineProps<{
  impulse: ImpulseTraceJson | null
  /** dB relative to the impulse's own peak, rather than volts. What you want for reading a decay. */
  normalised: boolean
  thickLines: boolean
  timeFromMs: number
  timeToMs: number
  /** Only used by the normalised view; the volts view autoscales, since its range is the DUT's. */
  yTopDb: number
  yBottomDb: number
}>()

const { isDark } = useAppTheme()

const container = ref<HTMLDivElement | null>(null)
const plot = shallowRef<uPlot | null>(null)

const THEME = {
  dark: { axis: '#a3a3a3', grid: '#404040', trace: '#60a5fa', marker: '#f59e0b' },
  light: { axis: '#525252', grid: '#e5e5e5', trace: '#1d6fd0', marker: '#b45309' },
} as const

function theme() {
  return isDark.value ? THEME.dark : THEME.light
}

/** dB re the impulse's own peak, floored so a zero sample does not become -Infinity. */
function toDb(volts: number, peak: number): number {
  return 20 * Math.log10(Math.max(Math.abs(volts), 1e-12) / Math.max(peak, 1e-12))
}

// Both pure, and in their own module so they can be unit-tested: nothing in a .vue file is testable
// under the node environment vitest runs in. See impulseUnit.ts for why the scaling exists at all.
const unit = computed(() => impulseUnit(props.impulse?.peakVolts ?? 0))

const peakLabel = computed(() =>
  props.impulse ? formatImpulsePeak(props.impulse.peakVolts, unit.value) : '',
)

/**
 * The y window when nothing is zoomed.
 *
 * The volts view used to let uPlot autoscale, which is the right picture but leaves "not zoomed"
 * undefined — there is no window to compare a drag against, and so nothing to reset to. Fitting the
 * data explicitly gives the same view and makes both work.
 */
const yWindow = computed(() => {
  if (props.normalised) return { min: props.yBottomDb, max: props.yTopDb }

  const { min, max } = series.value
  if (min.length === 0) return { min: -1, max: 1 }

  let lo = Number.POSITIVE_INFINITY
  let hi = Number.NEGATIVE_INFINITY
  for (let i = 0; i < min.length; i++) {
    if (min[i] < lo) lo = min[i]
    if (max[i] > hi) hi = max[i]
  }

  const pad = (hi - lo) * 0.05 || 0.1
  return { min: lo - pad, max: hi + pad }
})

const series = computed(() => {
  const impulse = props.impulse
  if (!impulse) return { timeMs: [] as number[], min: [] as number[], max: [] as number[] }

  const timeMs = impulse.timeS.map((t) => t * 1000)
  if (!props.normalised) {
    const factor = unit.value.factor
    return {
      timeMs,
      min: impulse.minVolts.map((v) => v * factor),
      max: impulse.maxVolts.map((v) => v * factor),
    }
  }

  // In dB the two envelope halves stop being "min" and "max": what a reader wants is the column's
  // largest excursion either way, so the band collapses to one magnitude curve. Drawing 20·log of a
  // negative minimum would be arithmetic on a sign that no longer means anything.
  const peak = Math.max(impulse.peakVolts, 1e-12)
  const magnitude = impulse.maxVolts.map((v, i) =>
    toDb(Math.max(Math.abs(v), Math.abs(impulse.minVolts[i])), peak),
  )
  return { timeMs, min: magnitude, max: magnitude }
})

/**
 * The min/max band, stroked as one vertical per column. The project's decimation rule: the server
 * sends both, and a client that drew only the max would redraw the waveform riding along one edge
 * of itself.
 */
function minMaxBandPlugin(): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          if (props.normalised) return
          const { min, max, timeMs } = series.value
          if (timeMs.length === 0) return

          const ctx = u.ctx
          ctx.save()
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          const path = new Path2D()
          for (let i = 0; i < timeMs.length; i++) {
            const x = u.valToPos(timeMs[i], 'x', true)
            path.moveTo(x, u.valToPos(min[i], 'y', true))
            path.lineTo(x, u.valToPos(max[i], 'y', true))
          }

          ctx.strokeStyle = theme().trace
          ctx.globalAlpha = 0.35
          ctx.lineWidth = 1
          ctx.stroke(path)
          ctx.restore()
        },
      ],
    },
  }
}

/** A dashed vertical at t = 0 — where the impulse is, and the origin every reflection is read from. */
function impulseMarkerPlugin(): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          const x = u.valToPos(0, 'x', true)
          if (x < u.bbox.left || x > u.bbox.left + u.bbox.width) return

          const ctx = u.ctx
          ctx.save()
          ctx.setLineDash([4, 4])
          ctx.strokeStyle = theme().marker
          ctx.lineWidth = 1
          ctx.beginPath()
          ctx.moveTo(x, u.bbox.top)
          ctx.lineTo(x, u.bbox.top + u.bbox.height)
          ctx.stroke()
          ctx.restore()
        },
      ],
    },
  }
}

/**
 * True while either axis shows something other than the window the display panel asks for. Drives
 * the Reset zoom button, which is the only visible sign the axes are no longer the panel's.
 */
const { zoomed, noteScale, resetZoom } = useZoomBounds(
  () => plot.value,
  () => ({ xMin: props.timeFromMs, xMax: props.timeToMs, yMin: yWindow.value.min, yMax: yWindow.value.max }),
)

function buildOptions(width: number): uPlot.Options {
  const t = theme()
  return {
    width,
    height: RESULT_CHART_HEIGHT_PX,
    scales: {
      // Arguments passed through rather than ignored, so a drag-zoom survives setScale — the same
      // idiom the other charts use. Note that what actually puts the panel's window on the x axis is
      // refresh()'s explicit setScale, not the `??` here: on an x scale that fallback is dead code,
      // because uPlot only ever passes null for x when there is no data yet. Drop those setScale
      // calls and this axis silently reverts to the data's own span. See CLAUDE.md.
      x: { time: false, auto: false, range: (_u, min, max) => [min ?? props.timeFromMs, max ?? props.timeToMs] },
      y: { auto: false, range: (_u, min, max) => [min ?? yWindow.value.min, max ?? yWindow.value.max] },
    },
    axes: [
      {
        label: 'Time (ms)',
        stroke: t.axis,
        grid: { stroke: t.grid, width: 1 },
        ticks: { stroke: t.grid, width: 1 },
      },
      {
        label: props.normalised ? 'Level (dB re peak)' : unit.value.label,
        size: 64,
        stroke: t.axis,
        grid: { stroke: t.grid, width: 1 },
        ticks: { stroke: t.grid, width: 1 },
      },
    ],
    series: [
      { label: 'Time (ms)' },
      {
        label: props.normalised ? 'Level' : 'Impulse',
        stroke: t.trace,
        width: props.thickLines ? 2 : 1,
        points: { show: false },
      },
    ],
    plugins: [minMaxBandPlugin(), impulseMarkerPlugin()],
    // Both axes, because a reflection is read off the time axis and its level off the other, and
    // demanding a pixel-accurate one-axis drag to get either would make both awkward.
    cursor: { drag: { x: true, y: true, uni: 12 } },
    legend: { show: false },
    // Fires on setData too (both scales are auto:false, so setData re-applies the same window),
    // hence the change gate inside noteScale.
    hooks: { setScale: [(u: uPlot) => noteScale(u)] },
  }
}

function rebuild() {
  const el = container.value
  if (!el) return

  plot.value?.destroy()
  const width = el.getBoundingClientRect().width || 600
  plot.value = new uPlot(buildOptions(width), [[], []], el)
  // uPlot has no double-click-to-reset of its own once drag zoom is on.
  plot.value.over.addEventListener('dblclick', resetZoom)
  refresh()
}

function refresh() {
  const p = plot.value
  if (!p) return

  const { timeMs, max } = series.value
  // setData on the existing instance, never a new plot per frame.
  p.setData([timeMs, max])
  p.setScale('x', { min: props.timeFromMs, max: props.timeToMs })
  p.setScale('y', { min: yWindow.value.min, max: yWindow.value.max })
}

let observer: ResizeObserver | null = null

onMounted(() => {
  rebuild()
  observer = new ResizeObserver(() => {
    const el = container.value
    if (el && plot.value) plot.value.setSize({ width: el.getBoundingClientRect().width, height: RESULT_CHART_HEIGHT_PX })
  })
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
  plot.value?.destroy()
  plot.value = null
})

// A theme change or a change of axis meaning needs the whole plot rebuilt; new data does not.
watch([isDark, () => props.normalised, () => props.thickLines, () => unit.value.label], rebuild)
watch([() => props.impulse, () => props.timeFromMs, () => props.timeToMs, () => props.yTopDb, () => props.yBottomDb], refresh)
</script>

<template>
  <section class="flex flex-col gap-1">
    <div class="flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>Impulse response</span>
      <span v-if="impulse">
        peak {{ peakLabel }} · gate −{{ impulse.gatePreMs }}/+{{ impulse.gatePostMs }} ms ·
        {{ impulse.fullSampleCount }} samples · nothing below {{ impulse.resolutionLimitHz.toFixed(0) }} Hz is resolved
      </span>
      <span v-else>No impulse response measured</span>

      <button
        v-if="zoomed"
        type="button"
        class="shrink-0 rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Back to the time and level window set in the display panel."
        @click="resetZoom"
      >
        Reset zoom
      </button>
      <span v-else class="shrink-0">Drag to zoom · double-click to reset</span>
    </div>
    <div ref="container" class="w-full"></div>
  </section>
</template>
