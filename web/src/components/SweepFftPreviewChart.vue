<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { PreviewPoint } from '../api/client'
import { useAppTheme } from '../composables/useAppTheme'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import ExpandToggle from './ExpandToggle.vue'
import { formatHz } from '../composables/useSweep'
import { freqAxis } from '../freqAxis'
import { mergePreviewSeries } from '../sweep/points'
import {
  PREVIEW_GRID_STEP_DB,
  previewAxisSplits,
  previewAxisTickLabels,
  useSweepPreviewFit,
  type PreviewAxisRange,
} from '../sweep/previewAxis'

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
 *
 * `fitRange` is the y window covering every spectrum the main chart is showing (see
 * sweep/previewAxis.ts), used while the header's Fit-all toggle is on. The toggle itself is shared
 * by every instance, so the live pair and each overlay's preview are drawn against one scale and
 * neither re-scales as the cursor moves from point to point -- which is what made two points
 * impossible to compare by eye.
 */
const props = defineProps<{
  pointLeft: PreviewPoint | null
  pointRight: PreviewPoint | null
  label?: string
  fitRange?: PreviewAxisRange | null
}>()

const { isDark } = useAppTheme()
const { fitAllSpectra } = useSweepPreviewFit()
const rootRef = ref<HTMLElement | null>(null)
const containerRef = ref<HTMLDivElement | null>(null)
const CHART_HEIGHT = 150
// Fitted only when expanded -- see charts/useResultChartHeight.ts's pageHeightPx. In the rail this
// keeps the height it has always had, because several of these stack there; expanded, the root is
// pinned over the viewport and the plot takes the window.
const chartHeight = useResultChartHeight(rootRef, containerRef, { pageHeightPx: CHART_HEIGHT })

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
  const fitted = fitAllSpectra.value
  return {
    width,
    height: chartHeight.value,
    scales: {
      x: { time: false, distr: 3, log: 10 },
      // Fitted, the window is the caller's rather than this point's own -- and it is read inside
      // the callback rather than baked into the options, because uPlot re-derives the scale on
      // every setData: a range that grows as the sweep adds points therefore lands on the next
      // redraw without the plot being rebuilt. The fallback keeps the chart drawable in the moment
      // between the toggle going on and the first range arriving.
      y: fitted
        ? {
            range: (_u, dataMin, dataMax) => {
              const bounds = props.fitRange
              if (bounds) return [bounds.min, bounds.max]
              return uPlot.rangeNum(dataMin ?? -160, dataMax ?? 0, 0.1, true)
            },
          }
        : {},
    },
    axes: [
      { ...freqAxis(), stroke: theme.axis, grid: { stroke: theme.grid, width: 1 }, ticks: { stroke: theme.grid, width: 1 } },
      {
        size: 46,
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
        // A fixed dB ladder rather than uPlot's "nice" increments, which move with the data and so
        // put the grid in a different place on every point -- the same thing the fitted scale
        // exists to stop. Both ends of that scale already sit on a line (fitPreviewAxis snaps
        // them outwards), so the axis starts and ends on one.
        ...(fitted
          ? {
              splits: (_u, _axisIdx, scaleMin, scaleMax) => previewAxisSplits(scaleMin, scaleMax),
              // Every line is drawn; only as many as fit are numbered. bbox is in canvas pixels.
              values: (u, splits) => previewAxisTickLabels(splits, u.bbox.height / uPlot.pxRatio),
            }
          : {}),
      },
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
/**
 * What the live instance was built for. The scale's `range` and the axis's `splits` are functions
 * on the options object, so switching the fitted axis on or off is a rebuild rather than a redraw
 * -- cheap, since this chart holds no zoom or viewport state of its own, and once per user click.
 * A *changed* fit range is not a rebuild; see the watcher below.
 */
function plotSignature() {
  return `${isDark.value}|${fitAllSpectra.value}`
}
let builtFor = plotSignature()

function ensurePlot() {
  const el = containerRef.value
  if (!el) return
  if (plot && builtFor === plotSignature()) return

  plot?.destroy()
  const width = el.getBoundingClientRect().width || 300
  plot = new uPlot(buildOptions(width), [[], [], []], el)
  builtFor = plotSignature()
}

function update() {
  ensurePlot()
  if (!plot) return

  const merged = mergePreviewSeries(props.pointLeft?.previewFft ?? null, props.pointRight?.previewFft ?? null)
  plot.setData([merged.x, merged.leftMax, merged.rightMax])
}

watch(() => [props.pointLeft, props.pointRight], update)
// A new fit window -- the sweep gained a point, or an overlay was checked on -- is one setData
// away, because the y scale's range callback reads it. Compared by value rather than by identity:
// the parent recomputes the object, so watching the prop itself would redraw on every pass of a
// running sweep whether the window moved or not.
watch(() => [props.fitRange?.min, props.fitRange?.max], update)
// The theme and the toggle change the options object itself, which ensurePlot notices.
watch([isDark, fitAllSpectra], update)

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
</script>

<template>
  <section
    ref="rootRef"
    class="relative flex flex-col gap-1 panel-surface rounded-xl border border-neutral-200 p-2 dark:border-neutral-800"
  >
    <ExpandToggle class="absolute right-1 top-1" :target="rootRef" />
    <!-- pr-9 for the button, which is positioned absolutely and so makes no room for itself. -->
    <div class="flex items-center justify-between gap-2 pr-9 text-xs text-neutral-500 dark:text-neutral-400">
      <!-- min-w-0 so the label is what gives way when an overlay's name is long: a flex item will
           not shrink below its content without it, and the button and the per-channel readout were
           pushed off the panel's edge instead. -->
      <span class="flex min-w-0 items-center gap-2">
        <span class="truncate">{{ label ? `${label} — Spectrum (dBV)` : 'Spectrum (dBV)' }}</span>
        <!-- Shared by every one of these charts on the screen, so the live pair and each overlay's
             preview are drawn against one window. -->
        <button
          type="button"
          class="shrink-0 rounded-md border px-1.5 py-0 leading-5"
          :class="fitAllSpectra ? 'btn-action' : 'btn-toggle-off'"
          :title="
            fitAllSpectra
              ? `Fixed axis: one ${PREVIEW_GRID_STEP_DB} dB grid fitting every spectrum the sweep chart is showing. Click to scale each point on its own again.`
              : `Fix the axis to a ${PREVIEW_GRID_STEP_DB} dB grid fitting every spectrum the sweep chart is showing, so points can be compared as the cursor moves.`
          "
          :aria-pressed="fitAllSpectra"
          @click="fitAllSpectra = !fitAllSpectra"
        >
          Fit all
        </button>
      </span>
      <span class="flex shrink-0 items-center gap-2">
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
