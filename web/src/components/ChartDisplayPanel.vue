<script setup lang="ts">
import { computed, watch } from 'vue'
import { useChartDisplay } from '../composables/useChartDisplay'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { SPECTRUM_UNIT_OPTIONS } from '../spectrumUnits'
import CollapsibleSection from './CollapsibleSection.vue'

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
  autofitOnDone,
  showWaterfall,
  showOscilloscope,
  showHarmonics,
  showOtherPeaks,
  spectrumUnit,
  showWeightingCurve,
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

// A unit switch moves every level on the chart by the same constant (about -6 dB for a 4 Hz ENBW,
// -20 dB more for a DUT of 20 dB gain), so the window that fitted the old unit is wrong for the
// new one. Refit rather than keep it; the user can still type a range afterwards.
function onUnitChange() {
  requestAutofitY()
}
</script>

<template>
  <!-- `contents`: the sections (and the checklist passed through the slot) are laid out directly
       by MeasurementLayout's aside grid, so under the chart they flow two-wide like every other
       screen's display panel. The width is the shell's xl column, the same 16rem as every other screen. -->
  <div class="contents">
    <CollapsibleSection title="Level range" size="xs">
      <label class="flex flex-col gap-1 text-xs" title="dBV per bin, or a noise density in dBV/√Hz (the bin's power spread over the window's noise bandwidth), optionally referred to the DUT's input by its gain. See the manual's FFT analysis page.">
        Units
        <select
          v-model="spectrumUnit"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700"
          @change="onUnitChange"
        >
          <option v-for="u in SPECTRUM_UNIT_OPTIONS" :key="u.value" :value="u.value">{{ u.label }}</option>
        </select>
      </label>
      <label class="flex flex-col gap-1 text-xs">
        Top
        <input
          v-model.number="yTop"
          type="number"
          step="10"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700"
        />
      </label>
      <label class="flex flex-col gap-1 text-xs">
        Bottom
        <input
          v-model.number="yBottom"
          type="number"
          step="10"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700"
        />
      </label>
      <button
        type="button"
        class="w-full rounded border border-blue-300 bg-blue-50 px-2 py-1 text-xs text-blue-700 hover:bg-blue-100 dark:border-blue-700 dark:bg-blue-950/40 dark:text-blue-300 dark:hover:bg-blue-900/40"
        title="Fits to the highest and lowest currently-plotted point, with 10 dB of headroom on each side."
        @click="requestAutofitY"
      >
        Autofit
      </button>
    </CollapsibleSection>

    <CollapsibleSection title="Frequency range" size="xs">
      <label class="flex flex-col gap-1 text-xs">
        From
        <select
          v-model.number="xFrom"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700"
        >
          <option v-for="f in fromOptions" :key="f" :value="f">{{ f }}</option>
        </select>
      </label>
      <label class="flex flex-col gap-1 text-xs">
        To
        <select
          v-model.number="xTo"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700"
        >
          <option v-for="f in toOptions" :key="f" :value="f">{{ f }}</option>
        </select>
      </label>
      <button
        type="button"
        class="w-full rounded border border-blue-300 bg-blue-50 px-2 py-1 text-xs text-blue-700 hover:bg-blue-100 dark:border-blue-700 dark:bg-blue-950/40 dark:text-blue-300 dark:hover:bg-blue-900/40"
        title="Sets From to 10 Hz and To just above the current sample rate's Nyquist frequency."
        @click="requestAutofitX"
      >
        Autofit
      </button>
    </CollapsibleSection>

    <!-- text-xs throughout, the same size SweepDisplayPanel uses, so the two screens' display
         rails read alike; the Channels row is that panel's too (caption, then Left/Right side by side). -->
    <CollapsibleSection title="Display">
      <section class="flex flex-col gap-1.5 text-xs">
        <label class="flex items-center gap-1.5">
          <input v-model="thickLines" type="checkbox" />
          Thick lines
        </label>
        <label class="flex items-center gap-1.5">
          <input v-model="showDataPoints" type="checkbox" />
          Show data points
        </label>
        <label class="flex items-center gap-1.5">
          <input v-model="autofitOnDone" type="checkbox" />
          Autofit when measurement done
        </label>
      </section>

      <div class="flex flex-col gap-1.5 text-xs">
        <span class="text-neutral-500 dark:text-neutral-400">Channels</span>
        <div class="flex gap-3">
          <label class="flex items-center gap-1.5" :class="{ 'opacity-40': !processLeft }">
            <input v-model="visibleL" type="checkbox" :disabled="!processLeft" />
            Left
          </label>
          <label class="flex items-center gap-1.5" :class="{ 'opacity-40': !processRight }">
            <input v-model="visibleR" type="checkbox" :disabled="!processRight" />
            Right
          </label>
        </div>
      </div>

      <section class="flex flex-col gap-1.5 text-xs">
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
        <label
          class="flex items-center gap-1.5"
          title="Draws the weighting in effect as a dashed amber curve — shape only, hung 20 dB below the top of the level range. Nothing is drawn while the weighting is None."
        >
          <input v-model="showWeightingCurve" type="checkbox" />
          Weighting curve
        </label>
      </section>

      <!-- Which of the secondary panels the FFT screen draws at all. Unlike the toggles above,
           these are not about the spectrum chart's own content: they show and hide whole panels
           under it, so a measurement that only needs the spectrum doesn't have to scroll past the
           three that come with it. All on by default -- see useChartDisplay.ts. Waterfall and
           Oscilloscope are hidden rather than unmounted (App.vue), so what they showed comes back. -->
      <section class="flex flex-col gap-1.5 border-t border-neutral-200 pt-2 text-xs dark:border-neutral-800">
        <label class="flex items-center gap-1.5">
          <input v-model="showWaterfall" type="checkbox" />
          Waterfall
        </label>
        <label class="flex items-center gap-1.5">
          <input v-model="showOscilloscope" type="checkbox" />
          Oscilloscope
        </label>
        <label class="flex items-center gap-1.5">
          <input v-model="showHarmonics" type="checkbox" />
          Harmonics
        </label>
        <label class="flex items-center gap-1.5">
          <input v-model="showOtherPeaks" type="checkbox" />
          Other peaks
        </label>
      </section>
    </CollapsibleSection>

    <!-- Measurements checklist + history, injected here by App.vue so they sit underneath Display
         at the same column width, for both the FFT and sweep screens. -->
    <slot />
  </div>
</template>
