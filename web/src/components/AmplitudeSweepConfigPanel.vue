<script setup lang="ts">
import { computed, nextTick, reactive, watch } from 'vue'
import type { ThdAmplitudeSweepStartRequest, ThdVsAmplitudeParameters, WindowType } from '../api/client'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import { useProjects } from '../composables/useProjects'
import CollapsibleSection from './CollapsibleSection.vue'
import EstimateBadge from './EstimateBadge.vue'
import { estimateDurationSeconds, formatDuration, logPerOctave } from '../sweep/points'

/**
 * Everything a THD-vs-amplitude sweep needs, staged locally and only sent on Run — the same
 * staged-not-live model SweepConfigPanel and the Acquisition fields use, because none of these can
 * be changed mid-sweep anyway.
 *
 * The frequency-sweep panel's counterpart, minus the two things that only mean something when the
 * tone moves: there is no level mode (the level *is* the swept variable) and no adaptive resolution
 * (one frequency has nothing to adapt to). What it gains is the fixed frequency and the start/end
 * voltage pair. The server rejects both dropped settings outright rather than ignoring them — see
 * ThdSweepRequest.ValidateAmplitudeSweep. The noise-floor pass is offered on both axes.
 */

defineProps<{
  running: boolean
}>()

const emit = defineEmits<{
  run: [request: ThdAmplitudeSweepStartRequest]
}>()

const { loadOhms } = useLoadOhms()
const { types, currentTypeId, markDirty } = useProjects()

/**
 * Fixed, exactly as on the frequency screen and for the same reason: the harmonic analysis needs the
 * fundamental several bins up, and 192 kHz / 131072 gives 1.465 Hz bins and a 0.68 s record. The
 * server enforces the same floor in ThdSweepRequest.Validate; this just stops anyone walking into
 * it. Hann because the tone is snapped to a coherent bin and the harmonic analysis assumes that
 * mainlobe shape.
 */
const SWEEP_SAMPLE_RATE_HZ = 192000
const SWEEP_FFT_SIZE = 131072
const SWEEP_BIN_SIZE_HZ = SWEEP_SAMPLE_RATE_HZ / SWEEP_FFT_SIZE
const SWEEP_WINDOW: WindowType = 'Hann'

/** Bins the fundamental must be up before its harmonics can be measured — ThdSweepRequest.MinFundamentalBins. */
const MIN_FUNDAMENTAL_BINS = 5

/** ThdSweepRequest.MinAmplitudeVolt / MaxAmplitudeVolt. 7.9 V is 17.96 dBV, the ceiling of the widest output range. */
const MIN_AMPLITUDE_VOLT = 0.001
const MAX_AMPLITUDE_VOLT = 7.9

const form = reactive({
  fixedFreqHz: 1000,
  startAmplitudeVolt: 0.001,
  endAmplitudeVolt: 1,
  stepsPerOctave: 5,
  processLeft: true,
  processRight: false,
  averageCount: 4,
  measureNoiseFloor: true,
  stopOnThd: false,
  stopThdPercent: 1,
})

/**
 * A sweep type's settings live here and nowhere else — unlike the FFT screen, none of these are
 * server state the host could snapshot on its own. So this panel both reads and writes
 * `measurement_type.params_json` for the ThdVsAmplitude kind: it loads the stored settings when a
 * type is selected, flags the type dirty on any edit, and hands the JSON back to App.vue on save.
 */
const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)

// Suppresses the dirty watcher while applyStoredParams writes the form — loading a type's saved
// settings is the opposite of an unsaved edit.
let restoring = false

function applyStoredParams(paramsJson: string) {
  let stored: Partial<ThdVsAmplitudeParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<ThdVsAmplitudeParameters>
  } catch {
    return
  }

  // Guarded field by field rather than Object.assign'd: another kind's params_json parses fine and
  // would otherwise overwrite the form with undefined.
  restoring = true
  if (typeof stored.fixedFreqHz === 'number') form.fixedFreqHz = stored.fixedFreqHz
  if (typeof stored.startAmplitudeVolt === 'number') form.startAmplitudeVolt = stored.startAmplitudeVolt
  if (typeof stored.endAmplitudeVolt === 'number') form.endAmplitudeVolt = stored.endAmplitudeVolt
  if (typeof stored.stepsPerOctave === 'number') form.stepsPerOctave = stored.stepsPerOctave
  if (typeof stored.loadOhms === 'number' && stored.loadOhms > 0) loadOhms.value = stored.loadOhms
  if (typeof stored.processLeft === 'boolean') form.processLeft = stored.processLeft
  if (typeof stored.processRight === 'boolean') form.processRight = stored.processRight
  if (typeof stored.averageCount === 'number') form.averageCount = stored.averageCount
  if (typeof stored.measureNoiseFloor === 'boolean') form.measureNoiseFloor = stored.measureNoiseFloor
  // Stored as one nullable number; the form keeps the checkbox and the value apart so unticking
  // does not lose the threshold the user typed.
  if (typeof stored.stopThdPercent === 'number' && stored.stopThdPercent > 0) {
    form.stopOnThd = true
    form.stopThdPercent = stored.stopThdPercent
  } else if (stored.stopThdPercent === null) {
    form.stopOnThd = false
  }

  // The form writes land in this tick; the watcher runs on the next one, so the flag has to survive
  // until then.
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

// Deep, rather than a @change on each input: every field here is part of the stored configuration,
// so "something in the form moved" is exactly the condition.
watch(
  [form, loadOhms],
  () => {
    if (!restoring) markDirty()
  },
  { deep: true },
)

/** The generator levels the sweep will step, in volts — the same log grid the server builds. */
const gridVolt = computed(() =>
  form.startAmplitudeVolt > 0 && form.endAmplitudeVolt > form.startAmplitudeVolt
    ? logPerOctave(form.startAmplitudeVolt, form.endAmplitudeVolt, Math.max(1, form.stepsPerOctave))
    : [],
)

const pointCount = computed(() => gridVolt.value.length)

// Discarded at the front of every point on top of the ~65 ms stimulus latency -- see
// useCaptureDelay -- so it belongs in the estimate the same way the latency does.
const { captureDelayMs } = useCaptureDelay()
const perPointDiscardSeconds = computed(() => 0.065 + captureDelayMs.value / 1000)

const durationLabel = computed(() =>
  formatDuration(
    estimateDurationSeconds(
      pointCount.value, SWEEP_FFT_SIZE, SWEEP_SAMPLE_RATE_HZ, form.averageCount, 0.5, perPointDiscardSeconds.value,
    ),
  ),
)

/** The lowest tone this resolution can carry a harmonic analysis at — matches the server's rejection. */
const minFreqHz = computed(() => MIN_FUNDAMENTAL_BINS * SWEEP_BIN_SIZE_HZ)

const invalid = computed(() => {
  if (!(form.fixedFreqHz >= minFreqHz.value))
    return `Frequency must be at least ${minFreqHz.value.toFixed(1)} Hz — below that the fundamental is too few bins up to separate from its own harmonics.`
  if (!(form.fixedFreqHz < SWEEP_SAMPLE_RATE_HZ / 2)) return 'Frequency must be below Nyquist.'
  if (!(form.startAmplitudeVolt >= MIN_AMPLITUDE_VOLT))
    return `Start voltage must be at least ${MIN_AMPLITUDE_VOLT * 1000} mV.`
  if (!(form.endAmplitudeVolt > form.startAmplitudeVolt)) return 'End voltage must be above the start voltage.'
  if (form.endAmplitudeVolt > MAX_AMPLITUDE_VOLT)
    return `End voltage must be at most ${MAX_AMPLITUDE_VOLT} V — the ceiling of the widest output range.`
  if (!form.processLeft && !form.processRight) return 'Select at least one channel.'
  // Unconditional here, unlike the frequency sweep: the chart can plot output power against the
  // swept level, and that needs a load whichever way the sweep was set up.
  if (!(loadOhms.value > 0)) return 'A load above 0 Ω is needed to report output power.'
  if (form.stopOnThd && !(form.stopThdPercent > 0)) return 'The THD stop threshold must be above 0 %.'
  return null
})

/** The threshold as the server takes it: a number when armed, null for "sweep to the end". */
const stopThdPercent = computed(() => (form.stopOnThd ? form.stopThdPercent : null))

defineExpose({ run, invalid, buildParamsJson })

/**
 * The form as a `ThdVsAmplitudeParameters`, for `measurement_type.params_json`. Every field the
 * record declares without a default is written: the server's deserializer binds constructor
 * parameters and treats a missing one as an error, so a partial object would fail to load rather
 * than fall back.
 */
function buildParamsJson(): string {
  const parameters: ThdVsAmplitudeParameters = {
    fixedFreqHz: form.fixedFreqHz,
    startAmplitudeVolt: form.startAmplitudeVolt,
    endAmplitudeVolt: form.endAmplitudeVolt,
    stepsPerOctave: form.stepsPerOctave,
    loadOhms: loadOhms.value,
    processLeft: form.processLeft,
    processRight: form.processRight,
    fftSize: SWEEP_FFT_SIZE,
    window: SWEEP_WINDOW,
    averageCount: form.averageCount,
    sampleRateHz: SWEEP_SAMPLE_RATE_HZ,
    measureNoiseFloor: form.measureNoiseFloor,
    stopThdPercent: stopThdPercent.value,
  }
  return JSON.stringify(parameters)
}

function run() {
  if (invalid.value) return
  emit('run', {
    fixedFreqHz: form.fixedFreqHz,
    startAmplitudeVolt: form.startAmplitudeVolt,
    endAmplitudeVolt: form.endAmplitudeVolt,
    stepsPerOctave: form.stepsPerOctave,
    loadOhms: loadOhms.value,
    processLeft: form.processLeft,
    processRight: form.processRight,
    averageCount: form.averageCount,
    measureNoiseFloor: form.measureNoiseFloor,
    stopThdPercent: stopThdPercent.value,
  })
}
</script>

<template>
  <CollapsibleSection title="Sweep">
    <label
      class="tb-field"
      title="The one frequency every point is measured at. The generator level is what steps."
    >
      Frequency (Hz)
      <input
        v-model.number="form.fixedFreqHz"
        type="number"
        min="1"
        step="any"
        :disabled="running"
        class="tb-input"
      />
    </label>

    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field" title="Lowest generator voltage measured. The sweep steps upward from here.">
        Start (V)
        <input
          v-model.number="form.startAmplitudeVolt"
          type="number"
          :min="MIN_AMPLITUDE_VOLT"
          :max="MAX_AMPLITUDE_VOLT"
          step="any"
          :disabled="running"
          class="tb-input"
        />
      </label>
      <label class="tb-field" title="Highest generator voltage measured. 7.9 V is the most the output stage can produce.">
        End (V)
        <input
          v-model.number="form.endAmplitudeVolt"
          type="number"
          :min="MIN_AMPLITUDE_VOLT"
          :max="MAX_AMPLITUDE_VOLT"
          step="any"
          :disabled="running"
          class="tb-input"
        />
      </label>
    </div>

    <div class="grid grid-cols-2 gap-2.5">
      <label
        class="tb-field"
        title="How many measurement points per doubling of voltage. More points resolve the clipping knee more finely but take proportionally longer."
      >
        Steps per octave
        <input
          v-model.number="form.stepsPerOctave"
          type="number"
          min="1"
          max="48"
          :disabled="running"
          class="tb-input"
        />
      </label>
      <label class="tb-field" title="Averages per point. More averages lower the noise floor of every reading.">
        Averages per point
        <input
          v-model.number="form.averageCount"
          type="number"
          min="1"
          max="64"
          :disabled="running"
          class="tb-input"
        />
      </label>
    </div>

    <!-- Shared with the FFT view's power readouts (useLoadOhms) rather than a second field that
         could disagree with it. Always shown here: the chart's output-power x axis needs it. -->
    <label
      class="tb-field"
      title="Amplifier load. Used to report output power per point, and for the output-power x axis."
    >
      Load (Ω)
      <input
        v-model.number="loadOhms"
        type="number"
        step="any"
        min="0.1"
        :disabled="running"
        class="tb-input"
      />
    </label>

    <div class="flex gap-5">
      <label class="tb-check-row">
        <input v-model="form.processLeft" type="checkbox" :disabled="running" />
        Left
      </label>
      <label class="tb-check-row">
        <input v-model="form.processRight" type="checkbox" :disabled="running" />
        Right
      </label>
    </div>

    <div class="text-xs">
    </div>

    <label
      class="tb-check-row"
      title="Captures one generator-off record before the sweep, so the chart can show the residual the distortion traces sit above. Taken once, at the input range autoranging settles on with no signal — if the range steps up during the sweep, the analyzer's own floor rises with it and this trace will read low at the top end."
    >
      <input v-model="form.measureNoiseFloor" type="checkbox" :disabled="running" />
      Measure noise floor first
    </label>

    <label
      class="tb-check-row"
      title="Ends the sweep as soon as a measured channel's THD exceeds this figure. The point that crossed it is kept — it is the one that says where the DUT gave up — so the DUT is driven one step past the threshold. Off: the sweep runs to its end voltage."
    >
      <input v-model="form.stopOnThd" type="checkbox" :disabled="running" />
      Stop when THD exceeds
      <input
        v-model.number="form.stopThdPercent"
        type="number"
        min="0.001"
        step="0.1"
        :disabled="running || !form.stopOnThd"
        class="tb-input"
      />
      %
    </label>



    <p v-if="invalid" class="text-xs text-red-600 dark:text-red-400">{{ invalid }}</p>

    <template #footer>
      <p
        class="leading-snug"
        title="Fixed for a sweep: the harmonic analysis needs the fundamental several bins up, and this resolution keeps that true down to a few tens of hertz."
      >
        Analysis: {{ SWEEP_SAMPLE_RATE_HZ / 1000 }} kHz · {{ SWEEP_FFT_SIZE }} points ·
        {{ SWEEP_BIN_SIZE_HZ.toFixed(3) }} Hz per bin
      </p>
      <div class="flex flex-wrap items-center gap-2">
        <EstimateBadge kind="points" title="How many levels the sweep will measure.">≈ {{ pointCount }} points</EstimateBadge>
        <EstimateBadge kind="time" title="Roughly how long the whole sweep will take.">≈ {{ durationLabel }}</EstimateBadge>
      </div>
    </template>
  </CollapsibleSection>
</template>
