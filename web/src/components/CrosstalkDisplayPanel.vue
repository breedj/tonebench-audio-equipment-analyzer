<script setup lang="ts">
import { computed, watch } from 'vue'
import { useFrequencyResponse, type ResponseTrace } from '../composables/useFrequencyResponse'
import { useMeasurements } from '../composables/useMeasurements'
import CollapsibleSection from './CollapsibleSection.vue'

/**
 * The crosstalk chart's display rail: a dB window, a frequency window, one Autofit for both, and the
 * two style toggles. Its own small panel rather than a `crosstalk` flag on
 * FrequencyResponseDisplayPanel — that panel is a projection/view/phase/coherence/bandwidth rail
 * for a Bode plot, and every one of those controls would be hidden here.
 */
const yTopDb = defineModel<number>('yTopDb', { required: true })
const yBottomDb = defineModel<number>('yBottomDb', { required: true })
const xFromHz = defineModel<number>('xFromHz', { required: true })
const xToHz = defineModel<number>('xToHz', { required: true })
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
const autofitOnDone = defineModel<boolean>('autofitOnDone', { required: true })

const { traces, running } = useFrequencyResponse()
const { overlays } = useMeasurements()

const DB_MIN = -200
const DB_MAX = 40
const DB_STEP = 5
const DB_MIN_SPAN = 10

const X_FROM_OPTIONS = [1, 2, 5, 10, 20, 50, 100, 200, 500]
const X_TO_OPTIONS = [1000, 2000, 5000, 10000, 20000, 50000, 100000]

/** See FrequencyResponseDisplayPanel.commitBoundedNumber for why `:value` + `@change`. */
function commitBoundedNumber(event: Event, current: number, lo: number, hi: number): number {
  const el = event.target as HTMLInputElement
  const typed = Number(el.value)
  const next = Number.isFinite(typed) && el.value.trim() !== '' ? Math.min(hi, Math.max(lo, typed)) : current
  el.value = String(next)
  return next
}

function onTopChange(event: Event) {
  yTopDb.value = commitBoundedNumber(event, yTopDb.value, yBottomDb.value + DB_MIN_SPAN, DB_MAX)
}

function onBottomChange(event: Event) {
  yBottomDb.value = commitBoundedNumber(event, yBottomDb.value, DB_MIN, yTopDb.value - DB_MIN_SPAN)
}

function isCrosstalk(t: ResponseTrace): boolean {
  return t.kind === 'CrosstalkLeftToRight' || t.kind === 'CrosstalkRightToLeft'
}

/** Every crosstalk curve on the chart: live plus checked-on history. */
const fitTraces = computed<ResponseTrace[]>(() => [
  ...traces.value.filter(isCrosstalk),
  ...overlays.value.filter((o) => o.visible).flatMap((o) => (o.responseTraces ?? []).filter(isCrosstalk)),
])

function autofitDb() {
  let lo = Infinity
  let hi = -Infinity
  for (const trace of fitTraces.value) {
    for (let i = 0; i < trace.magnitudeMaxDb.length; i++) {
      const f = trace.freqHz[i]
      if (f < xFromHz.value || f > xToHz.value) continue
      lo = Math.min(lo, trace.magnitudeMinDb[i])
      hi = Math.max(hi, trace.magnitudeMaxDb[i])
    }
  }
  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return

  const top = Math.ceil((hi + 2) / DB_STEP) * DB_STEP
  const bottom = Math.floor((lo - 2) / DB_STEP) * DB_STEP
  yTopDb.value = Math.min(DB_MAX, Math.max(top, bottom + DB_MIN_SPAN))
  yBottomDb.value = Math.max(DB_MIN, Math.min(bottom, yTopDb.value - DB_MIN_SPAN))
}

function autofitX() {
  const list = fitTraces.value.filter((t) => t.freqHz.length > 0)
  if (list.length === 0) return
  const from = Math.min(...list.map((t) => t.startFreqHz))
  const to = Math.max(...list.map((t) => t.endFreqHz))
  xFromHz.value = [...X_FROM_OPTIONS].reverse().find((v) => v <= from) ?? X_FROM_OPTIONS[0]
  xToHz.value = X_TO_OPTIONS.find((v) => v >= to) ?? X_TO_OPTIONS[X_TO_OPTIONS.length - 1]
}

/**
 * One Autofit per chart, not one per axis (Joost, 2026-09-30). The frequency window is fitted
 * first: every level fit below is scoped to it, so fitting the other way round sizes the axis to
 * whatever the old window happened to hold.
 */
function autofitAll() {
  autofitX()
  autofitDb()
}

watch(running, (isRunning, wasRunning) => {
  if (wasRunning && !isRunning && autofitOnDone.value) autofitAll()
})
</script>

<template>
  <CollapsibleSection title="Crosstalk range (dB)" size="xs">
    <label class="flex flex-col gap-1 text-xs">
      Top
      <input
        :value="yTopDb"
        type="number"
        :step="DB_STEP"
        class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        @change="onTopChange"
      />
    </label>
    <label class="flex flex-col gap-1 text-xs">
      Bottom
      <input
        :value="yBottomDb"
        type="number"
        :step="DB_STEP"
        class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        @change="onBottomChange"
      />
    </label>
    <!-- The chart's one Autofit, in the first section rather than a scroll below the fields it
         overwrites. -->
    <button
      type="button"
      class="w-full rounded border px-2 py-1 text-xs btn-action"
      title="Fits both axes to every crosstalk curve on the chart, live and history: the frequency range to the measured span, the dB window to the curves inside it."
      @click="autofitAll"
    >
      Autofit
    </button>
  </CollapsibleSection>

  <CollapsibleSection title="Frequency range (Hz)" size="xs">
    <label class="flex flex-col gap-1 text-xs">
      From
      <select v-model.number="xFromHz" class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
        <option v-for="hz in X_FROM_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
      </select>
    </label>
    <label class="flex flex-col gap-1 text-xs">
      To
      <select v-model.number="xToHz" class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
        <option v-for="hz in X_TO_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
      </select>
    </label>
  </CollapsibleSection>

  <CollapsibleSection title="Display">
    <label class="flex items-center gap-1.5 text-xs">
      <input v-model="autofitOnDone" type="checkbox" /> Autofit when measurement done
    </label>
    <div class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Style</span>
      <label class="flex items-center gap-1.5"><input v-model="thickLines" type="checkbox" /> Thick lines</label>
      <label class="flex items-center gap-1.5"><input v-model="showDataPoints" type="checkbox" /> Show data points</label>
    </div>
  </CollapsibleSection>
</template>
