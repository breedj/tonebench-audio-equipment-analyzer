<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import { STREAM_FLAG_ZOOMED, type SpectrumFrame } from '../streaming/protocol'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { useChartDisplay } from '../composables/useChartDisplay'
import { unitOffsetDb, unitSuffix } from '../spectrumUnits'
import { useAppTheme } from '../composables/useAppTheme'
// Shared with SpectrumChart.vue — the two charts sit on one x span and must label it identically.
import { freqAxis } from '../freqAxis'
import { axisLabel, CHART_EXPORT_HEADER_PX, composeChartPng, nextPaint, type ChartExportOptions } from '../charts/exportChart'
import { exportGridTheme } from '../charts/palette'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import ExpandToggle from './ExpandToggle.vue'

const props = defineProps<{
  spectrum: Record<number, SpectrumFrame>
}>()

// Same shared config the other two charts read — a channel the FFT settings panel has turned off
// must not keep feeding rows forever (the stream only ever adds per-channel entries, never removes
// a stale one on disable).
const { config: spectrumConfig } = useSpectrumConfig()
function channelEnabled(ch: number): boolean {
  if (!spectrumConfig.value) return true
  return ch === 0 ? spectrumConfig.value.processLeft : spectrumConfig.value.processRight
}

// The spectrum chart's live x span (see useChartDisplay.ts) — this chart deliberately has no zoom
// of its own: it is stacked directly under the spectrum and the two are only readable together if
// a column means the same frequency in both.
const { activeXRange, spectrumUnit, dutGainDb } = useChartDisplay()
const { isDark } = useAppTheme()

const CHANNEL_LABELS = ['L', 'R']
const CHART_HEIGHT = 260
/**
 * How many spectra of history to keep. At the default ~20 frames/s that's roughly 11 s; with
 * averaging on, frames arrive slower and the same 220 rows cover proportionally longer. Sized to
 * be about one row per pixel of plot area — more rows than that would just be thrown away by
 * drawImage's nearest-row pick, fewer would waste vertical resolution.
 */
const MAX_ROWS = 220
/** Colour resolution of the level → colour ramp. */
const LUT_SIZE = 256

/**
 * Jet-like ramp, dark blue (quiet) through cyan/green/yellow to red (loud) — the same reading
 * convention as the order-waterfall plots this chart is modelled on, and the one people already
 * expect from spectrograms. Deliberately not one of the perceptually-uniform maps: what matters
 * here is picking a *tone* out of a noise floor at a glance, which the sharp hue transitions of a
 * jet ramp do better than a monotone lightness ramp.
 */
const COLOR_STOPS: [number, number, number, number][] = [
  [0.0, 8, 12, 64],
  [0.12, 0, 24, 180],
  [0.25, 0, 96, 255],
  [0.38, 0, 200, 240],
  [0.5, 0, 220, 150],
  [0.62, 130, 230, 60],
  [0.75, 245, 220, 0],
  [0.87, 255, 128, 0],
  [1.0, 190, 0, 0],
]

/** COLOR_STOPS expanded to a flat RGB lookup table, so the per-pixel path is an array index. */
const COLOR_LUT = (() => {
  const lut = new Uint8Array(LUT_SIZE * 3)
  for (let i = 0; i < LUT_SIZE; i++) {
    const t = i / (LUT_SIZE - 1)
    let s = 0
    while (s < COLOR_STOPS.length - 2 && t > COLOR_STOPS[s + 1][0]) s++
    const [t0, r0, g0, b0] = COLOR_STOPS[s]
    const [t1, r1, g1, b1] = COLOR_STOPS[s + 1]
    const f = t1 === t0 ? 0 : (t - t0) / (t1 - t0)
    lut[i * 3] = r0 + (r1 - r0) * f
    lut[i * 3 + 1] = g0 + (g1 - g0) * f
    lut[i * 3 + 2] = b0 + (b1 - b0) * f
  }
  return lut
})()

/** The same ramp as a CSS gradient, for the colour key in the header. */
const legendGradient = `linear-gradient(to right, ${COLOR_STOPS.map(
  ([t, r, g, b]) => `rgb(${r},${g},${b}) ${(t * 100).toFixed(0)}%`,
).join(', ')})`

// Pixels no row covers — below the lowest column's frequency, above the highest, or the whole
// image before any data has arrived. putImageData overwrites alpha rather than compositing, so
// this has to be an opaque colour rather than a transparent one, or it would punch a hole through
// to whatever uPlot drew underneath.
const THEME = {
  dark: { axis: '#a3a3a3', grid: '#404040', gridWidth: 1, empty: [23, 23, 23] as const },
  light: { axis: '#525252', grid: '#e5e5e5', gridWidth: 1, empty: [245, 245, 245] as const },
} as const
type WaterfallTheme = (typeof THEME)[keyof typeof THEME]
function currentTheme(): WaterfallTheme {
  return isDark.value ? THEME.dark : THEME.light
}

/**
 * One captured spectrum. `freqs`/`values` are copied out of the frame rather than kept as views
 * into it: a SpectrumFrame's `columns` is a zero-copy view onto the whole received ArrayBuffer, so
 * holding 220 of them would pin 220 WebSocket buffers alive. `pixels` is this row rendered onto
 * the current plot grid, cached because it only changes when the grid or the level range does —
 * see gridKey.
 */
interface WaterfallRow {
  freqs: Float32Array
  values: Float32Array
  /** performance.now() at arrival — the y axis is real elapsed time, not a frame count. */
  timeMs: number
  /** 10th-percentile and maximum level in this row, for the auto level range. */
  loDb: number
  hiDb: number
  pixels: Uint8ClampedArray | null
}

// Oldest first, newest last.
const rows: WaterfallRow[] = []
// Which frame each row came from, so the other channel's arrival (the spectrum prop updates on
// both) doesn't append the same spectrum twice.
let lastSeq = -1

const rootRef = ref<HTMLElement | null>(null)
const containerRef = ref<HTMLDivElement>()
// Fitted only when expanded -- see charts/useResultChartHeight.ts's pageHeightPx. Under the
// spectrum chart this keeps the height it has always had, since the two share the view; expanded,
// the root is pinned over the viewport and the plot takes the window.
const chartHeight = useResultChartHeight(rootRef, containerRef, { pageHeightPx: CHART_HEIGHT })
const selectedChannel = ref(0)
const autoLevels = ref(true)
const topDb = ref(0)
const bottomDb = ref(-120)
const rowCount = ref(0)

// The full span, taken only from overview frames — a zoomed frame's x0/dx describe the zoom
// window, so sizing from one would make the fallback span wrong. Same reasoning as
// SpectrumChart.vue's fullLoFreqHz/fullHiFreqHz.
let fullLoFreqHz = 0
let fullHiFreqHz = 0

let plot: uPlot | null = null
let plotIsDark = isDark.value
// The y span (seconds of history) last pushed into the scale. uPlot only re-runs a scale's range
// callback for a scale that is auto or has a pending setScale — an auto:false scale like this one
// keeps whatever min/max it was first given, so without an explicit push the axis would stay stuck
// at the one-row span it was built with while the history grew behind it.
let lastYSpanS = -1
// The x span last pushed into the scale, for the same reason: an auto:false scale keeps whatever
// it was constructed with until something explicitly moves it. Null until the first push.
let lastXSpan: { min: number; max: number } | null = null
// Invalidates every row's cached pixels when the horizontal grid or the level range moves under
// them. Rows are re-rendered lazily on the next draw, not eagerly here.
let gridKey = ''

const channelAvailable = computed(() => [channelEnabled(0), channelEnabled(1)])

/** The x span this chart draws, mirroring the spectrum chart above. Null until a frame lands. */
function xSpan(): { min: number; max: number } | null {
  const active = activeXRange.value
  if (active && active.min > 0 && active.max > active.min) return active
  if (fullHiFreqHz > fullLoFreqHz && fullLoFreqHz > 0) return { min: fullLoFreqHz, max: fullHiFreqHz }
  return null
}

/** Seconds between the oldest and newest row — the y axis extent. Floored so a fresh, near-empty history doesn't collapse the scale. */
function historySeconds(): number {
  if (rows.length < 2) return 1
  return Math.max(0.5, (rows[rows.length - 1].timeMs - rows[0].timeMs) / 1000)
}

function buildOptions(
  width: number,
  height: number,
  span: { min: number; max: number },
  theme: WaterfallTheme,
  interactive: boolean,
): uPlot.Options {
  return {
    width,
    height,
    scales: {
      // Same log x scale as SpectrumChart. auto:false for the same reason it is there — setData
      // runs 20 times a second and an auto x scale would re-fit to the data on every one of them.
      // The consequence, and the thing this chart got wrong for a while: with auto:false, setData
      // re-applies the scale's *own* current min/max (uPlot's setData does
      // `xsc.auto(...) ? autoScaleX() : _setScale(xScaleKey, xsc.min, xsc.max)`) and never looks at
      // the data, so feeding it dummy points at the span edges moved nothing — the image followed
      // the spectrum's zoom while the axis labels stayed on the original full span. Following the
      // zoom is now an explicit setScale in update(); `range` only passes the pending min/max
      // through, substituting the construction span while the scale is still unset.
      x: { time: false, distr: 3, log: 10, auto: false, range: (_u, dataMin, dataMax) => [dataMin ?? span.min, dataMax ?? span.max] },
      // Age in seconds, negative so that uPlot's y-increases-upward puts "now" at the top and
      // history scrolls downward. The axis formatter flips the sign back for the labels.
      y: { auto: false, range: () => [-historySeconds(), 0] },
    },
    axes: [
      {
        ...freqAxis(),
        stroke: theme.axis,
        grid: { show: false },
        ticks: { stroke: theme.grid, width: theme.gridWidth },
        ...axisLabel(true, interactive, 'Frequency (Hz)'),
      },
      {
        // size: 50 matches SpectrumChart's y axis exactly, which is what keeps the two plot areas
        // horizontally aligned — a column at 1 kHz has to sit at the same x in both.
        size: 50,
        // Decimals chosen from the whole span, not per tick: deciding per tick mixes "8.0" and
        // "10" in one axis, since only some of them cross the threshold.
        values: (u, vals) => {
          const decimals = Math.abs(u.scales.y.min ?? 0) < 10 ? 1 : 0
          return vals.map((v) => (v == null ? null : `${(-v).toFixed(decimals)}`))
        },
        stroke: theme.axis,
        grid: { show: false },
        ticks: { stroke: theme.grid, width: theme.gridWidth },
        ...axisLabel(true, interactive, 'Seconds ago'),
      },
    ],
    // A placeholder: the image is the content (see waterfallPlugin), but uPlot still needs a
    // series to have a y scale and to run its draw pipeline at all.
    series: [{}, { stroke: 'transparent', points: { show: false } }],
    cursor: { show: false },
    legend: { show: false },
    plugins: [waterfallPlugin(theme)],
  }
}

/**
 * Renders one row onto the current pixel grid: each column paints from its own pixel rightward
 * until the next column's, which is the honest picture of a log axis — at low frequencies adjacent
 * FFT columns land many pixels apart, and leaving those pixels blank would draw a comb instead of
 * a spectrum. Pixels outside the row's own frequency coverage stay `empty`.
 */
function renderRow(row: WaterfallRow, width: number, logMin: number, logSpan: number, lo: number, hi: number, empty: readonly [number, number, number]): Uint8ClampedArray {
  const levels = new Float32Array(width).fill(Number.NaN)
  let firstPx = -1
  let lastPx = -1

  const n = row.freqs.length
  for (let i = 0; i < n; i++) {
    const freqHz = row.freqs[i]
    if (!(freqHz > 0)) continue
    const px = Math.floor(((Math.log10(freqHz) - logMin) / logSpan) * width)
    if (px < 0 || px >= width) continue
    const v = row.values[i]
    if (Number.isNaN(levels[px]) || v > levels[px]) levels[px] = v
    if (firstPx < 0) firstPx = px
    lastPx = px
  }

  const pixels = new Uint8ClampedArray(width * 4)
  const dbSpan = hi - lo || 1
  let carried = Number.NaN
  for (let px = 0; px < width; px++) {
    let v = levels[px]
    if (Number.isNaN(v) && px > firstPx && px <= lastPx) v = carried
    else if (!Number.isNaN(v)) carried = v

    const o = px * 4
    pixels[o + 3] = 255
    if (Number.isNaN(v)) {
      pixels[o] = empty[0]
      pixels[o + 1] = empty[1]
      pixels[o + 2] = empty[2]
      continue
    }
    let idx = Math.round(((v - lo) / dbSpan) * (LUT_SIZE - 1))
    idx = idx < 0 ? 0 : idx > LUT_SIZE - 1 ? LUT_SIZE - 1 : idx
    pixels[o] = COLOR_LUT[idx * 3]
    pixels[o + 1] = COLOR_LUT[idx * 3 + 1]
    pixels[o + 2] = COLOR_LUT[idx * 3 + 2]
  }
  return pixels
}

/**
 * Blits the history into the plot area. One ImageData covering the whole bbox, composed by
 * `set()`-ing each cached row into its pixel rows and written with a single putImageData — a
 * per-row drawImage would be 200+ scaled draw calls per frame, and rebuilding every pixel from
 * the raw levels each frame would be ~200k float→colour conversions at 20 Hz.
 *
 * putImageData works in backing-store pixels and ignores the canvas transform, which is the same
 * space uPlot's bbox is already in — no devicePixelRatio conversion needed on either side.
 */
function waterfallPlugin(theme: WaterfallTheme): uPlot.Plugin {
  // Reused across frames — reallocating a ~900x220 ImageData 20 times a second is pure garbage.
  // Per plugin instance, since the export's plot is a different size from the screen's.
  let image: ImageData | null = null
  return {
    hooks: {
      draw: [
        (u) => {
          if (rows.length === 0) return
          const left = Math.round(u.bbox.left)
          const top = Math.round(u.bbox.top)
          const width = Math.round(u.bbox.width)
          const height = Math.round(u.bbox.height)
          if (width <= 0 || height <= 0) return

          const span = xSpan()
          if (!span) return

          if (!image || image.width !== width || image.height !== height)
            image = new ImageData(width, height)
          const img = image

          const logMin = Math.log10(span.min)
          const logSpan = Math.log10(span.max) - logMin
          if (!(logSpan > 0)) return

          // The row cache is keyed on everything a rendered row depends on, the theme's empty
          // colour included — an export at another width or in the light theme re-renders every
          // row, and the screen's plot re-renders them back on its next draw.
          const key = `${width}|${span.min}|${span.max}|${bottomDb.value}|${topDb.value}|${theme.empty.join(',')}`
          if (key !== gridKey) {
            for (const row of rows) row.pixels = null
            gridKey = key
          }

          for (let y = 0; y < height; y++) {
            // y = 0 is the top of the plot, which is the newest row.
            const age = Math.min(rows.length - 1, Math.floor((y * rows.length) / height))
            const row = rows[rows.length - 1 - age]
            if (!row.pixels)
              row.pixels = renderRow(row, width, logMin, logSpan, bottomDb.value, topDb.value, theme.empty)
            img.data.set(row.pixels, y * width * 4)
          }

          u.ctx.putImageData(img, left, top)
        },
      ],
    },
  }
}

function ensurePlot(span: { min: number; max: number }) {
  const el = containerRef.value
  if (!el) return
  if (plot && plotIsDark === isDark.value) return

  plot?.destroy()
  const width = el.getBoundingClientRect().width || 800
  plot = new uPlot(buildOptions(width, chartHeight.value, span, currentTheme(), true), [[span.min, span.max], [null, null]], el)
  plotIsDark = isDark.value
  lastYSpanS = -1
  lastXSpan = { min: span.min, max: span.max }
  gridKey = ''
}

/** The 10th-percentile level in a row — the noise floor, near enough, and the sensible bottom of an auto range. */
function percentile10(values: Float32Array): number {
  const sorted = values.slice().sort()
  return sorted[Math.floor(sorted.length * 0.1)] ?? sorted[0] ?? 0
}

function pushRow(frame: SpectrumFrame) {
  // Rows are stored in the chart's unit (see spectrumUnits.ts), converted at ingest: the history
  // is one picture, and App.vue clears it when the unit changes. A frame that cannot be converted
  // yet (no ENBW under a density unit) is not a row.
  const offset = unitOffsetDb(spectrumUnit.value, frame.enbwHz, dutGainDb.value)
  if (offset === null) return
  const n = frame.header.nPoints
  const freqs = new Float32Array(n)
  const values = new Float32Array(n)
  for (let i = 0; i < n; i++) {
    freqs[i] = frame.columns[i * 3]
    values[i] = frame.columns[i * 3 + 2] + offset
  }

  let hiDb = -Infinity
  for (let i = 0; i < n; i++) if (values[i] > hiDb) hiDb = values[i]

  rows.push({ freqs, values, timeMs: performance.now(), loDb: percentile10(values), hiDb, pixels: null })
  if (rows.length > MAX_ROWS) rows.shift()
  rowCount.value = rows.length
}

/**
 * Auto level range: the quietest row's noise floor to the loudest row's peak, rounded out to 10 dB
 * so the numbers in the fields stay round and the range stops twitching frame to frame. Only ever
 * widens or narrows in whole 10 dB steps for that reason — a range recomputed to the exact extent
 * every frame makes the whole picture shimmer as the floor wanders by a fraction of a dB.
 */
function updateAutoLevels() {
  if (!autoLevels.value || rows.length === 0) return
  let lo = Infinity
  let hi = -Infinity
  for (const row of rows) {
    if (Number.isFinite(row.loDb) && row.loDb < lo) lo = row.loDb
    if (Number.isFinite(row.hiDb) && row.hiDb > hi) hi = row.hiDb
  }
  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return

  const nextBottom = Math.floor(lo / 10) * 10
  const nextTop = Math.max(nextBottom + 10, Math.ceil(hi / 10) * 10)
  if (nextBottom !== bottomDb.value) bottomDb.value = nextBottom
  if (nextTop !== topDb.value) topDb.value = nextTop
}

function clearHistory() {
  rows.length = 0
  rowCount.value = 0
  lastSeq = -1
  lastYSpanS = -1
  plot?.redraw()
}

/**
 * The waterfall as a PNG of exactly the requested size, in the light theme — the FFT report's
 * Waterfall element. Only this instance can draw it: the picture is the history `rows` collected
 * frame by frame, which a headless copy would not have, so the report reads it off the chart on
 * screen (see report/render.ts renderExtraCharts). A second uPlot is built offscreen over the same
 * rows and the same span; the plugin re-renders every row onto the export's wider grid, and the
 * visible plot renders them back on its next draw.
 */
async function exportPng({ width, height }: ChartExportOptions): Promise<Blob> {
  const span = xSpan()
  if (rows.length === 0 || !span) throw new Error('The waterfall has no history to export.')

  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header

  const host = document.createElement('div')
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(host)

  let u: uPlot | null = null
  try {
    u = new uPlot(buildOptions(width, plotHeight, span, exportGridTheme(THEME.light), false), [[span.min, span.max], [null, null]], host)
    u.setScale('y', { min: -historySeconds(), max: 0 })
    await nextPaint()
    // The screen's plot keys its row cache on its own grid; its next draw restores it.
    gridKey = ''

    const seconds = historySeconds()
    const title = `Waterfall — ${CHANNEL_LABELS[selectedChannel.value]}, ${rows.length} spectra over ${seconds.toFixed(1)} s, ${bottomDb.value} to ${topDb.value} ${unitSuffix(spectrumUnit.value)}`
    return await composeChartPng({ width, height, plotCanvas: u.ctx.canvas, headerHeight: header, title, legend: [] })
  } finally {
    u?.destroy()
    host.remove()
    plot?.redraw()
  }
}

defineExpose({ exportPng, clearHistory })

function update() {
  // App.vue empties the whole spectrum record when a run starts — history from the previous run
  // has nothing to do with this one, and would otherwise sit at the bottom of the chart drifting
  // upward for the next 11 seconds.
  if (Object.keys(props.spectrum).length === 0) {
    if (rows.length > 0) clearHistory()
    return
  }

  const frame = props.spectrum[selectedChannel.value]
  if (!frame) return

  if ((frame.header.flags & STREAM_FLAG_ZOOMED) === 0) {
    fullLoFreqHz = frame.loFreqHz
    fullHiFreqHz = frame.hiFreqHz
  }

  const span = xSpan()
  if (!span) return

  // The prop updates once per channel per tick; only the selected channel's own frame is a new row.
  if (frame.header.seq !== lastSeq) {
    lastSeq = frame.header.seq
    pushRow(frame)
    updateAutoLevels()
  }

  ensurePlot(span)
  if (!plot) return
  // Two points at the span edges, so the x data extent matches the scale; the y series is all-null
  // — the picture is the plugin's image, not a line.
  plot.setData([[span.min, span.max], [null, null]])

  // Both setScales after setData, never before: setData re-applies the scales synchronously and
  // would clobber a pending setScale (the same ordering SpectrumChart's zoom restore needs). Each
  // is gated on a visible change so a settled chart isn't re-scaling on every frame.
  //
  // The x push is what makes the axis labels follow the spectrum chart's zoom — the plugin's image
  // is drawn straight from xSpan(), so without it the picture zooms and the labels don't.
  if (!lastXSpan || lastXSpan.min !== span.min || lastXSpan.max !== span.max) {
    lastXSpan = { min: span.min, max: span.max }
    plot.setScale('x', { min: span.min, max: span.max })
  }

  const ySpanS = historySeconds()
  if (Math.abs(ySpanS - lastYSpanS) > 0.05) {
    lastYSpanS = ySpanS
    plot.setScale('y', { min: -ySpanS, max: 0 })
  }
}

watch(() => props.spectrum, update)
watch(selectedChannel, () => {
  // A different channel's history was never captured, so there is nothing to re-render — start over.
  clearHistory()
  update()
})
// Not a bare redraw: following the spectrum chart's zoom means both re-rendering every row onto
// the new pixel grid and moving the x scale, and update() is what does the second half.
watch(activeXRange, update)
watch([topDb, bottomDb], () => plot?.redraw())
watch(autoLevels, () => {
  updateAutoLevels()
  plot?.redraw()
})

// Follows the FFT settings panel: a channel that isn't being processed has no frames to show, so
// selecting it would leave the chart permanently blank.
watch(channelAvailable, ([left, right]) => {
  if (selectedChannel.value === 0 && !left && right) selectedChannel.value = 1
  else if (selectedChannel.value === 1 && !right && left) selectedChannel.value = 0
})

// Observes the container rather than the window: the column this chart sits in also changes width
// when the measurement sidebar is pinned or unpinned, which is not a window resize — measured, the
// canvas stayed at 432 px inside a column that had grown to 661 px. Same mechanism as the
// ResizeObserver in FrequencyResponseChart.
let resizeObserver: ResizeObserver | null = null
function handleResize() {
  if (!plot || !containerRef.value) return
  // Zero while the panel is hidden (App.vue's v-show) -- a 0 px canvas is not a size to keep; the
  // observer fires again with the real width when the panel is shown.
  const width = containerRef.value.getBoundingClientRect().width
  if (width > 0) plot.setSize({ width, height: chartHeight.value })
}
watch(chartHeight, handleResize)

// Axis colours are baked into the options at construction, same as the other two charts — a
// Light/Dark/Auto change is one of the few things that legitimately needs the instance rebuilt.
watch(isDark, () => {
  const span = xSpan()
  if (!plot || !span) return
  plot.destroy()
  plot = null
  ensurePlot(span)
  update()
})

onMounted(() => {
  resizeObserver = new ResizeObserver(handleResize)
  if (containerRef.value) resizeObserver.observe(containerRef.value)
})
onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  plot?.destroy()
  plot = null
})
</script>

<template>
  <section
    ref="rootRef"
    class="relative flex flex-col gap-2 panel-surface rounded-lg border border-neutral-200 p-3 dark:border-neutral-800"
  >
    <ExpandToggle class="absolute right-2 top-2" :target="rootRef" />
    <!-- pr-9 for the button, which is positioned absolutely and so makes no room for itself. -->
    <div class="flex flex-wrap items-center gap-3 pr-9 text-xs text-neutral-500 dark:text-neutral-400">
      <span>Waterfall</span>

      <div class="flex overflow-hidden rounded border border-neutral-300 dark:border-neutral-700">
        <button
          v-for="(label, ch) in CHANNEL_LABELS"
          :key="label"
          type="button"
          class="px-2 py-0.5 disabled:opacity-40"
          :class="selectedChannel === ch ? 'bg-neutral-200 text-neutral-900 dark:bg-neutral-700 dark:text-neutral-100' : 'hover:bg-neutral-100 dark:hover:bg-neutral-800'"
          :disabled="!channelAvailable[ch]"
          title="Which channel's history to show. One at a time — a waterfall has already spent its colour axis on level."
          @click="selectedChannel = ch"
        >
          {{ label }}
        </button>
      </div>

      <label class="flex items-center gap-1.5" title="Track the level range to the history currently on screen.">
        <input v-model="autoLevels" type="checkbox" />
        Auto levels
      </label>

      <div class="flex items-center gap-1.5">
        <input
          v-model.number="bottomDb"
          type="number"
          step="10"
          :disabled="autoLevels"
          class="w-16 rounded border border-neutral-300 px-1.5 py-0.5 text-xs disabled:opacity-50 dark:border-neutral-700"
          title="Level mapped to the coldest colour, in the chart's unit."
        />
        <span class="h-3 w-24 rounded-sm" :style="{ background: legendGradient }" aria-hidden="true"></span>
        <input
          v-model.number="topDb"
          type="number"
          step="10"
          :disabled="autoLevels"
          class="w-16 rounded border border-neutral-300 px-1.5 py-0.5 text-xs disabled:opacity-50 dark:border-neutral-700"
          title="Level mapped to the hottest colour, in the chart's unit."
        />
        <span>{{ unitSuffix(spectrumUnit) }}</span>
      </div>

      <button
        type="button"
        class="rounded border px-2 py-0.5 btn-action"
        title="Throws away the history and starts collecting fresh rows."
        @click="clearHistory"
      >
        Clear
      </button>

      <span class="flex-1"></span>
      <span v-if="rowCount === 0">Waiting for data…</span>
      <span v-else class="font-mono">s ago ↓</span>
    </div>
    <div ref="containerRef" class="w-full"></div>
  </section>
</template>
