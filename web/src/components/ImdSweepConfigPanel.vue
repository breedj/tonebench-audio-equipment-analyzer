<script setup lang="ts">
import { computed, nextTick, reactive, watch } from 'vue'
import type { ImdMethodName, ImdSweepStartRequest, ImdVsAmplitudeParameters, WindowType } from '../api/client'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import { useProjects } from '../composables/useProjects'
import { IMD_DEFAULT_MAX_ORDER, IMD_PRESETS, imdPreset, type ImdPresetId } from '../imd/presets'
import CollapsibleSection from './CollapsibleSection.vue'
import { estimateDurationSeconds, formatDuration, logPerOctave } from '../sweep/points'

/**
 * Everything an IMD-vs-amplitude sweep needs, staged locally and only sent on Run — the THD
 * amplitude panel's model with the one tone replaced by a two-tone pair. A preset fixes the pair,
 * the ratio, the method and the order default; Custom frees them. The levels are sine-equivalent
 * volts: the single sine whose peak equals the pair's peak (see imd/presets.ts), which is what an
 * amplifier's rating is quoted against and why the same 7.9 V ceiling applies.
 */

defineProps<{
  running: boolean
}>()

const emit = defineEmits<{
  run: [request: ImdSweepStartRequest]
}>()

const { loadOhms } = useLoadOhms()
const { types, currentTypeId, markDirty } = useProjects()

/** Fixed as on the THD sweeps: 1.465 Hz bins put SMPTE's 60 Hz spacing 41 bins apart, far clear of the analyzer's 8-bin minimum. */
const SWEEP_SAMPLE_RATE_HZ = 192000
const SWEEP_FFT_SIZE = 131072
const SWEEP_BIN_SIZE_HZ = SWEEP_SAMPLE_RATE_HZ / SWEEP_FFT_SIZE
const SWEEP_WINDOW: WindowType = 'Hann'

/** IntermodulationAnalyzer.MinToneSpacingBins — closer than this and a product's skirt sits inside the carrier's own lobe. */
const MIN_TONE_SPACING_BINS = 8

/** ThdSweepRequest.MinAmplitudeVolt / MaxAmplitudeVolt, applied to the sine-equivalent level. */
const MIN_LEVEL_VOLT = 0.001
const MAX_LEVEL_VOLT = 7.9

const METHODS: { value: ImdMethodName; label: string }[] = [
  { value: 'Smpte', label: 'SMPTE / DIN (sidebands re carrier)' },
  { value: 'Ccif', label: 'CCIF / ITU-R (difference tones)' },
]

const form = reactive({
  preset: 'Smpte' as ImdPresetId,
  toneLowHz: 60,
  toneHighHz: 7000,
  lowToHighRatio: 4,
  method: 'Smpte' as ImdMethodName,
  maxOrder: 5,
  startLevelVolt: 0.001,
  endLevelVolt: 1,
  stepsPerOctave: 5,
  processLeft: true,
  processRight: false,
  averageCount: 4,
  measureNoiseFloor: true,
  maxProductFreqHz: 20000,
})

const presetActive = computed(() => form.preset !== 'Custom')

/** Picking a preset writes its pair, ratio, method and order default into the form; Custom leaves whatever is there. */
function onPresetChange() {
  if (form.preset === 'Custom') return
  const p = imdPreset(form.preset)
  form.toneLowHz = p.lowHz
  form.toneHighHz = p.highHz
  form.lowToHighRatio = p.ratio
  form.method = p.method
  form.maxOrder = p.defaultMaxOrder
}

function onMethodChange() {
  form.maxOrder = IMD_DEFAULT_MAX_ORDER[form.method]
}

const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)

let restoring = false

function applyStoredParams(paramsJson: string) {
  let stored: Partial<ImdVsAmplitudeParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<ImdVsAmplitudeParameters>
  } catch {
    return
  }

  restoring = true
  if (typeof stored.preset === 'string' && IMD_PRESETS.some((p) => p.value === stored.preset)) form.preset = stored.preset as ImdPresetId
  if (typeof stored.toneLowHz === 'number') form.toneLowHz = stored.toneLowHz
  if (typeof stored.toneHighHz === 'number') form.toneHighHz = stored.toneHighHz
  if (typeof stored.lowToHighRatio === 'number') form.lowToHighRatio = stored.lowToHighRatio
  if (stored.method === 'Smpte' || stored.method === 'Ccif') form.method = stored.method
  if (typeof stored.maxOrder === 'number') form.maxOrder = stored.maxOrder
  if (typeof stored.startLevelVolt === 'number') form.startLevelVolt = stored.startLevelVolt
  if (typeof stored.endLevelVolt === 'number') form.endLevelVolt = stored.endLevelVolt
  if (typeof stored.stepsPerOctave === 'number') form.stepsPerOctave = stored.stepsPerOctave
  if (typeof stored.loadOhms === 'number' && stored.loadOhms > 0) loadOhms.value = stored.loadOhms
  if (typeof stored.processLeft === 'boolean') form.processLeft = stored.processLeft
  if (typeof stored.processRight === 'boolean') form.processRight = stored.processRight
  if (typeof stored.averageCount === 'number') form.averageCount = stored.averageCount
  if (typeof stored.measureNoiseFloor === 'boolean') form.measureNoiseFloor = stored.measureNoiseFloor
  // Null in the record means "the measurement band" — 20 kHz on the pipeline's defaults.
  form.maxProductFreqHz = typeof stored.maxProductFreqHz === 'number' ? stored.maxProductFreqHz : 20000

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

watch(
  [form, loadOhms],
  () => {
    if (!restoring) markDirty()
  },
  { deep: true },
)

const gridVolt = computed(() =>
  form.startLevelVolt > 0 && form.endLevelVolt > form.startLevelVolt
    ? logPerOctave(form.startLevelVolt, form.endLevelVolt, Math.max(1, form.stepsPerOctave))
    : [],
)

const pointCount = computed(() => gridVolt.value.length)

const { captureDelayMs } = useCaptureDelay()
const perPointDiscardSeconds = computed(() => 0.065 + captureDelayMs.value / 1000)

const durationLabel = computed(() =>
  formatDuration(
    estimateDurationSeconds(
      pointCount.value, SWEEP_FFT_SIZE, SWEEP_SAMPLE_RATE_HZ, form.averageCount, 0.5, perPointDiscardSeconds.value,
    ),
  ),
)

const minSpacingHz = MIN_TONE_SPACING_BINS * SWEEP_BIN_SIZE_HZ

const invalid = computed(() => {
  if (!(form.toneLowHz > 0)) return 'The low tone must be a positive frequency.'
  if (!(form.toneHighHz > form.toneLowHz)) return 'The high tone must be above the low tone.'
  if (!(form.toneHighHz < 0.98 * (SWEEP_SAMPLE_RATE_HZ / 2))) return 'The high tone must be below Nyquist.'
  if (form.toneLowHz < minSpacingHz) return `The low tone must be at least ${minSpacingHz.toFixed(1)} Hz at this resolution.`
  if (form.toneHighHz - form.toneLowHz < minSpacingHz) return `The tones must be at least ${minSpacingHz.toFixed(1)} Hz apart.`
  if (!(form.lowToHighRatio > 0)) return 'The amplitude ratio must be positive.'
  if (!(form.maxOrder >= 2 && form.maxOrder <= 12)) return 'Max order must be between 2 and 12.'
  if (!(form.startLevelVolt >= MIN_LEVEL_VOLT)) return `Start level must be at least ${MIN_LEVEL_VOLT * 1000} mV.`
  if (!(form.endLevelVolt > form.startLevelVolt)) return 'End level must be above the start level.'
  if (form.endLevelVolt > MAX_LEVEL_VOLT) return `End level must be at most ${MAX_LEVEL_VOLT} V — the ceiling of the widest output range.`
  if (!form.processLeft && !form.processRight) return 'Select at least one channel.'
  if (!(loadOhms.value > 0)) return 'A load above 0 Ω is needed to report output power.'
  if (!(form.maxProductFreqHz > 0)) return 'The product band limit must be positive.'
  return null
})

defineExpose({ run, invalid, buildParamsJson })

/** The form as an `ImdVsAmplitudeParameters`, for `measurement_type.params_json` — every non-defaulted field written, as the server binds constructor parameters. */
function buildParamsJson(): string {
  const parameters: ImdVsAmplitudeParameters = {
    preset: form.preset,
    toneLowHz: form.toneLowHz,
    toneHighHz: form.toneHighHz,
    lowToHighRatio: form.lowToHighRatio,
    method: form.method,
    maxOrder: form.maxOrder,
    startLevelVolt: form.startLevelVolt,
    endLevelVolt: form.endLevelVolt,
    stepsPerOctave: form.stepsPerOctave,
    loadOhms: loadOhms.value,
    processLeft: form.processLeft,
    processRight: form.processRight,
    fftSize: SWEEP_FFT_SIZE,
    window: SWEEP_WINDOW,
    averageCount: form.averageCount,
    sampleRateHz: SWEEP_SAMPLE_RATE_HZ,
    measureNoiseFloor: form.measureNoiseFloor,
    maxProductFreqHz: form.maxProductFreqHz,
  }
  return JSON.stringify(parameters)
}

function run() {
  if (invalid.value) return
  emit('run', {
    preset: form.preset,
    toneLowHz: form.toneLowHz,
    toneHighHz: form.toneHighHz,
    lowToHighRatio: form.lowToHighRatio,
    method: form.method,
    maxOrder: form.maxOrder,
    startLevelVolt: form.startLevelVolt,
    endLevelVolt: form.endLevelVolt,
    stepsPerOctave: form.stepsPerOctave,
    loadOhms: loadOhms.value,
    processLeft: form.processLeft,
    processRight: form.processRight,
    averageCount: form.averageCount,
    measureNoiseFloor: form.measureNoiseFloor,
    maxProductFreqHz: form.maxProductFreqHz,
  })
}
</script>

<template>
  <CollapsibleSection title="Sweep">
    <label
      class="flex flex-col gap-1 text-xs"
      title="A standard two-tone test. SMPTE and DIN modulate a small HF carrier with a large LF tone (4:1); CCIF plays two equal tones a kilohertz apart at the top of the band. Custom frees the pair."
    >
      IMD preset
      <select
        v-model="form.preset"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        @change="onPresetChange"
      >
        <option v-for="p in IMD_PRESETS" :key="p.value" :value="p.value">{{ p.label }}</option>
      </select>
    </label>

    <div class="grid grid-cols-2 gap-2 text-xs">
      <label class="flex flex-col gap-1" title="The lower tone — SMPTE's modulating tone, CCIF's f1.">
        Tone 1 (Hz)
        <input
          v-model.number="form.toneLowHz"
          type="number"
          min="1"
          step="any"
          :disabled="running || presetActive"
          class="rounded border border-neutral-300 px-2 py-1 disabled:opacity-60 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
      <label class="flex flex-col gap-1" title="The upper tone — SMPTE's carrier, CCIF's f2.">
        Tone 2 (Hz)
        <input
          v-model.number="form.toneHighHz"
          type="number"
          min="1"
          step="any"
          :disabled="running || presetActive"
          class="rounded border border-neutral-300 px-2 py-1 disabled:opacity-60 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
    </div>

    <label
      class="flex flex-col gap-1 text-xs"
      title="Amplitude of tone 1 over tone 2. 4 (12 dB) for SMPTE/DIN, 1 for CCIF."
    >
      Ratio (low:high)
      <input
        v-model.number="form.lowToHighRatio"
        type="number"
        min="0.01"
        step="any"
        :disabled="running || presetActive"
        class="w-20 rounded border border-neutral-300 px-2 py-1 disabled:opacity-60 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="SMPTE/DIN: sidebands f2 ± n·f1, relative to the HF carrier. CCIF: difference products |m·f1 − n·f2|, relative to the RMS sum of both tones."
    >
      Method
      <select
        v-model="form.method"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        @change="onMethodChange"
      >
        <option v-for="m in METHODS" :key="m.value" :value="m.value">{{ m.label }}</option>
      </select>
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="Highest product order counted. SMPTE 5 = four sideband pairs; CCIF 3 = the 2nd-order difference tone plus both 3rd-order products."
    >
      Max order
      <input
        v-model.number="form.maxOrder"
        type="number"
        min="2"
        max="12"
        :disabled="running"
        class="w-16 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>

    <div class="grid grid-cols-2 gap-2 text-xs">
      <label
        class="flex flex-col gap-1"
        title="Lowest generator level, as the single sine whose peak equals the pair's peak. The sweep steps upward from here."
      >
        Start (V sine-eq.)
        <input
          v-model.number="form.startLevelVolt"
          type="number"
          :min="MIN_LEVEL_VOLT"
          :max="MAX_LEVEL_VOLT"
          step="any"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
      <label class="flex flex-col gap-1" title="Highest generator level. 7.9 V is the most the output stage can produce at the pair's peak.">
        End (V sine-eq.)
        <input
          v-model.number="form.endLevelVolt"
          type="number"
          :min="MIN_LEVEL_VOLT"
          :max="MAX_LEVEL_VOLT"
          step="any"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
    </div>

    <label
      class="flex flex-col gap-1 text-xs"
      title="How many measurement points per doubling of level."
    >
      Steps per octave
      <input
        v-model.number="form.stepsPerOctave"
        type="number"
        min="1"
        max="48"
        :disabled="running"
        class="w-16 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="Amplifier load. Used to report output power per point, and for the output-power x axis."
    >
      Load (Ω)
      <input
        v-model.number="loadOhms"
        type="number"
        step="any"
        min="0.1"
        :disabled="running"
        class="w-16 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
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

    <div class="text-xs">
      <label class="flex flex-col gap-1" title="Averages per point. More averages lower the noise floor of every reading.">
        Averages per point
        <input
          v-model.number="form.averageCount"
          type="number"
          min="1"
          max="64"
          :disabled="running"
          class="w-16 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
    </div>

    <label
      class="flex items-center gap-1.5 text-xs"
      title="Captures one generator-off record before the sweep, so the chart can show the residual the distortion traces sit above."
    >
      <input v-model="form.measureNoiseFloor" type="checkbox" :disabled="running" />
      Measure noise floor first
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="Highest product frequency counted — the tutorial's 'in-band products only'. At 20 kHz the CCIF 19 + 20 kHz test counts 18 kHz and omits 21 kHz; widen it to count the products above the band too."
    >
      Product band limit (Hz)
      <input
        v-model.number="form.maxProductFreqHz"
        type="number"
        min="1"
        step="any"
        :disabled="running"
        class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>

    <p
      class="text-xs text-neutral-500 dark:text-neutral-400"
      title="Fixed for a sweep: this resolution keeps the SMPTE tones dozens of bins apart."
    >
      Analysis: {{ SWEEP_SAMPLE_RATE_HZ / 1000 }} kHz · {{ SWEEP_FFT_SIZE }} points ·
      {{ SWEEP_BIN_SIZE_HZ.toFixed(3) }} Hz per bin
    </p>

    <p class="text-xs text-neutral-500 dark:text-neutral-400">≈{{ pointCount }} points · ≈{{ durationLabel }}</p>

    <p v-if="invalid" class="text-xs text-red-600 dark:text-red-400">{{ invalid }}</p>
  </CollapsibleSection>
</template>
