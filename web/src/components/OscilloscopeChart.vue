<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { DeviceStatus } from '../api/client'
import type { ScopeFrame } from '../streaming/protocol'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { MAX_SCOPE_PERIODS, useScopeDisplay } from '../composables/useScopeDisplay'
import { useGeneratorStimulus } from '../composables/useGeneratorStimulus'
import { useStagedSampleRate } from '../composables/useStagedSampleRate'
import { isWaveKind, waveEffectiveFreqHz } from '../periodicWave'
import { exportGridTheme } from '../charts/palette'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import ExpandToggle from './ExpandToggle.vue'
import {
  CHART_EXPORT_HEADER_PX,
  composeChartPng,
  exportAxisLabel,
  nextPaint,
  type ChartExportOptions,
  type ExportLegendEntry,
} from '../charts/exportChart'

const props = defineProps<{
  scope: Record<number, ScopeFrame>
  /**
   * The same tick's frames with the fundamental fitted and subtracted (the THD residual), present
   * only while the pipeline's ScopeResidual option is on and a fundamental was found. Drawn instead
   * of `scope` in residual mode, but always at the window found on the raw left frame: a residual
   * has no fundamental crossings to trigger on.
   */
  residual?: Record<number, ScopeFrame>
  status: DeviceStatus | null
}>()

// Same shared config SpectrumChart reads — a channel the Acquisition panel has turned off must
// not keep showing its last waveform forever (the stream only ever adds entries per channel,
// never removes a stale one on disable).
const { config: spectrumConfig, apply: applyConfig } = useSpectrumConfig()
function channelEnabled(ch: number): boolean {
  if (!spectrumConfig.value) return true
  return ch === 0 ? spectrumConfig.value.processLeft : spectrumConfig.value.processRight
}

// Residual mode is live pipeline state (SpectrumPipelineOptions.ScopeResidual), like the FFT size:
// the server has to fit and subtract, so the checkbox writes the option and the chart reads it back
// from the shared config -- which is also what a loaded measurement type restores it through.
const residualMode = computed(() => spectrumConfig.value?.scopeResidual ?? false)
function onResidualChange(event: Event) {
  void applyConfig({ scopeResidual: (event.target as HTMLInputElement).checked })
}

const CHANNEL_LABELS = ['L', 'R']
const CHANNEL_COLORS = ['#3b82f6', '#f97316']
const CHART_HEIGHT = 350

// Same canvas-colour reasoning as SpectrumChart.vue: uPlot draws its axes onto the canvas itself,
// so Tailwind's `dark:` classes can't reach them.
const THEME = { dark: { axis: '#a3a3a3', grid: '#404040', gridWidth: 1 }, light: { axis: '#525252', grid: '#e5e5e5', gridWidth: 1 } } as const
const darkQuery = window.matchMedia('(prefers-color-scheme: dark)')
function currentTheme() {
  return darkQuery.matches ? THEME.dark : THEME.light
}

const rootRef = ref<HTMLElement | null>(null)
const containerRef = ref<HTMLDivElement>()
// Fitted only when expanded -- see charts/useResultChartHeight.ts's pageHeightPx. Under the
// spectrum chart this keeps the height it has always had, since the two share the view; expanded,
// the root is pinned over the viewport and the plot takes the window.
const chartHeight = useResultChartHeight(rootRef, containerRef, { pageHeightPx: CHART_HEIGHT })
// How many whole periods the last draw actually managed — findTriggerWindow steps the request
// down when the 2048-sample frame can't hold it, and silently showing 2 when the field says 20
// reads as the field being broken. Null while untriggered (no tone to count periods of).
const shownPeriods = ref<number | null>(null)
let plot: uPlot | null = null
let plotIsDark = darkQuery.matches

function buildOptions(width: number, height: number, theme: (typeof THEME)[keyof typeof THEME], interactive: boolean): uPlot.Options {
  return {
    width,
    height,
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
        grid: { stroke: theme.grid, width: theme.gridWidth },
        ticks: { stroke: theme.grid, width: theme.gridWidth },
        ...exportAxisLabel(interactive, 'Time (ms)'),
      },
      {
        values: (_u, vals) => vals.map((v) => (v == null ? null : `${v}`)),
        size: 50,
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: theme.gridWidth },
        ticks: { stroke: theme.grid, width: theme.gridWidth },
        ...exportAxisLabel(interactive, residualMode.value ? 'Residual (V)' : 'Volts'),
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

// The axis label is baked into the plot's options, so a mode change rebuilds it.
let plotIsResidual = false
function ensurePlot() {
  const el = containerRef.value
  if (!el) return
  if (plot && plotIsDark === darkQuery.matches && plotIsResidual === residualMode.value) return

  plot?.destroy()
  const width = el.getBoundingClientRect().width || 800
  plot = new uPlot(buildOptions(width, chartHeight.value, currentTheme(), true), [[], [], []], el)
  plotIsDark = darkQuery.matches
  plotIsResidual = residualMode.value
}

// The staged stimulus, for the fallback in generatorFreqHz below.
const { kind: stagedKind, tones: stagedTones, wave: stagedWave } = useGeneratorStimulus()

// A square or triangle's period is quantised to whole even samples, so the trigger has to use the
// *played* frequency and not the typed one: 10 kHz asked for at 192 kHz plays at 9.6 kHz, and a
// trigger 4 % out walks the trace sideways a little further on every frame. Re-derived here rather
// than read from status for the reason generatorFreqHz falls back at all -- Stop silences status,
// and a stopped SINGLE is exactly when the scope matters most.
const { stagedSampleRateHz } = useStagedSampleRate()

// The generator's own configured tone frequency — not something re-derived from the captured
// signal (e.g. via FFT or crossing spacing), since the generator already knows it exactly and a
// re-derivation would just be a noisier copy of the same number. Left and right always share one
// stimulus (see GeneratorPanel.vue), so either channel's config is the same answer; which one is
// enabled doesn't matter for the frequency itself, only for which trace gets drawn.
//
// Falls back to the *staged* stimulus when the live status has no tone, which is the state every
// stopped capture ends in: Stop silences the generator (deviceControl.ts's stopRun), so the moment a
// SINGLE auto-stopped, the trigger frequency went null and the trace re-drew as the whole
// 2048-sample buffer — 42 ms of 1 kHz rendered as a solid band, instead of the four periods the
// Periods field asks for. Same source and same reason as the Gen tile and the noise figures, which
// both read the staged form for exactly this: it survives Stop.
function generatorFreqHz(status: DeviceStatus | null): number | null {
  const gen = status?.generatorLeft ?? status?.generatorRight
  // Live status first, for every kind whose effective tone list is its fundamental: that is the
  // server's own answer, already quantised (square, triangle) or snapped (tones).
  if (gen && (gen.kind === 'Tones' || isWaveKind(gen.kind)) && gen.tones.length > 0 && gen.tones[0].freqHz > 0)
    return gen.tones[0].freqHz
  if (isWaveKind(stagedKind.value)) {
    const freqHz = waveEffectiveFreqHz(stagedWave.freqHz, stagedSampleRateHz.value)
    return freqHz > 0 ? freqHz : null
  }
  if (stagedKind.value !== 'Tones' || stagedTones.length === 0) return null
  return stagedTones[0].freqHz > 0 ? stagedTones[0].freqHz : null
}

// How many whole periods the Periods field asks for — see findTriggerWindow's fallback ladder
// below for what happens when the 2048-sample frame can't hold that many. Saved with the
// measurement type (see useScopeDisplay.ts).
const { scopePeriods, setScopePeriods } = useScopeDisplay()

/**
 * The field's own text, mirrored on every keystroke — deliberately *not* `:value="scopePeriods"`.
 * Vue re-patches an input's `value` prop on every re-render even when the bound value hasn't
 * changed (`patchProps` special-cases `value` so the DOM can never drift from the binding), and
 * this component re-renders on every scope frame, because the `scope` prop object is replaced
 * ~20 times a second while acquisition runs. Binding the *committed* number therefore stamped the
 * old value back into the field between keystrokes, so a typed edit was usually gone before Enter
 * or a blur could commit it — the field looked like it ignored what you typed. Mirroring the text
 * means each of those re-patches writes back exactly what is already there, which
 * `patchDOMProp`'s own `el.value !== newValue` check then skips.
 */
const periodsField = ref(String(scopePeriods.value))
watch(scopePeriods, (periods) => {
  periodsField.value = String(periods)
})

/**
 * Applies as you type, rather than only when the field is committed. `change` alone was not enough:
 * it fires on blur, and on Enter only in browsers that choose to raise it for a number input — so
 * a typed edit could sit there looking accepted while the chart kept drawing the previous count,
 * which reads as the field having been ignored.
 *
 * Debounced, so typing "12" doesn't briefly draw 1 period on the way through, and gated to in-range
 * integers so a half-typed or out-of-range entry is left alone under the cursor and waits for
 * onPeriodsChange to clamp it.
 */
const LIVE_COMMIT_DELAY_MS = 250
let liveCommitTimer: ReturnType<typeof setTimeout> | undefined

function onPeriodsInput(event: Event) {
  periodsField.value = (event.target as HTMLInputElement).value
  clearTimeout(liveCommitTimer)

  const typed = Number(periodsField.value)
  if (!Number.isInteger(typed) || typed < 1 || typed > MAX_SCOPE_PERIODS) return
  liveCommitTimer = setTimeout(() => setScopePeriods(typed), LIVE_COMMIT_DELAY_MS)
}

// Commit on change (blur/Enter/spinner), then re-read what was accepted: setScopePeriods clamps to
// 1..MAX_SCOPE_PERIODS, and a clamped-away edit (typing 999) leaves scopePeriods unchanged, so the
// watcher above wouldn't fire and the field would keep showing the rejected number.
function onPeriodsChange() {
  clearTimeout(liveCommitTimer)
  setScopePeriods(Number(periodsField.value))
  periodsField.value = String(scopePeriods.value)
}

/**
 * Finds a steady trigger window: the first rising (negative-to-positive) zero-crossing, then as
 * many whole periods of the fundamental as fit in what's left of the buffer from there — a
 * free-running buffer redraws a *different* slice of the waveform each tick, which for a periodic
 * signal looks like it's crawling sideways; starting every frame at the same phase is what makes
 * it look like a real scope.
 *
 * Prefers `desiredPeriods` whole periods (the Periods field), stepping down one at a time to 1 if
 * the 2048-sample frame can't hold that many — cutting a period off mid-cycle would show a real
 * discontinuity at the trace's own edge, so showing fewer *whole* periods reads better than a
 * partial one. Below one whole period there's nothing left to step down to, so it just shows
 * whatever remains. This is why the field is a request rather than a guarantee: 20 periods of
 * 1 kHz needs 3840 samples at 192 kHz and the frame only ever carries 2048.
 */
function findTriggerWindow(
  samples: Float32Array,
  frequencyHz: number,
  samplePeriodS: number,
  desiredPeriods: number,
): { startStep: number; count: number; periods: number } {
  const periodSamples = 1 / frequencyHz / samplePeriodS

  // The crossing search is bounded to a single period. One period is all a *periodic* signal can
  // need, but the captured waveform is the tone plus everything else: on a tone riding a large
  // low-frequency component the sum simply doesn't go negative for a while, and an unbounded search
  // ran 800+ samples — four periods — into a 2048-sample frame before it found a crossing. That
  // eats the very buffer the window is then measured against, which is what made the trace's length
  // (and with it the time axis) change on every frame. Failing to find a crossing inside one period
  // means there is no clean one to find, and starting at the raw beginning is no worse.
  const searchLimit = Math.min(samples.length, Math.ceil(periodSamples) + 1)
  let startStep = 0
  for (let i = 1; i < searchLimit; i++) {
    if (samples[i - 1] < 0 && samples[i] >= 0) {
      startStep = i
      break
    }
  }

  // Budgeted against that worst case rather than against this frame's actual `startStep`: the
  // crossing lands somewhere different every frame, so a ladder that stepped down on the real
  // offset chose a different period count from one frame to the next whenever the request sat near
  // the buffer's capacity — the "showing N" readout flickered along with the trace. Budgeting the
  // capped worst case makes the choice depend only on the request, the tone and the sample rate.
  const budget = samples.length - Math.ceil(periodSamples)

  let periods = desiredPeriods
  while (periods > 1 && periodSamples * periods > budget) periods--

  // `available` can only exceed `budget` now that the search is bounded, so this clamp is a
  // backstop (a sub-one-period buffer, say) rather than something the steady state relies on.
  const available = samples.length - startStep
  const count = Math.max(1, Math.min(Math.round(periodSamples * periods), available))
  return { startStep, count, periods }
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

/**
 * What one tick draws — the triggered window of both channels on a millisecond axis — and how many
 * periods it managed. Null when there is no frame at all. Shared by the visible plot and the export,
 * so the picture in a report is the trace on screen.
 */
function computeData(): { data: uPlot.AlignedData; periods: number | null } | null {
  const ch0 = props.scope[0]
  const ch1 = props.scope[1]
  if (!ch0 && !ch1) return null

  // In residual mode the window is still found on the raw frames below; only what is sliced at
  // that window changes. A channel with no residual frame this tick draws nothing.
  const drawn = residualMode.value ? (props.residual ?? {}) : props.scope

  // Triggers off whichever enabled channel is available, preferring L — both channels then draw
  // from that same window, so their relative phase stays meaningful. Falls back to the raw start
  // of the buffer (no trigger) when there's no known generator tone frequency to trigger on, e.g.
  // white noise/multitone/chirp, or nothing enabled at all.
  const triggerSource = channelEnabled(0) && ch0 ? ch0 : channelEnabled(1) && ch1 ? ch1 : null
  const freqHz = generatorFreqHz(props.status)

  let startStep = 0
  let count = (ch0 ?? ch1!).samples.length
  let periods: number | null = null
  if (freqHz && triggerSource) {
    const window = findTriggerWindow(triggerSource.samples, freqHz, triggerSource.samplePeriodS, scopePeriods.value)
    startStep = window.startStep
    count = window.count
    periods = window.periods
  }

  // Both channels' frames arrive independently, so their sample counts can momentarily differ —
  // that's why sliceSamples clamps to each channel's own length rather than assuming both match
  // the trigger source's.
  const source = ch0 ?? ch1!
  const stepMs = source.samplePeriodS * 1000
  const xs = new Float64Array(count)
  for (let i = 0; i < count; i++) xs[i] = i * stepMs

  const left = drawn[0] && channelEnabled(0) ? sliceSamples(drawn[0].samples, startStep, count) : new Array(count).fill(null)
  const right = drawn[1] && channelEnabled(1) ? sliceSamples(drawn[1].samples, startStep, count) : new Array(count).fill(null)

  return { data: [xs, left, right] as unknown as uPlot.AlignedData, periods }
}

function update() {
  const computed = computeData()
  // Destroying rather than merely returning matters here: a bare return leaves whatever the last
  // setData() call painted sitting on the canvas, so the chart would keep showing a stale waveform
  // under the "Waiting for data…" label in the header (see the Clear button in App.vue).
  if (!computed) {
    plot?.destroy()
    plot = null
    return
  }

  ensurePlot()
  if (!plot) return

  // Untriggered (no known tone frequency): the whole buffer is drawn, so the Periods field has
  // nothing to act on and its readout says so rather than claiming a count it didn't honour.
  shownPeriods.value = computed.periods
  plot.setData(computed.data)
}

/**
 * The trace as a PNG of exactly the requested size, drawn with the light theme whatever the screen's
 * — the same arrangement as SpectrumChart.exportPng, for the FFT report's Oscilloscope element. A
 * second uPlot is built offscreen from the same data as the visible one, so the export is this
 * chart and not an imitation of it; the axes carry their units, which on screen the header implies.
 */
async function exportPng({ width, height }: ChartExportOptions): Promise<Blob> {
  const computed = computeData()
  if (!computed) throw new Error('There is no waveform to export.')

  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header

  const host = document.createElement('div')
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(host)

  let u: uPlot | null = null
  try {
    u = new uPlot(buildOptions(width, plotHeight, exportGridTheme(THEME.light), false), computed.data, host)
    await nextPaint()

    const legend: ExportLegendEntry[] = []
    if (props.scope[0] && channelEnabled(0)) legend.push({ label: CHANNEL_LABELS[0], color: CHANNEL_COLORS[0] })
    if (props.scope[1] && channelEnabled(1)) legend.push({ label: CHANNEL_LABELS[1], color: CHANNEL_COLORS[1] })

    const what = residualMode.value ? 'Oscilloscope — residual' : 'Oscilloscope'
    const title = computed.periods === null ? what : `${what} — ${computed.periods} period${computed.periods === 1 ? '' : 's'}`
    return await composeChartPng({ width, height, plotCanvas: u.ctx.canvas, headerHeight: header, title, legend })
  } finally {
    u?.destroy()
    host.remove()
  }
}

defineExpose({ exportPng })

watch(() => props.scope, update)
// A Periods change has to redraw off the *last* frame rather than waiting for the next one —
// otherwise the field looks dead while acquisition is stopped. The residual box likewise: while
// stopped the raw frame is still there, and toggling back to it must not wait for a tick.
watch(scopePeriods, update)
watch(residualMode, update)

/** Residual mode with a raw frame but no residual one: the tick found no fundamental to remove. */
const residualMissing = computed(
  () => residualMode.value && (!!props.scope[0] || !!props.scope[1]) && !props.residual?.[0] && !props.residual?.[1],
)

// Observes the container rather than the window: the column this chart sits in also changes width
// when the measurement sidebar is pinned or unpinned, which is not a window resize — measured, the
// canvas stayed at 432 px inside a column that had grown to 661 px. Same mechanism as the
// ResizeObserver in FrequencyResponseChart.
let resizeObserver: ResizeObserver | null = null
function handleResize() {
  if (!plot || !containerRef.value) return
  // Zero while the panel is hidden (App.vue's v-show) -- see WaterfallChart's handleResize.
  const width = containerRef.value.getBoundingClientRect().width
  if (width > 0) plot.setSize({ width, height: chartHeight.value })
}
watch(chartHeight, handleResize)

function handleThemeChange() {
  if (!plot) return
  plot.destroy()
  plot = null
  ensurePlot()
  update()
}

onMounted(() => {
  resizeObserver = new ResizeObserver(handleResize)
  if (containerRef.value) resizeObserver.observe(containerRef.value)
  darkQuery.addEventListener('change', handleThemeChange)
  // Draw what the store already holds. Every update is driven by a watcher on changing data, so a
  // remount with *unchanged* data drew nothing at all: leaving one measurement and coming back
  // showed an empty chart until the next frame arrived, though the capture was still in the store.
  update()
})
onBeforeUnmount(() => {
  clearTimeout(liveCommitTimer)
  resizeObserver?.disconnect()
  darkQuery.removeEventListener('change', handleThemeChange)
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
    <!-- pr-9 for the button, which is positioned absolutely and so makes no room for itself. The
         row wraps, because its controls already crowd a narrow rail. -->
    <div class="flex flex-wrap items-center gap-3 pr-9 text-xs text-neutral-500 dark:text-neutral-400">
      <span>Oscilloscope</span>
      <label class="flex items-center gap-1.5" title="How many whole periods of the generator tone to show. Stepped down automatically when the captured frame is too short to hold that many.">
        Periods
        <input
          :value="periodsField"
          type="number"
          min="1"
          :max="MAX_SCOPE_PERIODS"
          step="1"
          class="w-16 rounded border border-neutral-300 px-1.5 py-0.5 text-xs dark:border-neutral-700"
          @input="onPeriodsInput"
          @change="onPeriodsChange"
        />
      </label>
      <span v-if="shownPeriods !== null && shownPeriods !== scopePeriods" class="text-amber-600 dark:text-amber-500">
        showing {{ shownPeriods }} — the captured frame is too short for {{ scopePeriods }}
      </span>
      <label
        class="flex items-center gap-1.5"
        title="Shows the captured waveform with its fundamental fitted and subtracted — the THD residual — so crossover spikes and harmonic residue are visible in the time domain. Still triggered on the raw left channel. Needs a fundamental; a multitone or noise stimulus has none to remove."
      >
        <input type="checkbox" :checked="residualMode" @change="onResidualChange" />
        Residual
      </label>
      <span v-if="residualMissing" class="text-amber-600 dark:text-amber-500">no fundamental to remove</span>
      <span class="flex-1"></span>
      <span v-if="!scope[0] && !scope[1]">Waiting for data…</span>
    </div>
    <div ref="containerRef" class="w-full"></div>
  </section>
</template>
