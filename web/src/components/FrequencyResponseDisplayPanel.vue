<script setup lang="ts">
import { computed } from 'vue'
import type { ResponseView } from '../api/client'
import { useFrequencyResponse } from '../composables/useFrequencyResponse'
import CollapsibleSection from './CollapsibleSection.vue'

const view = defineModel<ResponseView>('view', { required: true })
const showLeft = defineModel<boolean>('showLeft', { required: true })
const showRight = defineModel<boolean>('showRight', { required: true })
const showPhase = defineModel<boolean>('showPhase', { required: true })
const showCoherence = defineModel<boolean>('showCoherence', { required: true })
const showOneDb = defineModel<boolean>('showOneDb', { required: true })
const showThreeDb = defineModel<boolean>('showThreeDb', { required: true })
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
const yTopDb = defineModel<number>('yTopDb', { required: true })
const yBottomDb = defineModel<number>('yBottomDb', { required: true })
const xFromHz = defineModel<number>('xFromHz', { required: true })
const xToHz = defineModel<number>('xToHz', { required: true })

const { traces, averageCount } = useFrequencyResponse()

const DB_MIN = -200
const DB_MAX = 200
const DB_STEP = 2
const DB_MIN_SPAN = 4

const X_FROM_OPTIONS = [1, 2, 5, 10, 20, 50, 100, 200, 500]
const X_TO_OPTIONS = [1000, 2000, 5000, 10000, 20000, 50000, 100000]

/**
 * Clamping through `:value` + `@change` rather than `defineModel` + `@change`. Two reasons, both
 * learned the hard way on the sweep panel: a `defineModel` write goes out as an emit and comes back
 * as a prop, so a handler in the same tick still reads the *previous* value and clamps against it,
 * writing the old bound back over what was just typed. And `change` fires on blur, Enter and each
 * spinner click but not per keystroke, so the axis does not thrash while someone types "-160".
 */
function commitDb(event: Event, current: number, lo: number, hi: number): number {
  const el = event.target as HTMLInputElement
  const typed = Number(el.value)
  const next = Number.isFinite(typed) && el.value.trim() !== '' ? Math.min(hi, Math.max(lo, typed)) : current

  el.value = String(next)
  return next
}

function onTopDbChange(event: Event) {
  yTopDb.value = commitDb(event, yTopDb.value, yBottomDb.value + DB_MIN_SPAN, DB_MAX)
}

function onBottomDbChange(event: Event) {
  yBottomDb.value = commitDb(event, yBottomDb.value, DB_MIN, yTopDb.value - DB_MIN_SPAN)
}

/** The curve autofit sizes against: whichever one the current view is actually about. */
const fitTrace = computed(() => {
  if (view.value === 'Gain') return traces.value.find((t) => t.kind === 'Gain') ?? null
  return (showLeft.value ? traces.value.find((t) => t.kind === 'Left') : null)
    ?? traces.value.find((t) => t.kind === 'Right')
    ?? traces.value.find((t) => t.kind === 'Left')
    ?? null
})

/**
 * Fits the dB window to the curve, rounded outward to whole steps and never tighter than
 * DB_MIN_SPAN. Sized off the min *and* max columns, not just the plotted line: the band beneath it
 * is part of the curve, and fitting to the line alone clips the ripple out of view.
 */
function autofitDb() {
  const trace = fitTrace.value
  if (!trace || trace.magnitudeMaxDb.length === 0) return

  let lo = Infinity
  let hi = -Infinity
  for (let i = 0; i < trace.magnitudeMaxDb.length; i++) {
    const freqHz = trace.freqHz[i]
    if (freqHz < xFromHz.value || freqHz > xToHz.value) continue
    lo = Math.min(lo, trace.magnitudeMinDb[i])
    hi = Math.max(hi, trace.magnitudeMaxDb[i])
  }

  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return

  const top = Math.ceil((hi + 1) / DB_STEP) * DB_STEP
  const bottom = Math.floor((lo - 1) / DB_STEP) * DB_STEP

  yTopDb.value = Math.min(DB_MAX, Math.max(top, bottom + DB_MIN_SPAN))
  yBottomDb.value = Math.max(DB_MIN, Math.min(bottom, yTopDb.value - DB_MIN_SPAN))
}

/** Fits the frequency range to the measured band, snapped to the dropdown's own options. */
function autofitX() {
  const trace = fitTrace.value
  if (!trace || trace.freqHz.length === 0) return

  const from = trace.startFreqHz
  const to = trace.endFreqHz

  xFromHz.value = [...X_FROM_OPTIONS].reverse().find((v) => v <= from) ?? X_FROM_OPTIONS[0]
  xToHz.value = X_TO_OPTIONS.find((v) => v >= to) ?? X_TO_OPTIONS[X_TO_OPTIONS.length - 1]
}
</script>

<template>
  <CollapsibleSection title="Display">
    <div class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">View</span>
      <div class="flex overflow-hidden rounded border border-neutral-300 dark:border-neutral-700">
        <button
          type="button"
          class="flex-1 px-2 py-1"
          :class="
            view === 'Dbv'
              ? 'bg-amber-100 font-medium dark:bg-amber-900/40'
              : 'bg-white dark:bg-neutral-800'
          "
          @click="view = 'Dbv'"
        >
          FR (dBV)
        </button>
        <button
          type="button"
          class="flex-1 border-l border-neutral-300 px-2 py-1 dark:border-neutral-700"
          :class="
            view === 'Gain'
              ? 'bg-amber-100 font-medium dark:bg-amber-900/40'
              : 'bg-white dark:bg-neutral-800'
          "
          @click="view = 'Gain'"
        >
          Gain (dB)
        </button>
      </div>
    </div>

    <div v-if="view === 'Dbv'" class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Channels</span>
      <label class="flex items-center gap-1.5"><input v-model="showLeft" type="checkbox" /> Left</label>
      <label class="flex items-center gap-1.5"><input v-model="showRight" type="checkbox" /> Right</label>
    </div>

    <div class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Bandwidth</span>
      <label class="flex items-center gap-1.5" title="Dashed markers and a labelled arrow at the -1 dB corners.">
        <input v-model="showOneDb" type="checkbox" /> −1 dB
      </label>
      <label class="flex items-center gap-1.5" title="Dashed markers and a labelled arrow at the -3 dB corners.">
        <input v-model="showThreeDb" type="checkbox" /> −3 dB
      </label>
    </div>

    <div class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Extra traces</span>
      <label class="flex items-center gap-1.5" title="Unwrapped phase, with the bulk propagation delay removed, on its own axis at the right in degrees. The axis fits itself to the measured range.">
        <input v-model="showPhase" type="checkbox" /> Phase
      </label>
      <label
        class="flex items-center gap-1.5"
        :title="
          averageCount > 1
            ? 'How repeatable the response was, per bin, across the averaged records: 1 means every record agreed, and it falls where noise or too little stimulus energy makes that bin untrustworthy. Own axis at the right, 0 to 1.'
            : 'Needs two or more averages — a single record always reads 1.'
        "
      >
        <input v-model="showCoherence" type="checkbox" /> Coherence
      </label>
      <p v-if="showCoherence && averageCount <= 1" class="text-[11px] text-amber-600 dark:text-amber-400">
        Coherence needs 2+ averages; a single record reads 1 everywhere.
      </p>
    </div>

    <div class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">dB range</span>
      <label class="flex items-center justify-between gap-2">
        Top
        <input
          :value="yTopDb"
          type="number"
          :step="DB_STEP"
          class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="onTopDbChange"
        />
      </label>
      <label class="flex items-center justify-between gap-2">
        Bottom
        <input
          :value="yBottomDb"
          type="number"
          :step="DB_STEP"
          class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="onBottomDbChange"
        />
      </label>
      <button
        type="button"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        @click="autofitDb"
      >
        Autofit
      </button>
    </div>

    <div class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Frequency range (Hz)</span>
      <label class="flex items-center justify-between gap-2">
        From
        <select
          v-model.number="xFromHz"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        >
          <option v-for="hz in X_FROM_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
        </select>
      </label>
      <label class="flex items-center justify-between gap-2">
        To
        <select
          v-model.number="xToHz"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        >
          <option v-for="hz in X_TO_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
        </select>
      </label>
      <button
        type="button"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        @click="autofitX"
      >
        Autofit
      </button>
    </div>

    <div class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Style</span>
      <label class="flex items-center gap-1.5"><input v-model="thickLines" type="checkbox" /> Thick lines</label>
      <label class="flex items-center gap-1.5">
        <input v-model="showDataPoints" type="checkbox" /> Show data points
      </label>
    </div>
  </CollapsibleSection>
</template>
