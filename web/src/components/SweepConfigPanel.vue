<script setup lang="ts">
import { computed, nextTick, reactive, watch } from 'vue'
import type {
  SweepLevelMode,
  SweepLevelUnit,
  ThdSweepStartRequest,
  ThdVsFrequencyParameters,
  WindowType,
} from '../api/client'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useProjects } from '../composables/useProjects'
import {
  adaptiveFftBounds,
  adaptiveFftSize,
  adaptiveSampleRateHz,
  estimateAdaptiveDurationSeconds,
  estimateDurationSeconds,
  formatDuration,
  logPerOctave,
} from '../sweep/points'

/**
 * Everything a THD-vs-frequency sweep needs, staged locally and only sent on Run — the same
 * staged-not-live model the Acquisition fields use, because none of these can be changed
 * mid-sweep anyway.
 */

defineProps<{
  running: boolean
}>()

const emit = defineEmits<{
  run: [request: ThdSweepStartRequest]
}>()

const { loadOhms } = useLoadOhms()
const { types, currentTypeId, markDirty } = useProjects()

/**
 * A sweep's analysis resolution is fixed, not offered as a choice.
 *
 * Every frequency in the sweep is measured with one FFT size, and the bottom of the range is what
 * constrains it: HarmonicAnalyzer sums +/-3 bins around each harmonic, so a fundamental fewer than
 * ~5 bins up has its own second harmonic overlapping it and THD reads near 100%. At the previous
 * default of 192 kHz / 32768 (5.86 Hz bins) a 10 Hz sweep started at 17.6 Hz and its first two
 * points were fabricated. 192 kHz / 131072 gives 1.465 Hz bins -- 10 Hz lands on bin 7, and the
 * 0.68 s record keeps a 50-point sweep near a minute. The server enforces the same floor in
 * ThdSweepRequest.Validate; this just stops anyone walking into it.
 */
const SWEEP_SAMPLE_RATE_HZ = 192000
const SWEEP_FFT_SIZE = 131072
const SWEEP_BIN_SIZE_HZ = SWEEP_SAMPLE_RATE_HZ / SWEEP_FFT_SIZE

/** Bins the fundamental must be up before its harmonics can be measured -- ThdSweepRequest.MinFundamentalBins. */
const MIN_FUNDAMENTAL_BINS = 5

/**
 * Fixed, like the sample rate and FFT size above. Hann is the only window that makes sense here:
 * every sweep tone is snapped to a coherent bin, and the harmonic analysis assumes the mainlobe
 * shape it corrects for. Offering Rectangular or Flat Top only ever made a sweep worse.
 */
const SWEEP_WINDOW: WindowType = 'Hann'

// The three ways to specify drive level, ported from the previous app's E_GeneratorType. The two
// output-referred modes are what make "THD at 1 W into 8 ohm" expressible at all.
const LEVEL_MODES: { value: SweepLevelMode; label: string }[] = [
  { value: 'GeneratorVoltage', label: 'Generator voltage' },
  { value: 'OutputVoltage', label: 'DUT output voltage' },
  { value: 'OutputPower', label: 'DUT output power' },
]

const VOLTAGE_UNITS: { value: SweepLevelUnit; label: string }[] = [
  { value: 'Dbv', label: 'dBV' },
  { value: 'Volt', label: 'V' },
  { value: 'MilliVolt', label: 'mV' },
]

const form = reactive({
  startFreqHz: 20,
  endFreqHz: 20000,
  stepsPerOctave: 5,
  levelMode: 'GeneratorVoltage' as SweepLevelMode,
  levelValue: -20,
  levelUnit: 'Dbv' as SweepLevelUnit,
  processLeft: true,
  processRight: false,
  averageCount: 4,
  measureNoiseFloor: true,
  /**
   * Size the FFT per point and pick the sample rate from the sweep range, rather than holding the
   * fixed preset below across every point — see SweepResolution server-side. On by default: a fixed
   * size is chosen for the bottom of the sweep and then paid at every point above it, which is most
   * of them.
   */
  adaptiveResolution: true,
})

/**
 * A sweep type's settings live here and nowhere else — unlike the FFT screen, none of these are
 * server state the host could snapshot on its own (see MeasurementSaveService.BuildParameters,
 * which can only ever describe the live pipeline/device). So this panel both reads and writes
 * `measurement_type.params_json` for the ThdVsFrequency kind: it loads the stored settings when a
 * type is selected, flags the type dirty on any edit, and hands the JSON back to App.vue on save.
 */
const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)

// Suppresses the dirty watcher while applyStoredParams writes the form — loading a type's saved
// settings is the opposite of an unsaved edit, and marking it dirty there would light the Save
// button up on every selection.
let restoring = false

function applyStoredParams(paramsJson: string) {
  let stored: Partial<ThdVsFrequencyParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<ThdVsFrequencyParameters>
  } catch {
    // A type saved before sweep settings were persisted holds an FftSpectrumParameters (or, for a
    // brand-new one, whatever the server seeded). Nothing to restore — keep the defaults.
    return
  }

  // Guarded field by field rather than Object.assign'd: an FFT params_json parses fine and would
  // otherwise overwrite the form with undefined.
  restoring = true
  if (typeof stored.startFreqHz === 'number') form.startFreqHz = stored.startFreqHz
  if (typeof stored.endFreqHz === 'number') form.endFreqHz = stored.endFreqHz
  if (typeof stored.stepsPerOctave === 'number') form.stepsPerOctave = stored.stepsPerOctave
  if (stored.levelMode) form.levelMode = stored.levelMode
  if (typeof stored.levelValue === 'number') form.levelValue = stored.levelValue
  if (stored.levelUnit) form.levelUnit = stored.levelUnit
  if (typeof stored.loadOhms === 'number' && stored.loadOhms > 0) loadOhms.value = stored.loadOhms
  if (typeof stored.processLeft === 'boolean') form.processLeft = stored.processLeft
  if (typeof stored.processRight === 'boolean') form.processRight = stored.processRight
  if (typeof stored.averageCount === 'number') form.averageCount = stored.averageCount
  // Absent in sweeps saved before adaptive resolution existed; those ran fixed, so that is what
  // they must reload as rather than silently changing resolution on reopen.
  form.adaptiveResolution = stored.adaptiveResolution === true
  if (typeof stored.measureNoiseFloor === 'boolean') form.measureNoiseFloor = stored.measureNoiseFloor

  // The form writes land in this tick; the watcher runs on the next one, so the flag has to
  // survive until then.
  void nextTick(() => {
    restoring = false
  })
}

watch(
  () => currentType.value?.paramsJson,
  (paramsJson) => {
    if (paramsJson) applyStoredParams(paramsJson)
  },
  { immediate: true },
)

// Deep, rather than a @change on each of the eleven inputs: every field here is part of the stored
// configuration, so "something in the form moved" is exactly the condition.
watch(
  [form, loadOhms],
  () => {
    if (!restoring) markDirty()
  },
  { deep: true },
)

const isPowerMode = computed(() => form.levelMode === 'OutputPower')

/**
 * Turning adaptive resolution off means asking for the fixed reference sweep, and 8 averages is
 * part of that preset — at the fixed 131072 points they are what buys back the dynamic range the
 * adaptive mode gets from sizing each record to its own tone. Only forced on the transition, so the
 * count stays freely editable afterwards in either mode.
 */
watch(
  () => form.adaptiveResolution,
  (adaptive, wasAdaptive) => {
    if (wasAdaptive && !adaptive) form.averageCount = 8
  },
)

/**
 * What the sweep will actually analyze at. Under adaptive resolution the sample rate falls out of
 * the sweep's top end (enough Nyquist for D2..D5) and the FFT size varies per point; the fixed
 * preset is the reference both fall back to. Mirrors ThdSweepRequest.EffectiveSampleRateHz.
 */
const effectiveSampleRateHz = computed(() =>
  form.adaptiveResolution ? adaptiveSampleRateHz(form.endFreqHz) : SWEEP_SAMPLE_RATE_HZ,
)

/** The pre-snap log grid, shared by the point count and the adaptive duration estimate. */
const gridHz = computed(() =>
  logPerOctave(form.startFreqHz, form.endFreqHz, form.stepsPerOctave).filter(
    (hz) => hz > 0 && hz <= 0.98 * (effectiveSampleRateHz.value / 2),
  ),
)

const pointCount = computed(() => gridHz.value.length)

/** FFT size at the two ends of the sweep — the range the readout quotes for adaptive mode. */
const adaptiveFftRange = computed(() => {
  if (gridHz.value.length === 0) return null
  const sizes = gridHz.value.map((hz) => adaptiveFftSize(hz, effectiveSampleRateHz.value))
  return { max: Math.max(...sizes), min: Math.min(...sizes) }
})

const durationLabel = computed(() =>
  formatDuration(
    form.adaptiveResolution
      ? estimateAdaptiveDurationSeconds(gridHz.value, effectiveSampleRateHz.value, form.averageCount)
      : estimateDurationSeconds(pointCount.value, SWEEP_FFT_SIZE, SWEEP_SAMPLE_RATE_HZ, form.averageCount),
  ),
)

/** The highest frequency the sweep can actually reach — points above it are dropped server-side. */
const maxFreqHz = computed(() => Math.round(0.98 * (effectiveSampleRateHz.value / 2)))

/**
 * The lowest frequency this resolution can carry a harmonic analysis at — matches the server's
 * rejection. Adaptive mode reaches for the largest permitted FFT at the bottom of a sweep, so the
 * floor is that size's bin width rather than the fixed preset's.
 */
const minFreqHz = computed(() => {
  const binSizeHz = form.adaptiveResolution
    ? effectiveSampleRateHz.value / adaptiveFftBounds(effectiveSampleRateHz.value).ceiling
    : SWEEP_BIN_SIZE_HZ
  return MIN_FUNDAMENTAL_BINS * binSizeHz
})

const truncated = computed(() => form.endFreqHz > maxFreqHz.value)

const invalid = computed(() => {
  if (!(form.startFreqHz > 0) || !(form.endFreqHz > form.startFreqHz)) return 'End frequency must be above the start.'
  if (form.startFreqHz < minFreqHz.value)
    return `Start frequency must be at least ${minFreqHz.value.toFixed(1)} Hz — below that the fundamental is too few bins up to separate from its own harmonics.`
  if (!form.processLeft && !form.processRight) return 'Select at least one channel.'
  if (form.levelMode !== 'GeneratorVoltage' && !(loadOhms.value > 0)) return 'An output-referred level needs a load above 0 Ω.'
  if (isPowerMode.value && !(form.levelValue > 0)) return 'Output power must be above 0 W.'
  return null
})

defineExpose({ run, invalid, buildParamsJson })

/**
 * The form as a `ThdVsFrequencyParameters`, for `measurement_type.params_json`. Every field the
 * record declares is written: the server's deserializer binds constructor parameters and treats a
 * missing one as an error, so a partial object would fail to load rather than fall back.
 *
 * `generatorAmpDbv` is 0 here by design — it is a *result* field, solved by probing during a run,
 * and a configured type has no value for it yet.
 */
function buildParamsJson(): string {
  const parameters: ThdVsFrequencyParameters = {
    startFreqHz: form.startFreqHz,
    endFreqHz: form.endFreqHz,
    stepsPerOctave: form.stepsPerOctave,
    levelMode: form.levelMode,
    levelValue: form.levelValue,
    levelUnit: isPowerMode.value ? 'Watt' : form.levelUnit,
    loadOhms: loadOhms.value,
    processLeft: form.processLeft,
    processRight: form.processRight,
    fftSize: SWEEP_FFT_SIZE,
    window: SWEEP_WINDOW,
    averageCount: form.averageCount,
    sampleRateHz: SWEEP_SAMPLE_RATE_HZ,
    measureNoiseFloor: form.measureNoiseFloor,
    adaptiveResolution: form.adaptiveResolution,
    generatorAmpDbv: 0,
  }
  return JSON.stringify(parameters)
}

function run() {
  if (invalid.value) return
  emit('run', {
    startFreqHz: form.startFreqHz,
    endFreqHz: form.endFreqHz,
    stepsPerOctave: form.stepsPerOctave,
    levelMode: form.levelMode,
    levelValue: form.levelValue,
    // Watts is the only unit that means anything for the power mode, and the only mode it means
    // anything for — the server rejects the other combinations rather than guessing.
    levelUnit: isPowerMode.value ? 'Watt' : form.levelUnit,
    loadOhms: loadOhms.value,
    processLeft: form.processLeft,
    processRight: form.processRight,
    fftSize: SWEEP_FFT_SIZE,
    window: SWEEP_WINDOW,
    averageCount: form.averageCount,
    // Sent as the fixed preset regardless; the server ignores both under adaptiveResolution and
    // derives them per point instead (ThdSweepRequest.EffectiveSampleRateHz / FftSizeAt).
    sampleRateHz: SWEEP_SAMPLE_RATE_HZ,
    measureNoiseFloor: form.measureNoiseFloor,
    adaptiveResolution: form.adaptiveResolution,
  })
}
</script>

<template>
  <section class="flex flex-col gap-3 rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <h2 class="text-sm font-semibold text-neutral-900 dark:text-neutral-100">Sweep</h2>

    <div class="grid grid-cols-2 gap-2 text-xs">
      <label class="flex flex-col gap-1" title="Lowest frequency measured. The sweep steps upward from here.">
        Start (Hz)
        <input
          v-model.number="form.startFreqHz"
          type="number"
          min="1"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
      <label class="flex flex-col gap-1" title="Highest frequency measured. Anything above 98% of Nyquist is dropped.">
        End (Hz)
        <input
          v-model.number="form.endFreqHz"
          type="number"
          min="2"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
    </div>

    <label
      class="flex flex-col gap-1 text-xs"
      title="How many measurement points per octave. More points resolve narrow features but take proportionally longer."
    >
      Steps per octave
      <input
        v-model.number="form.stepsPerOctave"
        type="number"
        min="1"
        max="48"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="Generator voltage holds the drive constant. The two output modes probe at 1 kHz first and solve for the generator level that reaches the target."
    >
      Level mode
      <select
        v-model="form.levelMode"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="mode in LEVEL_MODES" :key="mode.value" :value="mode.value">{{ mode.label }}</option>
      </select>
    </label>

    <div class="flex items-end gap-2 text-xs">
      <label class="flex flex-1 flex-col gap-1">
        {{ isPowerMode ? 'Power (W)' : 'Level' }}
        <input
          v-model.number="form.levelValue"
          type="number"
          step="any"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
      <label v-if="!isPowerMode" class="flex flex-col gap-1">
        Unit
        <select
          v-model="form.levelUnit"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        >
          <option v-for="unit in VOLTAGE_UNITS" :key="unit.value" :value="unit.value">{{ unit.label }}</option>
        </select>
      </label>
    </div>

    <!-- Shared with the FFT view's power readouts (useLoadOhms) rather than a second field that
         could disagree with it. -->
    <label
      v-if="form.levelMode !== 'GeneratorVoltage'"
      class="flex flex-col gap-1 text-xs"
      title="Amplifier load. Used to turn watts into a target voltage, and to report power per point."
    >
      Load (Ω)
      <input
        v-model.number="loadOhms"
        type="number"
        step="any"
        min="0.1"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>

    <div class="flex gap-3 text-xs">
      <label class="flex items-center gap-1.5">
        <input v-model="form.processLeft" type="checkbox" :disabled="running" />
        Left
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="form.processRight" type="checkbox" :disabled="running" />
        Right
      </label>
    </div>

    <div class="grid grid-cols-2 gap-2 text-xs">
      <label class="flex flex-col gap-1" title="Averages per point. More averages lower the noise floor of every reading.">
        Averages per point
        <input
          v-model.number="form.averageCount"
          type="number"
          min="1"
          max="64"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
    </div>

    <label
      class="flex items-center gap-1.5 text-xs"
      title="Sizes the FFT per point instead of holding one size across the whole sweep, and picks the sample rate from the top of the frequency range. A fixed size is chosen for the lowest point and then paid at every point above it, where the record is far longer than the measurement needs — so this mostly speeds up the top of a sweep. Turn it off for a fixed 192 kHz / 131072-point reference: slower, but the same resolution and noise floor at every frequency."
    >
      <input v-model="form.adaptiveResolution" type="checkbox" :disabled="running" />
      Adaptive resolution
    </label>

    <p
      v-if="form.adaptiveResolution && adaptiveFftRange"
      class="text-xs text-neutral-500 dark:text-neutral-400"
      title="The FFT size varies per point so each record holds a similar number of cycles of the tone. The largest size is used at the bottom of the sweep, the smallest at the top."
    >
      Analysis: {{ effectiveSampleRateHz / 1000 }} kHz ·
      {{ adaptiveFftRange.max }}–{{ adaptiveFftRange.min }} points ·
      {{ (effectiveSampleRateHz / adaptiveFftRange.max).toFixed(3) }}–{{
        (effectiveSampleRateHz / adaptiveFftRange.min).toFixed(2)
      }}
      Hz per bin
    </p>
    <p
      v-else
      class="text-xs text-neutral-500 dark:text-neutral-400"
      title="Fixed for a sweep: the bottom of the frequency range needs this resolution for the harmonic analysis to separate a harmonic from its own fundamental."
    >
      Analysis: {{ SWEEP_SAMPLE_RATE_HZ / 1000 }} kHz · {{ SWEEP_FFT_SIZE }} points ·
      {{ SWEEP_BIN_SIZE_HZ.toFixed(3) }} Hz per bin
    </p>

    <label
      class="flex items-center gap-1.5 text-xs"
      title="Captures one generator-off record before the sweep, so the chart can show the residual noise the distortion traces sit above."
    >
      <input v-model="form.measureNoiseFloor" type="checkbox" :disabled="running" />
      Measure noise floor first
    </label>

    <p class="text-xs text-neutral-500 dark:text-neutral-400">
      ≈{{ pointCount }} points · ≈{{ durationLabel }}
      <span v-if="truncated" class="block text-amber-600 dark:text-amber-500">
        Above {{ maxFreqHz }} Hz is beyond Nyquist — the sweep will stop there.
      </span>
    </p>

    <p v-if="invalid" class="text-xs text-red-600 dark:text-red-400">{{ invalid }}</p>

  </section>
</template>
