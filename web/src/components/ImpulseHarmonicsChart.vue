<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { HarmonicCurveJson } from '../api/client'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { CHART_EXPORT_HEADER_PX, composeChartPng, nextPaint, type ChartExportOptions } from '../charts/exportChart'
import ExpandToggle from './ExpandToggle.vue'
import ToolbarIcon from './ToolbarIcon.vue'
import { useAppTheme } from '../composables/useAppTheme'
import type { OverlayEntry } from '../composables/useMeasurements'
import { useZoomBounds } from '../composables/useZoomBounds'
import { sweepOverlayDash } from '../measurements/sweepOverlayStyles'
import { freqAxis } from '../freqAxis'
import { exportGridTheme } from '../charts/palette'

/**
 * The DUT's harmonic distortion against the **fundamental** frequency that produced it, pulled out
 * of the same sweep the impulse response came from.
 *
 * <para>Each order stops at its own frequency, and the chart has to respect that rather than run
 * every curve to the same right-hand edge: the deconvolution divides by the stimulus, which has no
 * energy above the sweep's end, so the k-th harmonic can only be observed for fundamentals up to
 * f₂/k. H5 therefore ends at a fifth of the sweep's top. Drawing past `upperValidHz` would be
 * drawing frequencies nothing measured.</para>
 */
const props = defineProps<{
  harmonics: HarmonicCurveJson[]
  /**
   * Checked history items. Unlike the two decay surfaces this chart can genuinely overlay — colour
   * still means the order, so H3 is the same colour in every run, and the dash says which run.
   */
  overlays: OverlayEntry[]
  thickLines: boolean
  showDataPoints: boolean
  freqFromHz: number
  freqToHz: number
  yTopDb: number
  yBottomDb: number
}>()

const { isDark } = useAppTheme()

const root = ref<HTMLElement | null>(null)
const container = ref<HTMLDivElement | null>(null)
// Fitted to the view — see charts/useResultChartHeight.ts. uPlot sizes the container itself, so a
// new height is applied through setSize, the same call a width change makes below.
const chartHeight = useResultChartHeight(root, container)
const plot = shallowRef<uPlot | null>(null)

/** Matches the sweep chart's harmonic colours, so H2 means the same colour on both screens. */
const ORDER_COLORS: Record<number, { dark: string; light: string }> = {
  2: { dark: '#60a5fa', light: '#1d6fd0' },
  3: { dark: '#4ade80', light: '#15803d' },
  4: { dark: '#fbbf24', light: '#b45309' },
  5: { dark: '#f87171', light: '#c62828' },
}

const THEME = {
  dark: { axis: '#a3a3a3', grid: '#404040', gridWidth: 1 },
  light: { axis: '#525252', grid: '#e5e5e5', gridWidth: 1 },
} as const

type Theme = (typeof THEME)[keyof typeof THEME]

// Threaded through as an argument rather than read at draw time — the export builds a second, light
// plot while the screen may be dark. See charts/palette.ts.
function themeFor(dark: boolean, forExport = false): Theme {
  const theme = dark ? THEME.dark : THEME.light
  return forExport ? exportGridTheme(theme) : theme
}

function colorFor(order: number, dark: boolean = isDark.value): string {
  const pair = ORDER_COLORS[order] ?? ORDER_COLORS[5]
  return dark ? pair.dark : pair.light
}

const ordered = computed(() => [...props.harmonics].sort((a, b) => a.order - b.order))

/**
 * Every curve to draw, flattened: the live run's orders first, then each checked history item's.
 * One list so the series, the data columns and the legend are built from the same order — the same
 * arrangement the sweep chart uses, and for the same reason.
 */
const plotted = computed(() =>
  [
    { dash: undefined as number[] | undefined, curves: ordered.value },
    ...props.overlays.map((o) => ({
      dash: sweepOverlayDash(props.overlays, o.id, props.harmonics.length > 0) ?? undefined,
      curves: [...(o.harmonicCurves ?? [])].sort((a, b) => a.order - b.order),
    })),
  ].flatMap((run) => run.curves.map((curve) => ({ curve, dash: run.dash }))),
)

/**
 * One shared x axis with every curve resampled onto it. uPlot wants a single x series, and the
 * orders arrive on different grids (each packet is transformed at its own length, then relabelled
 * f → f/k), so they have to be merged rather than passed through.
 */
/**
 * One entry per order actually drawn, whichever run it came from — the legend names the colours, and
 * the colours mean orders. A legend built from the live curves alone read "No harmonic curves" over a
 * chart full of a checked history item's, which is how the overlay looked broken when it was not.
 *
 * <p>The validity limit shown is the widest of the runs carrying that order: it is the frequency past
 * which <em>nothing</em> on screen has a curve.</p>
 */
const legendOrders = computed(() => {
  const widest = new Map<number, number>()
  for (const { curve } of plotted.value) {
    widest.set(curve.order, Math.max(widest.get(curve.order) ?? 0, curve.upperValidHz))
  }
  return [...widest.entries()].sort((a, b) => a[0] - b[0]).map(([order, upperValidHz]) => ({ order, upperValidHz }))
})

const data = computed<uPlot.AlignedData>(() => {
  const curves = plotted.value.map((p) => p.curve)
  if (curves.length === 0) return [[], []] as unknown as uPlot.AlignedData

  const xs = new Set<number>()
  for (const curve of curves) for (const f of curve.fundamentalHz) xs.add(f)
  const x = [...xs].sort((a, b) => a - b)

  const columns: (number | null)[][] = curves.map((curve) => {
    const out = new Array<number | null>(x.length).fill(null)
    let source = 0
    for (let i = 0; i < x.length; i++) {
      // Past this order's own band there is nothing to draw, and a null is how uPlot leaves a gap.
      if (x[i] > curve.upperValidHz) break
      while (source < curve.fundamentalHz.length - 1 && curve.fundamentalHz[source] < x[i]) source++
      if (curve.fundamentalHz.length > 0) out[i] = curve.levelDbc[source]
    }
    return out
  })

  return [x, ...columns] as unknown as uPlot.AlignedData
})

/**
 * True while either axis shows something other than the window the display panel asks for. Drives
 * the Reset zoom button, which is the only visible sign the axes are no longer the panel's.
 */
const { zoomed, noteScale, resetZoom } = useZoomBounds(
  () => plot.value,
  () => ({ xMin: props.freqFromHz, xMax: props.freqToHz, yMin: props.yBottomDb, yMax: props.yTopDb }),
)

/**
 * @param dark Which theme to build with — see themeFor.
 * @param interactive False for the export's offscreen plot: no cursor and no zoom bookkeeping.
 */
function buildOptions(width: number, height: number, dark: boolean, interactive: boolean, forExport = false): uPlot.Options {
  const t = themeFor(dark, forExport)
  return {
    width,
    height,
    scales: {
      // What puts the panel's window on the x axis is applyScales()'s explicit setScale, not the
      // `??` here: on an x scale that fallback is dead code, because uPlot only ever passes null for
      // x when there is no data yet. The pass-through still matters — it is what lets a drag-zoom
      // survive — but drop applyScales and this axis silently reverts to the data's own span, which
      // is the bug the frequency-response and impedance charts had. See CLAUDE.md.
      x: {
        time: false,
        distr: 3,
        log: 10,
        auto: false,
        range: (_u, min, max) => [min ?? props.freqFromHz, max ?? props.freqToHz],
      },
      y: { auto: false, range: (_u, min, max) => [min ?? props.yBottomDb, max ?? props.yTopDb] },
    },
    axes: [
      {
        ...freqAxis(),
        label: 'Fundamental (Hz)',
        stroke: t.axis,
        grid: { stroke: t.grid, width: t.gridWidth },
        ticks: { stroke: t.grid, width: t.gridWidth },
      },
      {
        label: 'Level (dBc)',
        size: 64,
        stroke: t.axis,
        grid: { stroke: t.grid, width: t.gridWidth },
        ticks: { stroke: t.grid, width: t.gridWidth },
      },
    ],
    series: [
      { label: 'Fundamental (Hz)' },
      ...plotted.value.map(({ curve, dash }) => ({
        label: `H${curve.order}`,
        stroke: colorFor(curve.order, dark),
        width: props.thickLines ? 2 : 1,
        dash,
        points: { show: props.showDataPoints, size: 4 },
        alpha: dash ? 0.7 : undefined,
      })),
    ],
    // Both axes: narrowing onto the band a DUT distorts in is the common gesture, but a dBc window
    // matters just as much on a curve that sits 90 dB down.
    cursor: interactive ? { drag: { x: true, y: true, uni: 12 } } : { show: false },
    legend: { show: false },
    // Fires on setData too (both scales are auto:false, so setData re-applies the same window),
    // hence the change gate inside noteScale.
    hooks: interactive ? { setScale: [(u: uPlot) => noteScale(u)] } : {},
  }
}

function rebuild() {
  const el = container.value
  if (!el) return

  plot.value?.destroy()
  const width = el.getBoundingClientRect().width || 600
  plot.value = new uPlot(buildOptions(width, chartHeight.value, isDark.value, true), data.value, el)
  // uPlot has no double-click-to-reset of its own once drag zoom is on.
  plot.value.over.addEventListener('dblclick', resetZoom)
  applyScales()
}

/**
 * The chart as a PNG of exactly the requested size, light whatever the theme — the same arrangement
 * as FrequencyResponseChart.exportPng. This chart's own ordering is kept: data to the constructor,
 * then the explicit setScale pair that puts the panel's window on the axes.
 */
async function exportPng({ width, height, captureZoom = false }: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header

  const host = document.createElement('div')
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(host)

  const live = plot.value
  const xWindow =
    captureZoom && live?.scales.x.min != null && live.scales.x.max != null
      ? { min: live.scales.x.min, max: live.scales.x.max }
      : { min: props.freqFromHz, max: props.freqToHz }
  const yWindow =
    captureZoom && live?.scales.y.min != null && live.scales.y.max != null
      ? { min: live.scales.y.min, max: live.scales.y.max }
      : { min: props.yBottomDb, max: props.yTopDb }

  let u: uPlot | null = null
  try {
    u = new uPlot(buildOptions(width, plotHeight, false, false, true), data.value, host)
    u.setScale('x', xWindow)
    u.setScale('y', yWindow)
    await nextPaint()

    return await composeChartPng({
      width,
      height,
      plotCanvas: u.ctx.canvas,
      headerHeight: header,
      title: 'Harmonic distortion vs fundamental',
      legend: legendOrders.value.map(({ order }) => ({ label: `H${order}`, color: colorFor(order, false) })),
    })
  } finally {
    u?.destroy()
    host.remove()
  }
}

defineExpose({ exportPng })

function applyScales() {
  const p = plot.value
  if (!p) return
  p.setScale('x', { min: props.freqFromHz, max: props.freqToHz })
  p.setScale('y', { min: props.yBottomDb, max: props.yTopDb })
}

function refresh() {
  const p = plot.value
  if (!p) return
  // setData on the existing instance; the plot is only re-created when the series set changes.
  p.setData(data.value)
  applyScales()
}

let observer: ResizeObserver | null = null

function fit() {
  const el = container.value
  if (el && plot.value) plot.value.setSize({ width: el.getBoundingClientRect().width, height: chartHeight.value })
}
watch(chartHeight, fit)

onMounted(() => {
  rebuild()
  observer = new ResizeObserver(fit)
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
  plot.value?.destroy()
  plot.value = null
})

// The series set changes when the orders do, which needs a rebuild; new values do not.
watch(
  [
    isDark,
    () => props.thickLines,
    () => props.showDataPoints,
    // Series count and colours are baked in at construction, so a run appearing or disappearing is a
    // rebuild rather than a redraw.
    () => plotted.value.map((p) => `${p.curve.order}${p.dash?.join('') ?? ''}`).join(','),
  ],
  rebuild,
)
watch(
  [() => props.harmonics, () => props.overlays, () => props.freqFromHz, () => props.freqToHz, () => props.yTopDb, () => props.yBottomDb],
  refresh,
)
</script>

<template>
  <!-- A card like every panel beside it — the 2026-10-12 mockups. -->
  <section ref="root" class="relative flex flex-col gap-1 panel-surface rounded-xl border border-neutral-200 p-3 dark:border-neutral-800">
    <ExpandToggle class="absolute right-3 top-3" :target="root" />
    <div class="min-h-6 pr-9 flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>Harmonic distortion vs fundamental</span>
      <span class="flex items-center gap-3">
        <span v-for="entry in legendOrders" :key="entry.order" class="flex items-center gap-1">
          <span class="h-1.5 w-4 rounded" :style="{ background: colorFor(entry.order) }"></span>
          H{{ entry.order }} ≤ {{ (entry.upperValidHz / 1000).toFixed(2) }} kHz
        </span>
        <span v-if="legendOrders.length === 0">No harmonic curves</span>
      </span>

      <button
        v-if="zoomed"
        type="button"
        class="inline-flex shrink-0 items-center gap-1 rounded border px-2 py-0.5 btn-action"
        title="Back to the frequency and dBc window set in the display panel."
        @click="resetZoom"
      >
        <ToolbarIcon name="reset-zoom" />
        Reset zoom
      </button>
      <span v-else class="shrink-0">Drag to zoom · double-click to reset</span>
    </div>
    <div ref="container" class="w-full"></div>
  </section>
</template>
