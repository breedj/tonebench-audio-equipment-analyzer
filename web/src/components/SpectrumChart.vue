<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import { type MetersFrame, type SpectrumFrame } from '../streaming/protocol'

const props = defineProps<{
  spectrum: Record<number, SpectrumFrame>
  meters: Record<number, MetersFrame>
}>()

const CHANNEL_LABELS = ['L', 'R']
const CHANNEL_COLORS = ['#3b82f6', '#f97316']
const CHART_HEIGHT = 360

const containerRef = ref<HTMLDivElement>()
const cursorReadout = ref<{ freqHz: number; dbv: number } | null>(null)

// uPlot instance is created exactly once and reused via setData()/setSize() for the component's
// whole lifetime — re-instantiating per frame is the single biggest perf mistake for a
// streaming chart like this.
let plot: uPlot | null = null
let plotNPoints = -1
let plotNyquistHz = -1
let plotChannelCount = -1

function harmonicMarkersPlugin(): uPlot.Plugin {
  return {
    hooks: {
      draw: [
        (u) => {
          const ch0 = props.meters[0]
          if (!ch0 || ch0.harmonics.length === 0) return

          const ctx = u.ctx
          ctx.save()
          ctx.strokeStyle = 'rgba(234, 179, 8, 0.5)'
          ctx.fillStyle = 'rgba(161, 98, 7, 0.95)'
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
  const series: uPlot.Series[] = [{}]
  for (let ch = 0; ch < channelCount; ch++)
    series.push({ label: CHANNEL_LABELS[ch], stroke: CHANNEL_COLORS[ch], width: 1.5, points: { show: false } })

  return {
    width,
    height: CHART_HEIGHT,
    scales: {
      x: { time: false, distr: 3, log: 10, min: 10, max: nyquistHz },
      y: { range: [-180, 20] },
    },
    axes: [
      { space: 60, values: (_u, vals) => vals.map(formatFreqTick) },
      { values: (_u, vals) => vals.map(formatDbTick), size: 50 },
    ],
    series,
    cursor: { points: { show: false } },
    legend: { show: true },
    plugins: [harmonicMarkersPlugin()],
  }
}

// The wire payload interleaves (freqHz,min,max) per column; the trace shown is max-per-column —
// it never misses a harmonic spike the way a mean would (see docs/qa403-app-stack-spec.md §3.1).
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

function ensurePlot(nPoints: number, nyquistHz: number, channelCount: number) {
  const el = containerRef.value
  if (!el) return

  if (plot && plotNPoints === nPoints && plotNyquistHz === nyquistHz && plotChannelCount === channelCount) return

  plot?.destroy()
  const width = el.getBoundingClientRect().width || 800
  plot = new uPlot(buildOptions(width, nyquistHz, channelCount), [[], ...Array(channelCount).fill([])], el)
  plotNPoints = nPoints
  plotNyquistHz = nyquistHz
  plotChannelCount = channelCount
}

function update() {
  const ch0 = props.spectrum[0]
  if (!ch0) return

  // Only build series for channels actually streaming — an idle channel 1 must not show up as
  // a phantom "R" legend entry when only channel 0 (single-channel acquisition) is active.
  const channelCount = props.spectrum[1] ? 2 : 1
  const nPoints = ch0.header.nPoints
  const nyquistHz = ch0.hiFreqHz
  ensurePlot(nPoints, nyquistHz, channelCount)
  if (!plot) return

  const xs = freqTrace(ch0.columns, nPoints)
  const data = [xs, maxTrace(ch0.columns, nPoints)]
  if (channelCount === 2)
    data.push(maxTrace(props.spectrum[1]!.columns, props.spectrum[1]!.header.nPoints))

  plot.setData(data)
}

watch(() => props.spectrum, update)

function handleResize() {
  if (plot && containerRef.value)
    plot.setSize({ width: containerRef.value.getBoundingClientRect().width, height: CHART_HEIGHT })
}

onMounted(() => window.addEventListener('resize', handleResize))
onBeforeUnmount(() => {
  window.removeEventListener('resize', handleResize)
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
