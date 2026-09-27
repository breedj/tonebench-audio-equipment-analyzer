<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { ThdSweepPoint } from '../api/client'
import { useAppTheme } from '../composables/useAppTheme'
import { formatHz } from '../composables/useSweep'
import { freqAxis } from '../freqAxis'
import { mergePreviewSeries } from '../sweep/points'

/**
 * A small spectrum snapshot alongside the sweep's THD-vs-frequency chart -- a decimated FFT around
 * whichever point is currently selected on each channel (the last one measured while a sweep runs,
 * or the one nearest the cursor once it's done; see App.vue's selectedSweepPointFor). Left is drawn
 * blue, right red -- matching this app's spectrum convention -- and a channel with no point (never
 * part of this sweep, i.e. disabled in the generator) simply draws nothing for it.
 *
 * Deliberately not SpectrumChart reused smaller: that component streams live full-rate frames and
 * owns viewport/zoom state this never needs -- this only ever redraws on a point change, from data
 * already decimated to at most SpectrumPipeline.PreviewMaxColumns server-side.
 *
 * `label`, when given, names whose points these are (a checked history item's own name) so several
 * of these stacked underneath each other -- the live one plus one per checked overlay -- can still
 * be told apart. Omitted for the live pair, whose header stays the plain "Spectrum (dBV)" it always was.
 */
const props = defineProps<{ pointLeft: ThdSweepPoint | null; pointRight: ThdSweepPoint | null; label?: string }>()

const { isDark } = useAppTheme()
const containerRef = ref<HTMLDivElement | null>(null)
const CHART_HEIGHT = 150

// Matches ThdSweepChart's own D2 (blue)/D5 (red) hues -- one is "left channel", the other "right
// channel" here rather than "which harmonic", but staying inside the app's existing blue/red pair
// keeps a hovered value readable as the same colour language everywhere else.
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
    scales: { x: { time: false, distr: 3, log: 10 }, y: {} },
    axes: [
      { ...freqAxis(), stroke: theme.axis, grid: { stroke: theme.grid, width: 1 }, ticks: { stroke: theme.grid, width: 1 } },
      { size: 46, stroke: theme.axis, grid: { stroke: theme.grid, width: 1 }, ticks: { stroke: theme.grid, width: 1 } },
    ],
    // One line per channel, not a min/max pair: at this chart's small size a min+max band read as
    // two separate wiggly traces per channel (visibly four with both channels on) rather than the
    // noise-floor context it's meant to convey on the full-size spectrum chart. Max, not min or a
    // mean, so a harmonic spike a bucket happens to straddle still shows -- the "never plot the
    // mean" reasoning stays, just resolved to one series instead of two.
    series: [
      {},
      { label: 'L', stroke: theme.left, width: 1, points: { show: false } },
      { label: 'R', stroke: theme.right, width: 1, points: { show: false } },
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
  plot = new uPlot(buildOptions(width), [[], [], []], el)
  plotIsDark = isDark.value
}

function update() {
  ensurePlot()
  if (!plot) return

  const merged = mergePreviewSeries(props.pointLeft?.previewFft ?? null, props.pointRight?.previewFft ?? null)
  plot.setData([merged.x, merged.leftMax, merged.rightMax])
}

watch(() => [props.pointLeft, props.pointRight], update)
watch(isDark, () => {
  plot?.destroy()
  plot = null
  update()
})

// Observes the container rather than the window: the column this chart sits in also changes width
// when the measurement sidebar is pinned or unpinned, which is not a window resize — measured, the
// canvas stayed at 432 px inside a column that had grown to 661 px. Same mechanism as the
// ResizeObserver in FrequencyResponseChart.
let resizeObserver: ResizeObserver | null = null
function handleResize() {
  if (plot && containerRef.value)
    plot.setSize({ width: containerRef.value.getBoundingClientRect().width, height: CHART_HEIGHT })
}

onMounted(() => {
  resizeObserver = new ResizeObserver(handleResize)
  if (containerRef.value) resizeObserver.observe(containerRef.value)
  update()
})
onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  plot?.destroy()
  plot = null
})

const theme = computed(() => currentTheme())
</script>

<template>
  <section class="flex flex-col gap-1 panel-surface rounded-lg border border-neutral-200 p-2 dark:border-neutral-800">
    <div class="flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>{{ label ? `${label} — Spectrum (dBV)` : 'Spectrum (dBV)' }}</span>
      <span class="flex items-center gap-2">
        <span v-if="pointLeft" class="flex items-center gap-1">
          <span class="h-1.5 w-1.5 rounded-full" :style="{ background: theme.left }"></span>
          {{ formatHz(pointLeft.freqHz) }}
        </span>
        <span v-if="pointRight" class="flex items-center gap-1">
          <span class="h-1.5 w-1.5 rounded-full" :style="{ background: theme.right }"></span>
          {{ formatHz(pointRight.freqHz) }}
        </span>
        <span v-if="!pointLeft && !pointRight">No data</span>
      </span>
    </div>
    <div ref="containerRef" class="w-full"></div>
  </section>
</template>
