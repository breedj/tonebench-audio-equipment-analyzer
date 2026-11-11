<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, shallowRef, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import { useAppTheme } from '../composables/useAppTheme'
import { useClockDrift } from '../composables/useClockDrift'
import { useSoundCard } from '../composables/useSoundCard'
import { useManual } from '../manual/useManual'
import HeaderIcon from './HeaderIcon.vue'
import { clampNumberField } from '../numberField'
import CollapsibleSection from './CollapsibleSection.vue'
import Modal from './Modal.vue'

/**
 * Measures how far two cards' clocks run apart, and says what that costs.
 *
 * <p>Opened from the two-clocks warning in the sound card dialog, which states the problem without
 * a number — this is the number. It measures through whatever is connected, a DUT included, since a
 * DUT changes a tone's amplitude and phase and not its frequency.</p>
 *
 * <p>The verdict is the server's (<c>ClockDriftReading</c>), not this component's: the thresholds
 * are arithmetic about crystal tolerances rather than presentation, and a second copy here would be
 * a second answer to one question.</p>
 */
const props = defineProps<{ open: boolean; pairingNote?: string | null }>()
const emit = defineEmits<{ (e: 'close'): void }>()

const { status, error, busy, running, points, refresh, start, cancel, clear } = useClockDrift()
const soundCard = useSoundCard()
const { openManual } = useManual()
const { isDark } = useAppTheme()

const form = reactive({ toneHz: 1000, levelDbv: -20, durationSeconds: 60 })

const host = ref<HTMLDivElement | null>(null)
const plot = shallowRef<uPlot | null>(null)

/**
 * What the Level box is in. dBV asserts volts, and an uncalibrated card has no volts scale -- it
 * assumes full scale is root-2 V peak so that dBV and dBFS coincide numerically, which makes the
 * figure right and the label a claim the app cannot back. Reported from 2026-11-11: "shouldn't we
 * change that dBV to dBFS instead, that is why I was confused".
 *
 * <p>The OUTPUT full scale decides it, not the pair: this box drives the generator. It follows the
 * convention the status bar already states -- "not calibrated, so levels are dBFS rather than
 * volts" -- rather than inventing one.</p>
 */
const levelUnit = computed(() => (soundCard.config.value?.outputFullScalePeakMv != null ? 'dBV' : 'dBFS'))

const verdict = computed(() => status.value?.verdict ?? null)

const verdictTone = computed(() => {
  switch (verdict.value?.quality) {
    case 'Good':
      return 'border-emerald-300 text-emerald-900 dark:border-emerald-700/60 dark:text-emerald-200'
    case 'Fair':
      return 'border-amber-300 text-amber-900 dark:border-amber-700/60 dark:text-amber-200'
    case 'Poor':
      return 'border-red-300 text-red-800 dark:border-red-700/60 dark:text-red-300'
    default:
      return 'border-neutral-300 text-neutral-700 dark:border-neutral-700 dark:text-neutral-300'
  }
})

/**
 * The reading is only as good as the tone it was taken from, and a buried tone is the one way this
 * measurement goes quietly wrong — so the weakest point's margin is shown rather than the best.
 */
const weakestDominanceDb = computed(() =>
  points.value.length ? Math.min(...points.value.map((p) => p.dominanceDb)) : null,
)

const progressPercent = computed(() => {
  const s = status.value
  if (!s || s.durationSeconds <= 0) return null
  return Math.min(100, (s.elapsedSeconds / s.durationSeconds) * 100)
})

function fmt(value: number | null | undefined, digits = 2, unit = '') {
  return value == null || !Number.isFinite(value) ? '—' : `${value.toFixed(digits)}${unit}`
}

async function onStart() {
  await start({ ...form })
}

function buildPlot() {
  const element = host.value
  if (!element) return
  plot.value?.destroy()

  const dark = isDark.value
  const ink = dark ? '#d4d4d4' : '#404040'
  const grid = dark ? 'rgba(212,212,212,0.14)' : 'rgba(64,64,64,0.14)'

  plot.value = new uPlot(
    {
      width: element.clientWidth || 640,
      height: 240,
      // No legend: there is one series, and the readings are tabulated under the chart.
      legend: { show: false },
      cursor: { drag: { x: false, y: false } },
      scales: { x: { time: false } },
      axes: [
        { stroke: ink, grid: { stroke: grid }, label: 'Time (s)', labelSize: 28 },
        { stroke: ink, grid: { stroke: grid }, label: 'Drift (ppm)', labelSize: 44 },
      ],
      series: [
        {},
        {
          stroke: dark ? '#60a5fa' : '#2563eb',
          width: 2,
          points: { show: true, size: 5 },
          label: 'Drift',
        },
      ],
    },
    [[], []],
    element,
  )

  draw()
}

function draw() {
  const p = plot.value
  if (!p) return
  const xs = points.value.map((q) => q.atSeconds)
  const ys = points.value.map((q) => q.ppm)
  p.setData([xs, ys])
}

watch(points, draw, { deep: true })
watch(isDark, () => void nextTick(buildPlot))

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    await refresh()
    await nextTick()
    buildPlot()
  },
  { immediate: true },
)

onBeforeUnmount(() => plot.value?.destroy())

// The tone must never outlive the dialog, as the sound card dialog's own close does: this one is
// the generator too, and a user who walks away should not leave it playing into their DUT.
async function close() {
  if (running.value) await cancel()
  emit('close')
}
</script>

<template>
  <Modal
    :open="props.open"
    flush
    ground
    icon="clock"
    tone="violet"
    title="Clock drift"
    size="lg"
    @close="close"
  >

    <div class="flex flex-col gap-3 p-4">
      <!-- Two things a reader needs BEFORE pressing anything, so they go above the explanation
           and in amber rather than inside it.

           The loopback is advice rather than a requirement -- the measurement really does work
           through a DUT, since a DUT changes a tone's level and phase and not its frequency -- but
           it is the right advice: a loopback gives the strongest, cleanest tone, which is the one
           thing this measurement needs and the one way it goes quietly wrong. The cable is also
           already there, since both calibration steps use it.

           The tone warning was found the hard way on 2026-11-11: a test run during development
           played 1 kHz out of the bench's output, which was patched to a pair of headphones
           somebody was wearing. The measurement cannot avoid playing a tone -- that is what it
           measures -- so the honest answer is to say so first. -->
      <p
        class="shrink-0 rounded-xl border border-amber-300 px-3 py-2 text-[12px] leading-relaxed text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
      >
        <strong>Connect the output back to the input</strong> — the same loopback the calibration
        steps use. It will measure through a DUT as well, but a loopback gives the cleanest tone and
        so the best reading. And <strong>it plays that tone for as long as the test runs</strong>, at
        the level below: take headphones off, and turn a monitoring amplifier down, before you press
        Measure.
      </p>

      <!-- Folded away, and shut by default: neither paragraph is needed to run the test, and the
           dialog is read top to bottom by someone who wants to press Measure. The second is there
           because it was asked within a minute of the dialog first being seen -- a reader who has
           just been told to calibrate a card before trusting its levels will reasonably assume the
           same of this. -->
      <CollapsibleSection
        title="How this works"
        icon="info"
        tone="blue"
        :default-open="false"
        class="shrink-0"
      >
        <div class="flex flex-col gap-2 text-[12px] leading-relaxed">
          <p>
            Two cards run on two crystals, and neither is obliged to produce exactly the rate it was
            asked for. This plays a tone on the output card and reads its frequency on the input
            card: the difference between what was played and what came back <em>is</em> the
            difference between the two clocks. Neither card's own error against true time is
            knowable from one cable, and neither needs to be — only the difference reaches a
            measurement.
          </p>
          <p>
            <strong>Calibration is not needed for this.</strong> The answer is a ratio of two
            frequencies, so the volts scale never enters it — an uncalibrated card gives the same
            drift figure as a calibrated one, and even a badly wrong level would not bias it, since
            clipping adds harmonics without moving the fundamental. On an uncalibrated card the
            Level box reads in <strong>dBFS</strong>, where 0 is the clipping point, and −20 is a
            good place to measure from.
          </p>
        </div>
      </CollapsibleSection>

      <div class="dialog-card shrink-0">
        <div class="sc-body pt-1">
          <label class="sc-row">
            <span class="sc-label">Tone</span>
            <span class="flex items-center gap-2">
              <input
                v-model.number="form.toneHz"
                type="number"
                step="100"
                max="20000"
                class="tb-input w-28"
                :disabled="running"
                @change="form.toneHz = clampNumberField(form.toneHz, 20, 20000)"
              />
              <span class="text-neutral-500">Hz</span>
            </span>
          </label>
          <label class="sc-row">
            <span class="sc-label">Level</span>
            <span class="flex items-center gap-2">
              <input
                v-model.number="form.levelDbv"
                type="number"
                step="1"
                max="20"
                class="tb-input w-28"
                :disabled="running"
                @change="form.levelDbv = clampNumberField(form.levelDbv, -80, 20)"
              />
              <span class="text-neutral-500">{{ levelUnit }}</span>
            </span>
          </label>
          <label class="sc-row">
            <span class="sc-label">For</span>
            <span class="flex items-center gap-2">
              <input
                v-model.number="form.durationSeconds"
                type="number"
                step="10"
                max="3600"
                class="tb-input w-28"
                :disabled="running"
                @change="form.durationSeconds = clampNumberField(form.durationSeconds, 0, 3600)"
              />
              <span class="text-neutral-500">s — 0 runs until stopped</span>
            </span>
          </label>
        </div>

        <div class="mt-3 flex flex-wrap items-center gap-3">
          <button
            v-if="!running"
            type="button"
            class="btn-run-go w-52 flex-none"
            :disabled="busy"
            @click="onStart"
          >
            Measure
          </button>
          <button v-else type="button" class="btn-run-stop w-52 flex-none" :disabled="busy" @click="cancel">
            Stop
          </button>
          <button
            type="button"
            class="btn-secondary"
            :disabled="running || !points.length"
            @click="clear"
          >
            Clear
          </button>

          <span v-if="status" class="text-[12px] text-neutral-600 dark:text-neutral-300">
            {{ status.phase }}<span v-if="points.length"> · {{ points.length }} readings</span>
            <span v-if="status.playedToneHz">
              · playing {{ status.playedToneHz.toFixed(3) }} Hz on {{ status.measuredChannel }}
            </span>
          </span>
        </div>

        <div v-if="progressPercent !== null && running" class="tb-progress mt-2">
          <div :style="{ width: `${progressPercent}%` }"></div>
        </div>
      </div>

      <!-- The chart and the empty state are alternatives rather than a stack: uPlot draws nothing
           at all without a data range, so rendering both left a tall blank box with a caption
           under it. v-show rather than v-if on the host, because buildPlot needs the element to
           exist and to have a width when the dialog opens. -->
      <div class="dialog-card shrink-0 flex min-h-[260px] items-center justify-center">
        <div v-show="points.length" ref="host" class="w-full"></div>
        <p v-if="!points.length" class="px-4 text-center text-[12px] text-neutral-500 dark:text-neutral-400">
          Press Measure. A reading lands about once a second.
        </p>
      </div>

      <div v-if="status && points.length" class="dialog-card shrink-0 text-[12px]">
        <div class="grid grid-cols-2 gap-x-6 gap-y-1 sm:grid-cols-4">
          <div><span class="text-neutral-500">Drift</span><br /><span class="font-mono tabular-nums">{{ fmt(status.meanPpm, 2, ' ppm') }}</span></div>
          <div><span class="text-neutral-500">Spread</span><br /><span class="font-mono tabular-nums">{{ fmt(status.spreadPpm, 2, ' ppm') }}</span></div>
          <div><span class="text-neutral-500">σ</span><br /><span class="font-mono tabular-nums">{{ fmt(status.sigmaPpm, 3, ' ppm') }}</span></div>
          <div><span class="text-neutral-500">Tone margin</span><br /><span class="font-mono tabular-nums">{{ fmt(weakestDominanceDb, 1, ' dB') }}</span></div>
        </div>
      </div>

      <div
        v-if="verdict"
        class="dialog-card shrink-0 rounded border px-3 py-2 text-[12px] leading-relaxed"
        :class="verdictTone"
      >
        <strong>{{ verdict.headline }}</strong>
        <p class="mt-1">{{ verdict.detail }}</p>
        <p v-if="status?.chirpCost" class="mt-2">{{ status.chirpCost }}</p>
      </div>

      <p
        v-if="status?.warning"
        class="dialog-card shrink-0 rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
      >
        {{ status.warning }}
      </p>

      <p
        v-if="status?.error || error"
        class="dialog-card shrink-0 rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
      >
        {{ status?.error ?? error }}
      </p>
    </div>

    <template #footer>
      <button
        type="button"
        class="mr-auto inline-flex h-9 items-center gap-1.5 rounded-lg border border-neutral-200 px-3 text-[13px] font-semibold hover:bg-neutral-100 dark:border-neutral-800 dark:hover:bg-neutral-800"
        title="Clock drift: what the figure means, and which measurements it reaches."
        @click="openManual('clock-drift')"
      >
        <HeaderIcon name="manual" :size="16" />
        Manual
      </button>
      <button type="button" class="btn-primary" @click="close">Close</button>
    </template>
  </Modal>
</template>
