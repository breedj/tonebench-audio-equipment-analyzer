<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { DeviceStatus } from '../api/client'
import type { ScopeFrame } from '../streaming/protocol'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'

const props = defineProps<{
  scope: Record<number, ScopeFrame>
  status: DeviceStatus | null
}>()

// Same shared config SpectrumChart reads — a channel the Acquisition panel has turned off must
// not keep showing its last waveform forever (the stream only ever adds entries per channel,
// never removes a stale one on disable).
const { config: spectrumConfig } = useSpectrumConfig()
function channelEnabled(ch: number): boolean {
  if (!spectrumConfig.value) return true
  return ch === 0 ? spectrumConfig.value.processLeft : spectrumConfig.value.processRight
}

const CHANNEL_LABELS = ['L', 'R']
const CHANNEL_COLORS = ['#3b82f6', '#f97316']
const CHART_HEIGHT = 350

// Same canvas-colour reasoning as SpectrumChart.vue: uPlot draws its axes onto the canvas itself,
// so Tailwind's `dark:` classes can't reach them.
const THEME = { dark: { axis: '#a3a3a3', grid: '#404040' }, light: { axis: '#525252', grid: '#e5e5e5' } } as const
const darkQuery = window.matchMedia('(prefers-color-scheme: dark)')
function currentTheme() {
  return darkQuery.matches ? THEME.dark : THEME.light
}

const containerRef = ref<HTMLDivElement>()
let plot: uPlot | null = null
let plotIsDark = darkQuery.matches

function buildOptions(width: number): uPlot.Options {
  const theme = currentTheme()
  return {
    width,
    height: CHART_HEIGHT,
    scales: {
      // Auto-ranging on both axes is exactly right here, unlike SpectrumChart's fixed/reported
      // scales: every incoming frame is a fresh, self-contained snapshot (see ScopeFrame's own
      // docs), not a continuous signal a zoom window carves a slice out of — there is nothing to
      // preserve across frames, so just fit to whatever this tick's samples are.
      x: { time: false },
      y: {},
    },
    axes: [
      {
        space: 60,
        values: (_u, vals) => vals.map((v) => (v == null ? null : `${v.toFixed(1)}`)),
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
      {
        values: (_u, vals) => vals.map((v) => (v == null ? null : `${v}`)),
        size: 50,
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
    ],
    series: [
      {},
      { label: CHANNEL_LABELS[0], stroke: CHANNEL_COLORS[0], width: 1.5, points: { show: false } },
      { label: CHANNEL_LABELS[1], stroke: CHANNEL_COLORS[1], width: 1.5, points: { show: false } },
    ],
    cursor: { points: { show: false } },
    legend: { show: false },
  }
}

function ensurePlot() {
  const el = containerRef.value
  if (!el) return
  if (plot && plotIsDark === darkQuery.matches) return

  plot?.destroy()
  const width = el.getBoundingClientRect().width || 800
  plot = new uPlot(buildOptions(width), [[], [], []], el)
  plotIsDark = darkQuery.matches
}

// The generator's own configured tone frequency — not something re-derived from the captured
// signal (e.g. via FFT or crossing spacing), since the generator already knows it exactly and a
// re-derivation would just be a noisier copy of the same number. Left and right always share one
// stimulus (see GeneratorPanel.vue), so either channel's config is the same answer; which one is
// enabled doesn't matter for the frequency itself, only for which trace gets drawn.
function generatorFreqHz(status: DeviceStatus | null): number | null {
  const gen = status?.generatorLeft ?? status?.generatorRight
  if (!gen || gen.kind !== 'Tones' || gen.tones.length === 0) return null
  return gen.tones[0].freqHz > 0 ? gen.tones[0].freqHz : null
}

/** Most periods to show at once — see findTriggerWindow's fallback ladder below. */
const MAX_TRIGGER_PERIODS = 4

/**
 * Finds a steady trigger window: the first rising (negative-to-positive) zero-crossing, then as
 * many whole periods of the fundamental as fit in what's left of the buffer from there — a
 * free-running buffer redraws a *different* slice of the waveform each tick, which for a periodic
 * signal looks like it's crawling sideways; starting every frame at the same phase is what makes
 * it look like a real scope.
 *
 * Prefers MAX_TRIGGER_PERIODS whole periods, stepping down to 3, 2, then 1 if the buffer (2048
 * samples minus however much the crossing search itself consumed) doesn't hold that many —
 * cutting a period off mid-cycle would show a real discontinuity at the trace's own edge, so
 * showing fewer *whole* periods reads better than a partial one. Below one whole period there's
 * nothing left to step down to, so it just shows whatever remains.
 */
function findTriggerWindow(samples: Float32Array, frequencyHz: number, samplePeriodS: number): { startStep: number; count: number } {
  let startStep = 0
  for (let i = 1; i < samples.length; i++) {
    if (samples[i - 1] < 0 && samples[i] >= 0) {
      startStep = i
      break
    }
  }

  const available = samples.length - startStep
  const periodSamples = 1 / frequencyHz / samplePeriodS

  let periods = MAX_TRIGGER_PERIODS
  while (periods > 1 && periodSamples * periods > available) periods--

  const count = Math.max(1, Math.min(Math.round(periodSamples * periods), available))
  return { startStep, count }
}

/** Reads `count` samples starting at `start`; null past this particular channel's own array bounds (see update()'s remarks on mismatched lengths). */
function sliceSamples(samples: Float32Array, start: number, count: number): (number | null)[] {
  const out: (number | null)[] = new Array(count)
  for (let i = 0; i < count; i++) {
    const idx = start + i
    out[i] = idx < samples.length ? samples[idx] : null
  }
  return out
}

function update() {
  const ch0 = props.scope[0]
  const ch1 = props.scope[1]
  if (!ch0 && !ch1) return

  ensurePlot()
  if (!plot) return

  // Triggers off whichever enabled channel is available, preferring L — both channels then draw
  // from that same window, so their relative phase stays meaningful. Falls back to the raw start
  // of the buffer (no trigger) when there's no known generator tone frequency to trigger on, e.g.
  // white noise/multitone/chirp, or nothing enabled at all.
  const triggerSource = channelEnabled(0) && ch0 ? ch0 : channelEnabled(1) && ch1 ? ch1 : null
  const freqHz = generatorFreqHz(props.status)

  let startStep = 0
  let count = (ch0 ?? ch1!).samples.length
  if (freqHz && triggerSource) {
    const window = findTriggerWindow(triggerSource.samples, freqHz, triggerSource.samplePeriodS)
    startStep = window.startStep
    count = window.count
  }

  // Both channels' frames arrive independently, so their sample counts can momentarily differ —
  // that's why sliceSamples clamps to each channel's own length rather than assuming both match
  // the trigger source's.
  const source = ch0 ?? ch1!
  const stepMs = source.samplePeriodS * 1000
  const xs = new Float64Array(count)
  for (let i = 0; i < count; i++) xs[i] = i * stepMs

  const left = ch0 && channelEnabled(0) ? sliceSamples(ch0.samples, startStep, count) : new Array(count).fill(null)
  const right = ch1 && channelEnabled(1) ? sliceSamples(ch1.samples, startStep, count) : new Array(count).fill(null)

  plot.setData([xs, left, right])
}

watch(() => props.scope, update)

function handleResize() {
  if (plot && containerRef.value)
    plot.setSize({ width: containerRef.value.getBoundingClientRect().width, height: CHART_HEIGHT })
}

function handleThemeChange() {
  if (!plot) return
  plot.destroy()
  plot = null
  ensurePlot()
  update()
}

onMounted(() => {
  window.addEventListener('resize', handleResize)
  darkQuery.addEventListener('change', handleThemeChange)
})
onBeforeUnmount(() => {
  window.removeEventListener('resize', handleResize)
  darkQuery.removeEventListener('change', handleThemeChange)
  plot?.destroy()
  plot = null
})
</script>

<template>
  <section class="flex flex-col gap-2 rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <div class="flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>Oscilloscope</span>
      <span v-if="!scope[0] && !scope[1]">Waiting for data…</span>
    </div>
    <div ref="containerRef" class="w-full"></div>
  </section>
</template>
