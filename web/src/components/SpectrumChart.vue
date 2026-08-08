<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import { STREAM_FLAG_ZOOMED, type MetersFrame, type SpectrumFrame } from '../streaming/protocol'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'

const props = defineProps<{
  spectrum: Record<number, SpectrumFrame>
  meters: Record<number, MetersFrame>
}>()

const emit = defineEmits<{
  /** The frequency range now on screen, or null when zoomed all the way out. */
  viewportChange: [viewport: { loFreqHz: number; hiFreqHz: number } | null]
}>()

// Shared singleton (see useSpectrumConfig.ts) — reads whatever AcquisitionPanel's L/R checkboxes
// last set, so a channel that's been turned off doesn't keep showing its last frame forever (the
// spectrum stream only ever adds entries per channel, never removes a stale one).
const { config: spectrumConfig } = useSpectrumConfig()

function channelEnabled(ch: number): boolean {
  if (!spectrumConfig.value) return true
  return ch === 0 ? spectrumConfig.value.processLeft : spectrumConfig.value.processRight
}

const CHANNEL_LABELS = ['L', 'R']
const CHANNEL_COLORS = ['#3b82f6', '#f97316']
// Same hues at low alpha: the band is dense (one stroke per column) so it reads as a solid region
// even this faint, and the max trace has to stay legible on top of it.
const CHANNEL_BAND_COLORS = ['rgba(59, 130, 246, 0.35)', 'rgba(249, 115, 22, 0.35)']
const CHART_HEIGHT = 360

// uPlot draws axis labels, ticks and grid onto a canvas, so Tailwind's `dark:` classes can't reach
// them — left unset it falls back to its own near-black defaults, which are unreadable on the dark
// background. These are canvas colours, chosen to match the surrounding Tailwind neutral palette:
// neutral-400/neutral-700 on dark, neutral-600/neutral-200 on light.
const THEME = {
  dark: {
    axis: '#a3a3a3',
    grid: '#404040',
    markerLine: 'rgba(234, 179, 8, 0.45)',
    markerText: 'rgba(250, 204, 21, 0.95)',
  },
  light: {
    axis: '#525252',
    grid: '#e5e5e5',
    markerLine: 'rgba(234, 179, 8, 0.5)',
    markerText: 'rgba(161, 98, 7, 0.95)',
  },
} as const

// Tailwind v4 defaults to the `prefers-color-scheme` media query (this app sets no darkMode
// override and has no theme toggle), so the OS preference is the single source of truth.
const darkQuery = window.matchMedia('(prefers-color-scheme: dark)')
function currentTheme() {
  return darkQuery.matches ? THEME.dark : THEME.light
}

const containerRef = ref<HTMLDivElement>()
const cursorReadout = ref<{ freqHz: number; dbv: number } | null>(null)

// uPlot instance is created exactly once and reused via setData()/setSize() for the component's
// whole lifetime — re-instantiating per frame is the single biggest perf mistake for a
// streaming chart like this.
let plot: uPlot | null = null
// The full-span edges, taken only from overview frames. A zoomed frame's x0/dx describe the zoom
// window, so sizing the axis from one would shrink the axis to the zoom and make zooming out
// impossible. See STREAM_FLAG_ZOOMED.
let fullLoFreqHz = 10
let fullHiFreqHz = -1
let plotNyquistHz = -1
let plotChannelCount = -1
// Axis colours are baked into the options at construction, so a theme flip is one of the few
// things that legitimately requires rebuilding the instance.
let plotIsDark = darkQuery.matches

// The server sends min *and* max per decimated column; plotting only the max threw away half of
// what decimation deliberately preserved, and made our noise floor read as a thin line ~6 dB above
// where QuantAsylum's app draws a thick band. QA403 draws every bin, so each screen column becomes a
// vertical stroke from that column's min to its max — this reproduces that directly from the min/max
// pair rather than trying to fake it from the max alone.
//
// Drawn in `drawAxes` (fires after the grid, before the series) so the band sits above the gridlines
// and under each channel's max trace. One Path2D per channel, stroked once — 1600 columns × 2
// channels per frame is fine as a single batched path, but not as 3200 individual stroke calls.
function minMaxBandPlugin(getSpectrum: () => Record<number, SpectrumFrame>): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          const spectrum = getSpectrum()
          const ctx = u.ctx
          ctx.save()

          // Clip to the plot area: a column whose min falls below the y-scale would otherwise
          // paint over the axis labels.
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          for (let ch = 0; ch < CHANNEL_COLORS.length; ch++) {
            if (!channelEnabled(ch)) continue
            const frame = spectrum[ch]
            if (!frame) continue

            const path = new Path2D()
            const { columns, header } = frame
            for (let i = 0; i < header.nPoints; i++) {
              const freqHz = columns[i * 3]
              const minDb = columns[i * 3 + 1]
              const maxDb = columns[i * 3 + 2]
              // Column 0 can sit at or below the log x-scale minimum, where valToPos is -Infinity.
              if (!(freqHz > 0)) continue

              const x = u.valToPos(freqHz, 'x', true)
              path.moveTo(x, u.valToPos(minDb, 'y', true))
              path.lineTo(x, u.valToPos(maxDb, 'y', true))
            }

            ctx.strokeStyle = CHANNEL_BAND_COLORS[ch]
            ctx.lineWidth = 1
            ctx.stroke(path)
          }

          ctx.restore()
        },
      ],
    },
  }
}

// Reporting the visible range upstream is what lets the server spend its whole column budget on
// what's actually on screen. Debounced and change-gated because uPlot fires setScale on every
// setData too (the x scale is auto:false, so setData re-applies the same min/max every frame) —
// without both guards this would emit 20 times a second saying nothing new.
let viewportTimer: ReturnType<typeof setTimeout> | undefined
let lastReportedLo = Number.NaN
let lastReportedHi = Number.NaN

function reportViewport(u: uPlot) {
  const min = u.scales.x.min
  const max = u.scales.x.max
  if (min == null || max == null || fullHiFreqHz <= 0) return

  // Treat "essentially the whole span" as not zoomed, so nudging the edges doesn't leave the
  // server encoding a private frame that buys nothing.
  const atFullSpan = min <= fullLoFreqHz * 1.001 && max >= fullHiFreqHz * 0.999
  const lo = atFullSpan ? Number.NaN : min
  const hi = atFullSpan ? Number.NaN : max
  if (Object.is(lo, lastReportedLo) && Object.is(hi, lastReportedHi)) return

  lastReportedLo = lo
  lastReportedHi = hi

  clearTimeout(viewportTimer)
  viewportTimer = setTimeout(
    () => emit('viewportChange', atFullSpan ? null : { loFreqHz: min, hiFreqHz: max }),
    150,
  )
}

function harmonicMarkersPlugin(): uPlot.Plugin {
  return {
    hooks: {
      draw: [
        (u) => {
          const ch0 = props.meters[0]
          if (!ch0 || ch0.harmonics.length === 0) return

          const theme = currentTheme()
          const ctx = u.ctx
          ctx.save()
          ctx.strokeStyle = theme.markerLine
          ctx.fillStyle = theme.markerText
          ctx.font = '10px ui-monospace, monospace'
          ctx.lineWidth = 1

          for (const h of ch0.harmonics) {
            if (h.freqHz <= 0) continue
            const x = u.valToPos(h.freqHz, 'x', true)
            if (x < u.bbox.left || x > u.bbox.left + u.bbox.width) continue

            ctx.beginPath()
            ctx.moveTo(x, u.bbox.top)
            ctx.lineTo(x, u.bbox.top + u.bbox.height)
            ctx.stroke()
            ctx.fillText(`${h.dbc.toFixed(0)}`, x + 3, u.bbox.top + 10)
          }

          ctx.restore()
        },
      ],
      setScale: [
        (u, key) => {
          if (key === 'x') reportViewport(u)
        },
      ],
      setCursor: [
        (u) => {
          const idx = u.cursor.idx
          const xs = u.data[0]
          const ys = u.data[1]
          if (idx == null || xs[idx] == null || ys?.[idx] == null) {
            cursorReadout.value = null
            return
          }
          cursorReadout.value = { freqHz: xs[idx] as number, dbv: ys[idx] as number }
        },
      ],
    },
  }
}

// uPlot ticks/labels a log axis at more split positions than it wants text on — positions with
// no label come through as `null` and must stay `null` (not get stringified into the text "null").
function formatFreqTick(v: number | null): string | null {
  if (v == null) return null
  return v >= 1000 ? `${(v / 1000).toFixed(v >= 10000 ? 0 : 1)}k` : `${v}`
}

function formatDbTick(v: number | null): string | null {
  return v == null ? null : `${v}`
}

function buildOptions(width: number, nyquistHz: number, channelCount: number): uPlot.Options {
  const theme = currentTheme()
  const series: uPlot.Series[] = [{}]
  for (let ch = 0; ch < channelCount; ch++)
    series.push({ label: CHANNEL_LABELS[ch], stroke: CHANNEL_COLORS[ch], width: 1.5, points: { show: false } })

  return {
    width,
    height: CHART_HEIGHT,
    scales: {
      // auto:false — uPlot's x scale defaults to auto-ranging from each setData() call's own
      // data, which re-fits the view to the incoming frame on every one of these (up to
      // targetFrameRateHz times a second) and silently overwrites any zoom/pan the user just did.
      // With auto:false, setData() re-applies the scale's own current min/max instead (see
      // uPlot's setData: `xsc.auto(...) ? autoScaleX() : _setScale(xScaleKey, xsc.min, xsc.max)`).
      //
      // `range` (a callback), not `min`/`max` directly: the plot is first constructed with empty
      // data (see ensurePlot below), and uPlot only ever seeds a scale's live min/max from `range`
      // — static `min`/`max` fields are read *by* the default range fn, they aren't a substitute
      // for one. With no `range` callback, the built-in log-range fn hands back [null, null] for
      // zero data, and since auto:false means nothing ever recomputes it later, the x scale would
      // stay stuck at null forever — every point maps to an invalid position and the chart never
      // draws again once real data replaces the empty construction data.
      //
      // Passing dataMin/dataMax straight through (only substituting when null) matters just as
      // much as auto:false itself: setScales() unconditionally re-derives the x-series' range by
      // calling this same fn with whatever's pending — including the exact min/max a user's drag-
      // zoom just requested via uPlot's own setScale(). A version that ignored those args and
      // always returned the fixed default (tried first) fed the zoomed request straight back into
      // range() and got the full range back, silently overriding every zoom the instant it was
      // set — the same bug as before, just moved one level down.
      x: { time: false, distr: 3, log: 10, auto: false, range: (_u, dataMin, dataMax) => [dataMin ?? 10, dataMax ?? nyquistHz] },
      y: { range: [-180, 20] },
    },
    axes: [
      {
        space: 60,
        values: (_u, vals) => vals.map(formatFreqTick),
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
      {
        values: (_u, vals) => vals.map(formatDbTick),
        size: 50,
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
    ],
    series,
    cursor: { points: { show: false } },
    legend: { show: true },
    plugins: [minMaxBandPlugin(() => props.spectrum), harmonicMarkersPlugin()],
  }
}

// The wire payload interleaves (freqHz,min,max) per column. The uPlot series is the max — it never
// misses a harmonic spike the way a mean would (see docs/qa403-app-stack-spec.md §3.1) — and it is
// what the legend and cursor readout report. The min is drawn by minMaxBandPlugin above rather than
// as a second series, which keeps one legend row per channel and the cursor on the trace people
// actually read values off.
// freqHz comes from the server per-column (never reconstructed from a spacing formula — see
// streaming/protocol.ts for why that was tried and broke at small FFT sizes).
function freqTrace(columns: Float32Array, nPoints: number): Float64Array {
  const out = new Float64Array(nPoints)
  for (let i = 0; i < nPoints; i++) out[i] = columns[i * 3]
  return out
}

function maxTrace(columns: Float32Array, nPoints: number): Float64Array {
  const out = new Float64Array(nPoints)
  for (let i = 0; i < nPoints; i++) out[i] = columns[i * 3 + 2]
  return out
}

// Deliberately not keyed on the frame's point count: nothing in buildOptions depends on it, and
// including it meant a zoom window with fewer bins than columns tore the plot down and rebuilt it
// — losing the very zoom that produced the frame. setData handles a changing length by itself.
function ensurePlot(nyquistHz: number, channelCount: number) {
  const el = containerRef.value
  if (!el) return

  if (plot && plotNyquistHz === nyquistHz && plotChannelCount === channelCount && plotIsDark === darkQuery.matches)
    return

  plot?.destroy()
  const width = el.getBoundingClientRect().width || 800
  plot = new uPlot(buildOptions(width, nyquistHz, channelCount), [[], ...Array(channelCount).fill([])], el)

  // uPlot's own double-click handler calls autoScaleX(), which fits the axis to the *data* extent.
  // While a zoomed frame is on screen that extent is the zoom window, so its reset would be a
  // no-op and there'd be no way back out. Ours runs after it (added later on the same element) and
  // restores the real full span.
  plot.over.addEventListener('dblclick', resetZoom)

  plotNyquistHz = nyquistHz
  plotChannelCount = channelCount
  plotIsDark = darkQuery.matches
}

function resetZoom() {
  if (plot && fullHiFreqHz > 0)
    plot.setScale('x', { min: fullLoFreqHz, max: fullHiFreqHz })
}

function update() {
  const ch0 = props.spectrum[0]
  if (!ch0) return

  // Only an overview frame describes the whole spectrum; a zoomed one describes the zoom window.
  // The axis must always be built from the former, or zooming would redefine "fully zoomed out".
  if ((ch0.header.flags & STREAM_FLAG_ZOOMED) === 0) {
    fullLoFreqHz = ch0.loFreqHz
    fullHiFreqHz = ch0.hiFreqHz
  }
  if (fullHiFreqHz <= 0) return

  // Only build series for channels actually streaming and currently enabled — an idle or disabled
  // channel 1 must not show up as a phantom "R" legend entry (idle: single-channel acquisition;
  // disabled: the R checkbox turned off, but the stream record for it never gets cleared — see
  // useSpectrumStream.ts).
  //
  // This count must depend only on whether R exists at all, never on anything that varies frame to
  // frame: it is an ensurePlot key, so changing it tears the plot down and rebuilds it — which
  // resets the x scale and throws away the user's zoom.
  const ch1 = props.spectrum[1]
  const channelCount = ch1 !== undefined && channelEnabled(1) ? 2 : 1
  const nPoints = ch0.header.nPoints
  ensurePlot(fullHiFreqHz, channelCount)
  if (!plot) return

  const xs = freqTrace(ch0.columns, nPoints)
  const left = maxTrace(ch0.columns, nPoints)

  // Passed to setData as literals rather than being accumulated in a local array: uPlot's
  // AlignedData permits a TypedArray x with y-series that are TypedArray *or* (number|null)[],
  // but only through its tuple form. Annotating an intermediate array collapses that to a plain
  // union-element array, which matches neither branch — and `vue-tsc -b` (what `npm run build`
  // runs) rejects it even though a bare `vue-tsc --noEmit` lets it through.
  //
  // The 1-arg setData is also deliberate: passing resetScales=false looks like the way to stop
  // zoom-reset (see the x scale's `auto:false` above for the actual fix), but in this uPlot build
  // setData()'s commit() — the call that actually repaints the canvas — only runs inside the same
  // `if (_resetScales !== false)` branch as the scale reset. Passing false skips both: the chart
  // would freeze on frame one and never draw again.
  if (channelCount === 1) {
    plot.setData([xs, left])
    return
  }

  // Both series share data[0] (L's frequencies), so R can only be drawn when it was decimated over
  // the same span. For the one frame after a zoom where L has already switched and R has not,
  // blank R for that frame rather than plotting it at the wrong x positions — and, unlike dropping
  // the series, without disturbing the plot (which would reset the zoom).
  const comparable =
    ch1!.header.nPoints === nPoints &&
    (ch1!.header.flags & STREAM_FLAG_ZOOMED) === (ch0.header.flags & STREAM_FLAG_ZOOMED)

  plot.setData([
    xs,
    left,
    comparable ? maxTrace(ch1!.columns, ch1!.header.nPoints) : new Array<number | null>(nPoints).fill(null),
  ])
}

watch(() => props.spectrum, update)

function handleResize() {
  if (plot && containerRef.value)
    plot.setSize({ width: containerRef.value.getBoundingClientRect().width, height: CHART_HEIGHT })
}

// Rebuilds with the new palette on an OS light/dark switch. Rebuilding is only acceptable because
// this fires on a theme change, not per frame — ensurePlot's identity check keeps every other path
// on setData().
function handleThemeChange() {
  if (!plot) return
  const nyquistHz = plotNyquistHz
  const channelCount = plotChannelCount
  plot.destroy()
  plot = null
  plotNyquistHz = -1
  ensurePlot(nyquistHz, channelCount)
  update()
}

onMounted(() => {
  window.addEventListener('resize', handleResize)
  darkQuery.addEventListener('change', handleThemeChange)
})
onBeforeUnmount(() => {
  window.removeEventListener('resize', handleResize)
  darkQuery.removeEventListener('change', handleThemeChange)
  clearTimeout(viewportTimer)
  plot?.destroy()
  plot = null
})
</script>

<template>
  <section class="flex flex-col gap-2 rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <div class="flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>Spectrum</span>
      <span v-if="cursorReadout" class="font-mono">
        {{ cursorReadout.freqHz.toFixed(1) }} Hz · {{ cursorReadout.dbv.toFixed(1) }} dBV
      </span>
      <span v-else-if="!spectrum[0]">Waiting for data…</span>
    </div>
    <div ref="containerRef" class="w-full"></div>
  </section>
</template>

<!--
  Deliberately not `scoped`: the legend is markup uPlot creates itself, outside this component's
  template, so a scoped rule's data attribute never lands on it. The axes are canvas-drawn and
  handled in THEME above — this covers only the DOM parts uPlot.min.css colours near-black.
-->
<style>
.u-legend {
  color: #525252;
}

@media (prefers-color-scheme: dark) {
  .u-legend {
    color: #a3a3a3;
  }
}
</style>
