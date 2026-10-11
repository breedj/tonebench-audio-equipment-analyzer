<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { PreviewPoint } from '../api/client'
import { useAppTheme } from '../composables/useAppTheme'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import ExpandToggle from './ExpandToggle.vue'
import { formatHz } from '../composables/useSweep'
import { mergePreviewSeries } from '../sweep/points'
import {
  previewAxisSplits,
  scopeAxisTickLabels,
  useSweepPreviewFit,
  type ScopeAxisRange,
} from '../sweep/previewAxis'

/**
 * A small oscilloscope snapshot alongside the sweep chart -- the time-domain waveform behind
 * whichever point is currently selected on each channel. Each trace starts at its own rising
 * zero-crossing (see SpectrumPipeline.BuildScopePreview server-side, ported from
 * OscilloscopeChart.vue's live trigger search) rather than at an arbitrary tail offset, so it reads
 * as a clean cycle instead of a random-looking squiggle. Colours match the spectrum preview's
 * convention: left blue, right red.
 *
 * `label`, when given, names whose points these are -- see SweepFftPreviewChart's own doc on why.
 *
 * `fitRange` is the volts window covering every waveform the main chart's points hold, used while
 * the header's Fit-all toggle is on -- the spectrum preview's arrangement exactly, with its own
 * toggle because a waveform and a spectrum are read for different things. Symmetric about zero and
 * on a 1-2-5 grid; see sweep/previewAxis.ts.
 */
const props = defineProps<{
  pointLeft: PreviewPoint | null
  pointRight: PreviewPoint | null
  label?: string
  fitRange?: ScopeAxisRange | null
}>()

const { isDark } = useAppTheme()
const { fitAllScopes } = useSweepPreviewFit()
const rootRef = ref<HTMLElement | null>(null)
const containerRef = ref<HTMLDivElement | null>(null)
const CHART_HEIGHT = 150
// Fitted only when expanded -- see charts/useResultChartHeight.ts's pageHeightPx. In the rail this
// keeps the height it has always had, because several of these stack there; expanded, the root is
// pinned over the viewport and the plot takes the window.
const chartHeight = useResultChartHeight(rootRef, containerRef, { pageHeightPx: CHART_HEIGHT })

const THEME = {
  dark: { axis: '#a3a3a3', grid: '#404040', left: '#60a5fa', right: '#f87171' },
  light: { axis: '#525252', grid: '#e5e5e5', left: '#1d6fd0', right: '#c62828' },
} as const
function currentTheme() {
  return isDark.value ? THEME.dark : THEME.light
}

function buildOptions(width: number): uPlot.Options {
  const theme = currentTheme()
  const fitted = fitAllScopes.value
  return {
    width,
    height: chartHeight.value,
    scales: {
      x: { time: false },
      // Read inside the callback rather than baked into the options, so a window that grows as the
      // sweep adds points lands on the next setData -- see SweepFftPreviewChart, same arrangement.
      y: fitted
        ? {
            range: (_u, dataMin, dataMax) => {
              const bounds = props.fitRange
              if (bounds) return [bounds.min, bounds.max]
              return uPlot.rangeNum(dataMin ?? -1, dataMax ?? 1, 0.1, true)
            },
          }
        : {},
    },
    axes: [
      {
        space: 50,
        values: (_u, vals) => vals.map((v) => (v == null ? null : `${(v * 1000).toFixed(v !== 0 && Math.abs(v) < 0.001 ? 3 : 2)}`)),
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
      {
        size: 46,
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
        // A fixed volts ladder rather than uPlot's own increments, which move with the data and so
        // put the grid somewhere else on every point. Every line is drawn; only as many as fit are
        // numbered (bbox is in canvas pixels).
        // The `?? span/4` is not decoration: these callbacks outlive the window they were built
        // for by however long it takes the watcher below to rebuild the plot, and a grid of four
        // divisions is the right shape to draw in the meantime.
        ...(fitted && props.fitRange
          ? {
              splits: (_u, _axisIdx, scaleMin, scaleMax) =>
                previewAxisSplits(scaleMin, scaleMax, props.fitRange?.stepV ?? (scaleMax - scaleMin) / 4),
              values: (u, splits) =>
                scopeAxisTickLabels(
                  splits,
                  u.bbox.height / uPlot.pxRatio,
                  props.fitRange?.stepV ?? scopeStepFromSplits(splits),
                ),
            }
          : {}),
      },
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

/** The spacing a set of splits already has — the label formatter's fallback, see buildOptions. */
function scopeStepFromSplits(splits: readonly (number | null)[]): number {
  const first = splits[0]
  const second = splits[1]
  return first !== null && first !== undefined && second !== null && second !== undefined
    ? Math.abs(second - first)
    : 1
}

let plot: uPlot | null = null
/**
 * What the live instance was built for -- the theme, the toggle, and whether a window exists at
 * all, since the axis carries a `splits`/`values` pair only once one does. Options hold functions,
 * so those are rebuilds; a *changed* window is not (see the watcher below).
 */
function plotSignature() {
  return `${isDark.value}|${fitAllScopes.value}|${props.fitRange !== null && props.fitRange !== undefined}`
}
let builtFor = plotSignature()

function ensurePlot() {
  const el = containerRef.value
  if (!el) return
  if (plot && builtFor === plotSignature()) return

  plot?.destroy()
  const width = el.getBoundingClientRect().width || 300
  plot = new uPlot(buildOptions(width), [[], [], [], [], []], el)
  builtFor = plotSignature()
}

function update() {
  ensurePlot()
  if (!plot) return

  const merged = mergePreviewSeries(props.pointLeft?.previewScope ?? null, props.pointRight?.previewScope ?? null)
  plot.setData([merged.x, merged.leftMin, merged.leftMax, merged.rightMin, merged.rightMax])
}

watch(() => [props.pointLeft, props.pointRight], update)
// A new window is one setData away: the y scale's range callback reads it. Compared by value
// rather than by identity -- the parent recomputes the object on every pass of a running sweep.
watch(() => [props.fitRange?.min, props.fitRange?.max, props.fitRange?.stepV], update)
// The theme and the toggle change the options object itself, which ensurePlot notices.
watch([isDark, fitAllScopes], update)

// Observes the container rather than the window: the column this chart sits in also changes width
// when the measurement sidebar is pinned or unpinned, which is not a window resize — measured, the
// canvas stayed at 432 px inside a column that had grown to 661 px. Same mechanism as the
// ResizeObserver in FrequencyResponseChart.
let resizeObserver: ResizeObserver | null = null
function handleResize() {
  if (plot && containerRef.value)
    plot.setSize({ width: containerRef.value.getBoundingClientRect().width, height: chartHeight.value })
}
watch(chartHeight, handleResize)

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

/** "1.99 ms" from a point's own preview span — each channel can differ (independent triggers), so this is per-channel, not shared. */
function spanMs(point: PreviewPoint | null): string | null {
  const x = point?.previewScope?.x
  if (!x || x.length < 2) return null
  return `${((x[x.length - 1] - x[0]) * 1000).toFixed(2)} ms`
}

const leftSpan = computed(() => spanMs(props.pointLeft))
const rightSpan = computed(() => spanMs(props.pointRight))
</script>

<template>
  <section
    ref="rootRef"
    class="relative flex flex-col gap-1 panel-surface rounded-lg border border-neutral-200 p-2 dark:border-neutral-800"
  >
    <ExpandToggle class="absolute right-1 top-1" :target="rootRef" />
    <!-- pr-9 for the button, which is positioned absolutely and so makes no room for itself. -->
    <div class="flex items-center justify-between gap-2 pr-9 text-xs text-neutral-500 dark:text-neutral-400">
      <!-- min-w-0 so the label is what gives way when an overlay's name is long -- see the
           spectrum preview's own header. -->
      <span class="flex min-w-0 items-center gap-2">
        <span class="truncate">{{ label ? `${label} — Oscilloscope (ms)` : 'Oscilloscope (ms)' }}</span>
        <!-- Shared by every oscilloscope preview on the screen, and separate from the spectrum's. -->
        <button
          type="button"
          class="shrink-0 rounded border px-1.5 py-0 leading-5"
          :class="
            fitAllScopes
              ? 'btn-action'
              : 'border-neutral-300 text-neutral-500 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-400 dark:hover:bg-neutral-800'
          "
          :title="
            fitAllScopes
              ? 'Fixed axis: one volts window fitting every waveform the sweep chart is showing. Click to scale each point on its own again.'
              : 'Fix the volts axis to a window fitting every waveform the sweep chart is showing, so a level change between points is visible instead of scaled away.'
          "
          :aria-pressed="fitAllScopes"
          @click="fitAllScopes = !fitAllScopes"
        >
          Fit all
        </button>
      </span>
      <span class="flex shrink-0 items-center gap-2">
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
