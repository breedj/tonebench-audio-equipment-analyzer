<script setup lang="ts">
import { computed, watch } from 'vue'
import { useChartDisplay } from '../composables/useChartDisplay'
import { useExperimental384k } from '../composables/useExperimental384k'
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
  showFftTrace,
  autofitOnDone,
  showWaterfall,
  showOscilloscope,
  showHarmonics,
  showOtherPeaks,
  spectrumUnit,
  dutGainDb,
  showWeightingCurve,
  showHarmonicMarkers,
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

/**
 * Nyquist's own bucket for the experimental 384 kHz rate (the top of Autofit's ladder — see
 * autofitFreqToFor in SpectrumChart.vue), offered only while that rate is: the list otherwise ends
 * at 100 kHz, which is the whole of a 192 kHz capture and all any other rate can show.
 *
 * It has to be a *preset* rather than something Autofit leaves behind. withCurrentValue below folds
 * whatever is selected into the list, so pressing Autofit at 384 kHz did put 200000 in the dropdown
 * — and picking any other entry dropped it again, with no way back to the band short of another
 * Autofit. Keyed on the setting and not on the staged sample rate, so the entry does not appear and
 * vanish as the rate is changed; a window wider than Nyquist just shows empty spectrum above it,
 * which 100 kHz already does at 48 and 96 kHz.
 */
const EXPERIMENTAL_384K_FREQ_TO_HZ = 200000
const { experimental384kEnabled, load: loadExperimental384k } = useExperimental384k()
// Idempotent (the composable keeps one in-flight promise). ConfigDialog is the only other caller
// and it is mounted on demand, so this screen cannot rely on its having run.
void loadExperimental384k()

// Dropdowns, like Stimulus — but Autofit can land on a value outside the preset list (e.g. 25000,
// from the sample-rate-derived target), so the current value is folded into its own list rather
// than dropped, the same way a value loaded from a saved measurement wouldn't be silently lost.
function withCurrentValue(presets: number[], current: number): number[] {
  return presets.includes(current) ? presets : [...presets, current].sort((a, b) => a - b)
}
const fromOptions = computed(() => withCurrentValue(FREQ_FROM_PRESETS, xFrom.value))
const toOptions = computed(() =>
  withCurrentValue(
    experimental384kEnabled.value ? [...FREQ_TO_PRESETS, EXPERIMENTAL_384K_FREQ_TO_HZ] : FREQ_TO_PRESETS,
    xTo.value,
  ),
)

// A unit switch moves every level on the chart by the same constant (about -6 dB for a 4 Hz ENBW,
// -20 dB more for a DUT of 20 dB gain), so the window that fitted the old unit is wrong for the
// new one. Refit rather than keep it; the user can still type a range afterwards.
function onUnitChange() {
  requestAutofitY()
}

/**
 * One Autofit per chart, not one per axis (Joost, 2026-09-30): it fits every axis at once, so
 * "show me all of it" is one press wherever you are. The per-axis buttons are gone from every
 * display panel; the axis fields themselves stay, for a window chosen by hand.
 *
 * Order does not matter on this chart — SpectrumChart's own level fit deliberately reads the whole
 * span rather than the plotted window (see computeYExtent there) — but it is x-then-y everywhere
 * else, where the level fit is scoped to the frequency window, so it is x-then-y here too.
 */
function autofitAll() {
  requestAutofitX()
  requestAutofitY()
}

// The DUT gain is only ever *read* by the input-referred unit and by the "Noise in nV/√Hz" tile, and
// both of those divide by a gain of 0 dB, i.e. by nothing. So the field is shown when it is selected
// (the unit) or when it is doing something (a non-zero value) and hidden otherwise — hidden always
// means 0 dB, so nothing on screen is ever changed by a value the user cannot see. Note the
// consequence: with the chart in dBV and the gain at 0, the way to reach the field is to pick the
// input-referred unit; it then stays visible after switching back, because it still divides the tile.
// The Units tooltip says so.
const showDutGain = computed(() => spectrumUnit.value === 'dBVPerRootHzInput' || dutGainDb.value !== 0)
</script>

<template>
  <!-- `contents`: the sections (and the checklist passed through the slot) are laid out directly
       by MeasurementLayout's aside grid, so under the chart they flow two-wide like every other
       screen's display panel. The width is the shell's xl column, the same 16rem as every other screen. -->
  <div class="contents">
    <CollapsibleSection title="Level range" size="xs">
      <label class="flex flex-col gap-1 text-xs" title="dBV per bin, or a noise density in dBV/√Hz (the bin's power spread over the window's noise bandwidth), optionally referred to the DUT's input by its gain. Picking the input-referred unit reveals the DUT gain field below, which also divides the Noise in nV/√Hz tile. See the manual's FFT analysis page.">
        Units
        <select
          v-model="spectrumUnit"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700"
          @change="onUnitChange"
        >
          <option v-for="u in SPECTRUM_UNIT_OPTIONS" :key="u.value" :value="u.value">{{ u.label }}</option>
        </select>
      </label>
      <label
        v-if="showDutGain"
        class="flex flex-col gap-1 text-xs"
        title="The DUT's gain from its input to the analyzer's input, in dB. The input-referred unit and the Noise in nV/√Hz tile divide the measured noise density by it, so a preamp's output floor reads as the noise at its input. 0 for a loopback. Saved with the measurement."
      >
        DUT gain (dB)
        <input
          v-model.number="dutGainDb"
          type="number"
          step="0.1"
          class="rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700"
        />
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
      <!-- The chart's one Autofit. It sits in the first section rather than in Display below,
           where it would be a scroll away from the fields it overwrites. -->
      <button
        type="button"
        class="w-full rounded border px-2 py-1 text-xs btn-action"
        title="Fits both axes to everything currently plotted: the level range to the highest and lowest point with 10 dB of headroom, and the frequency range to the full measured span."
        @click="autofitAll"
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
        <label
          class="flex items-center gap-1.5"
          title="The live capture: the spectrum line and the min/max band under it, which is the same data. Unchecking it clears Min/max too and leaves the peak-hold trace on its own — a still picture of the worst case so far, with nothing redrawn over the top of it. Ticking it back on restores the band."
        >
          <input v-model="showFftTrace" type="checkbox" />
          FFT data
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
          class="w-full rounded border px-2 py-1 text-xs btn-action"
          title="Clears the peak-hold trace and starts capturing fresh maximums."
          @click="requestResetPeakHold"
        >
          Reset peak-hold
        </button>
        <label
          class="flex items-center gap-1.5"
          title="The vertical lines at H2, H3 … over the spectrum, each labelled with its level in dBc. The harmonics table below the chart reads the same figures either way."
        >
          <input v-model="showHarmonicMarkers" type="checkbox" />
          Harmonic markers
        </label>
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
           three that come with it. Oscilloscope and Harmonics are on by default, Waterfall and Other
           peaks off -- see useChartDisplay.ts. Waterfall and Oscilloscope are hidden rather than
           unmounted (App.vue), so what they showed comes back. -->
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
