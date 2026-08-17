<script setup lang="ts">
import { computed, watch } from 'vue'
import { useChartDisplay } from '../composables/useChartDisplay'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'

const {
  yTop,
  yBottom,
  xFrom,
  xTo,
  thickLines,
  showDataPoints,
  visibleL,
  visibleR,
  showMinMax,
  showPeakHold,
  requestAutofitY,
  requestAutofitX,
  requestResetPeakHold,
} = useChartDisplay()

// FFT settings' L/R checkboxes (SpectrumConfigPanel.vue) are the source of truth for whether a
// channel is processed at all — a channel turned off there has no data to show, so its own
// show/hide checkbox here is forced to match and locked rather than left toggleable.
const { config: spectrumConfig } = useSpectrumConfig()
const processLeft = computed(() => spectrumConfig.value?.processLeft ?? true)
const processRight = computed(() => spectrumConfig.value?.processRight ?? false)

watch(processLeft, (enabled) => { visibleL.value = enabled }, { immediate: true })
watch(processRight, (enabled) => { visibleR.value = enabled }, { immediate: true })

const FREQ_FROM_PRESETS = [5, 10, 20, 50, 100, 200, 500]
const FREQ_TO_PRESETS = [1000, 2000, 5000, 10000, 20000, 100000]

// Dropdowns, like Stimulus — but Autofit can land on a value outside the preset list (e.g. 25000,
// from the sample-rate-derived target), so the current value is folded into its own list rather
// than dropped, the same way a value loaded from a saved measurement wouldn't be silently lost.
function withCurrentValue(presets: number[], current: number): number[] {
  return presets.includes(current) ? presets : [...presets, current].sort((a, b) => a - b)
}
const fromOptions = computed(() => withCurrentValue(FREQ_FROM_PRESETS, xFrom.value))
const toOptions = computed(() => withCurrentValue(FREQ_TO_PRESETS, xTo.value))
</script>

<template>
  <div class="flex w-full flex-col gap-3 lg:w-48 lg:flex-none">
    <section class="rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
      <h2 class="mb-2 text-xs font-semibold text-neutral-900 dark:text-neutral-100">dBV range</h2>
      <label class="mb-1 flex flex-col gap-1 text-xs">
        Top
        <input
          v-model.number="yTop"
          type="number"
          step="10"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
      <label class="mb-2 flex flex-col gap-1 text-xs">
        Bottom
        <input
          v-model.number="yBottom"
          type="number"
          step="10"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
      <button
        type="button"
        class="w-full rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Fits to the highest and lowest currently-plotted point, with 10 dBV of headroom on each side."
        @click="requestAutofitY"
      >
        Autofit
      </button>
    </section>

    <section class="rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
      <h2 class="mb-2 text-xs font-semibold text-neutral-900 dark:text-neutral-100">Frequency range</h2>
      <label class="mb-1 flex flex-col gap-1 text-xs">
        From
        <select
          v-model.number="xFrom"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700 dark:bg-neutral-800"
        >
          <option v-for="f in fromOptions" :key="f" :value="f">{{ f }}</option>
        </select>
      </label>
      <label class="mb-2 flex flex-col gap-1 text-xs">
        To
        <select
          v-model.number="xTo"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700 dark:bg-neutral-800"
        >
          <option v-for="f in toOptions" :key="f" :value="f">{{ f }}</option>
        </select>
      </label>
      <button
        type="button"
        class="w-full rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Sets From to 10 Hz and To just above the current sample rate's Nyquist frequency."
        @click="requestAutofitX"
      >
        Autofit
      </button>
    </section>

    <!-- Measurements checklist injected here by App.vue, matching where the wireframe places it in this column. -->
    <slot />

    <section class="flex flex-col gap-1.5 text-sm">
      <label class="flex items-center gap-1.5">
        <input v-model="thickLines" type="checkbox" />
        Thick lines
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="showDataPoints" type="checkbox" />
        Show data points
      </label>
    </section>

    <section class="flex flex-col gap-1.5 text-sm">
      <label class="flex items-center gap-1.5" :class="{ 'opacity-40': !processLeft }">
        <input v-model="visibleL" type="checkbox" :disabled="!processLeft" />
        Left channel
      </label>
      <label class="flex items-center gap-1.5" :class="{ 'opacity-40': !processRight }">
        <input v-model="visibleR" type="checkbox" :disabled="!processRight" />
        Right channel
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="showMinMax" type="checkbox" />
        Min/max
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="showPeakHold" type="checkbox" />
        Peak-hold
      </label>
      <button
        v-if="showPeakHold"
        type="button"
        class="w-full rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Clears the peak-hold trace and starts capturing fresh maximums."
        @click="requestResetPeakHold"
      >
        Reset peak-hold
      </button>
    </section>
  </div>
</template>
