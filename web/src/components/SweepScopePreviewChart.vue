<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { ThdSweepPoint } from '../api/client'
import { useAppTheme } from '../composables/useAppTheme'
import { formatHz } from '../composables/useSweep'
import { mergePreviewSeries } from '../sweep/points'

/**
 * A small oscilloscope snapshot alongside the sweep chart -- the time-domain waveform behind
 * whichever point is currently selected on each channel. Each trace starts at its own rising
 * zero-crossing (see SpectrumPipeline.BuildScopePreview server-side, ported from
 * OscilloscopeChart.vue's live trigger search) rather than at an arbitrary tail offset, so it reads
 * as a clean cycle instead of a random-looking squiggle. Colours match the spectrum preview's
 * convention: left blue, right red.
 */
const props = defineProps<{ pointLeft: ThdSweepPoint | null; pointRight: ThdSweepPoint | null }>()

const { isDark } = useAppTheme()
const containerRef = ref<HTMLDivElement | null>(null)
const CHART_HEIGHT = 150

const THEME = {
  dark: { axis: '#a3a3a3', grid: '#404040', left: '#60a5fa', right: '#f87171' },
  light: { axis: '#525252', grid: '#e5e5e5', left: '#1d6fd0', right: '#c62828' },
} as const
function currentTheme() {
  return isDark.value ? THEME.dark : THEME.light
}

function buildOptions(width: number): uPlot.Options {
  const theme = currentTheme()
  return {
    width,
    height: CHART_HEIGHT,
    scales: { x: { time: false }, y: {} },
    axes: [
      {
        space: 50,
        values: (_u, vals) => vals.map((v) => (v == null ? null : `${(v * 1000).toFixed(v !== 0 && Math.abs(v) < 0.001 ? 3 : 2)}`)),
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
      { size: 46, stroke: theme.axis, grid: { stroke: theme.grid, width: 1 }, ticks: { stroke: theme.grid, width: 1 } },
    ],
    series: [
      {},
      { label: 'L min', stroke: theme.left, width: 1, points: { show: false } },
      { label: 'L max', stroke: theme.left, width: 1, points: { show: false } },
      { label: 'R min', stroke: theme.right, width: 1, points: { show: false } },
      { label: 'R max', stroke: theme.right, width: 1, points: { show: false } },
    ],
    cursor: { points: { show: false } },
    legend: { show: false },
  }
}

let plot: uPlot | null = null
let plotIsDark = isDark.value

function ensurePlot() {
  const el = containerRef.value
  if (!el) return
  if (plot && plotIsDark === isDark.value) return

  plot?.destroy()
  const width = el.getBoundingClientRect().width || 300
  plot = new uPlot(buildOptions(width), [[], [], [], [], []], el)
  plotIsDark = isDark.value
}

function update() {
  ensurePlot()
  if (!plot) return

  const merged = mergePreviewSeries(props.pointLeft?.previewScope ?? null, props.pointRight?.previewScope ?? null)
  plot.setData([merged.x, merged.leftMin, merged.leftMax, merged.rightMin, merged.rightMax])
}

watch(() => [props.pointLeft, props.pointRight], update)
watch(isDark, () => {
  plot?.destroy()
  plot = null
  update()
})

function handleResize() {
  if (plot && containerRef.value)
    plot.setSize({ width: containerRef.value.getBoundingClientRect().width, height: CHART_HEIGHT })
}

onMounted(() => {
  window.addEventListener('resize', handleResize)
  update()
})
onBeforeUnmount(() => {
  window.removeEventListener('resize', handleResize)
  plot?.destroy()
  plot = null
})

const theme = computed(() => currentTheme())

/** "1.99 ms" from a point's own preview span — each channel can differ (independent triggers), so this is per-channel, not shared. */
function spanMs(point: ThdSweepPoint | null): string | null {
  const x = point?.previewScope?.x
  if (!x || x.length < 2) return null
  return `${((x[x.length - 1] - x[0]) * 1000).toFixed(2)} ms`
}

const leftSpan = computed(() => spanMs(props.pointLeft))
const rightSpan = computed(() => spanMs(props.pointRight))
</script>

<template>
  <section class="flex flex-col gap-1 rounded-lg border border-neutral-200 p-2 dark:border-neutral-800">
    <div class="flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>Oscilloscope (ms)</span>
      <span class="flex items-center gap-2">
        <span v-if="pointLeft" class="flex items-center gap-1">
          <span class="h-1.5 w-1.5 rounded-full" :style="{ background: theme.left }"></span>
          {{ formatHz(pointLeft.freqHz) }}<span v-if="leftSpan"> · {{ leftSpan }}</span>
        </span>
        <span v-if="pointRight" class="flex items-center gap-1">
          <span class="h-1.5 w-1.5 rounded-full" :style="{ background: theme.right }"></span>
          {{ formatHz(pointRight.freqHz) }}<span v-if="rightSpan"> · {{ rightSpan }}</span>
        </span>
        <span v-if="!pointLeft && !pointRight">No data</span>
      </span>
    </div>
    <div ref="containerRef" class="w-full"></div>
  </section>
</template>
