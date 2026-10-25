<script setup lang="ts">
import { clampNumberField } from '../numberField'
import { computed, nextTick, reactive, watch } from 'vue'
import type { ImdMethodName, ImdSweepStartRequest, ImdVsAmplitudeParameters, WindowType } from '../api/client'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import { useProjects } from '../composables/useProjects'
import { IMD_DEFAULT_MAX_ORDER, IMD_PRESETS, imdPreset, type ImdPresetId } from '../imd/presets'
import CollapsibleSection from './CollapsibleSection.vue'
import EstimateBadge from './EstimateBadge.vue'
import BandwidthField from './BandwidthField.vue'
import { DEFAULT_BANDWIDTH } from '../weighting'
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
  /**
   * The band this sweep's noise figures are read over: the **noise-floor trace**
   * (SweepCaptureSupport.MeanBandBinVrms), which is the only one of them that is drawn. A sweep's
   * own setting rather than inherited from whichever FFT measurement was open last (2026-10-14).
   *
   * Nearly separate from the product band limit beside it, and the exception is worth knowing:
   * a *null* product limit falls back to this band's upper edge
   * (SpectrumRecordAnalyzer: `: band.HiBinInclusive`). The panel always sends a figure, so in
   * practice the two are independent -- but they are not unrelated.
   */
  measurementLoFreqHz: DEFAULT_BANDWIDTH.loHz as number,
  measurementHiFreqHz: DEFAULT_BANDWIDTH.hiHz as number,
  /**
   * The early abort, the THD amplitude sweep's on the figure this sweep measures. Two fields for
   * one nullable setting, as that panel does it: unticking must not lose the threshold typed.
   */
  stopOnImd: false,
  stopImdPercent: 1,
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
  // Absent on a sweep saved before the band was one of its settings; those ran on whatever the
  // live pipeline held, which is unknowable now, so the default stands rather than a guess.
  if (typeof stored.measurementLoFreqHz === 'number') form.measurementLoFreqHz = stored.measurementLoFreqHz
  if (typeof stored.measurementHiFreqHz === 'number') form.measurementHiFreqHz = stored.measurementHiFreqHz
  // Stored as one nullable number; the form keeps the checkbox and the value apart so unticking
  // does not lose the threshold the user typed.
  if (typeof stored.stopImdPercent === 'number' && stored.stopImdPercent > 0) {
    form.stopOnImd = true
    form.stopImdPercent = stored.stopImdPercent
  } else if (stored.stopImdPercent === null) {
    form.stopOnImd = false
  }

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
  if (form.stopOnImd && !(form.stopImdPercent > 0)) return 'The IMD stop threshold must be above 0 %.'
  return null
})

/** The threshold as the server takes it: a number when armed, null for "sweep to the end". */
const stopImdPercent = computed(() => (form.stopOnImd ? form.stopImdPercent : null))

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
    stopImdPercent: stopImdPercent.value,
    measurementLoFreqHz: form.measurementLoFreqHz,
    measurementHiFreqHz: form.measurementHiFreqHz,
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
    stopImdPercent: stopImdPercent.value,
    measurementLoFreqHz: form.measurementLoFreqHz,
    measurementHiFreqHz: form.measurementHiFreqHz,
  })
}
</script>

<template>
  <CollapsibleSection title="Sweep">
    <label
      class="tb-field"
      title="A standard two-tone test. SMPTE and DIN modulate a small HF carrier with a large LF tone (4:1); CCIF plays two equal tones a kilohertz apart at the top of the band. Custom frees the pair."
    >
      IMD preset
      <select
        v-model="form.preset"
        :disabled="running"
        class="tb-input"
        @change="onPresetChange"
      >
        <option v-for="p in IMD_PRESETS" :key="p.value" :value="p.value">{{ p.label }}</option>
      </select>
    </label>

    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field" title="The lower tone — SMPTE's modulating tone, CCIF's f1.">
        Tone 1 (Hz)
        <input
          v-model.number="form.toneLowHz"
          type="number"
          min="1"
          step="any"
          :disabled="running || presetActive"
          class="rounded border border-neutral-300 px-2 py-1 disabled:opacity-60 dark:border-neutral-700"
        />
      </label>
      <label class="tb-field" title="The upper tone — SMPTE's carrier, CCIF's f2.">
        Tone 2 (Hz)
        <input
          v-model.number="form.toneHighHz"
          type="number"
          min="1"
          step="any"
          :disabled="running || presetActive"
          class="rounded border border-neutral-300 px-2 py-1 disabled:opacity-60 dark:border-neutral-700"
        />
      </label>
    </div>

    <label
      class="tb-field"
      title="Amplitude of tone 1 over tone 2. 4 (12 dB) for SMPTE/DIN, 1 for CCIF."
    >
      Ratio (low:high)
      <input
        v-model.number="form.lowToHighRatio"
        type="number"
        min="0.01"
        step="any"
        :disabled="running || presetActive"
        class="w-20 rounded border border-neutral-300 px-2 py-1 disabled:opacity-60 dark:border-neutral-700"
      />
    </label>

    <label
      class="tb-field"
      title="SMPTE/DIN: sidebands f2 ± n·f1, relative to the HF carrier. CCIF: difference products |m·f1 − n·f2|, relative to the RMS sum of both tones."
    >
      Method
      <select
        v-model="form.method"
        :disabled="running"
        class="tb-input"
        @change="onMethodChange"
      >
        <option v-for="m in METHODS" :key="m.value" :value="m.value">{{ m.label }}</option>
      </select>
    </label>

    <label
      class="tb-field"
      title="Highest product order counted. SMPTE 5 = four sideband pairs; CCIF 3 = the 2nd-order difference tone plus both 3rd-order products."
    >
      Max order
      <input
        v-model.number="form.maxOrder"
        type="number"
        min="2"
        max="12"
        :disabled="running"
        class="tb-input"
      />
    </label>

    <div class="grid grid-cols-2 gap-2.5">
      <label
        class="tb-field"
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
          class="tb-input"
        />
      </label>
      <label class="tb-field" title="Highest generator level. 7.9 V is the most the output stage can produce at the pair's peak.">
        End (V sine-eq.)
        <input
          v-model.number="form.endLevelVolt"
          type="number"
          :min="MIN_LEVEL_VOLT"
          :max="MAX_LEVEL_VOLT"
          step="any"
          :disabled="running"
          class="tb-input"
        />
      </label>
    </div>

    <div class="grid grid-cols-2 gap-2.5">
      <label
        class="tb-field"
        title="How many measurement points per doubling of level."
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

    <BandwidthField
      v-model:lo-hz="form.measurementLoFreqHz"
      v-model:hi-hz="form.measurementHiFreqHz"
      :disabled="running"
      hint="The band the noise-floor trace is measured over. It also becomes the product limit if the product band limit below is cleared. It does not bound the harmonics."
    />

    <label
      class="tb-check-row"
      title="Captures one generator-off record before the sweep, so the chart can show the residual the distortion traces sit above."
    >
      <input v-model="form.measureNoiseFloor" type="checkbox" :disabled="running" />
      Measure noise floor first
    </label>

    <label
      class="tb-field"
      title="Highest product frequency counted — the tutorial's 'in-band products only'. At 20 kHz the CCIF 19 + 20 kHz test counts 18 kHz and omits 21 kHz; widen it to count the products above the band too."
    >
      Product band limit (Hz)
      <input
        v-model.number="form.maxProductFreqHz"
        type="number"
        min="1"
        step="any"
        :disabled="running"
        class="tb-input w-28"
      />
    </label>

    <label
      class="tb-check-row flex-wrap"
      title="Ends the sweep as soon as a measured channel's IMD exceeds this figure. The point that crossed it is kept — it is the one that says where the DUT gave up — so the DUT is driven one step past the threshold. Off: the sweep runs to its end level."
    >
      <input v-model="form.stopOnImd" type="checkbox" :disabled="running" />
      <span class="whitespace-nowrap">Stop when IMD exceeds</span>
      <!-- The box gets a width of its own and the row wraps as two whole things -- see
           AmplitudeSweepConfigPanel, whose own label broke over three lines in a 226 px rail. -->
      <span class="flex items-center gap-2">
        <input
          v-model.number="form.stopImdPercent"
          type="number"
          step="0.1"
          :disabled="running || !form.stopOnImd"
          @change="form.stopImdPercent = clampNumberField(form.stopImdPercent, 0.001)"
          class="tb-input w-18 shrink-0"
        />
        %
      </span>
    </label>



    <p v-if="invalid" class="text-xs text-red-600 dark:text-red-400">{{ invalid }}</p>

    <template #footer>
      <p
        class="leading-snug"
        title="Fixed for a sweep: this resolution keeps the SMPTE tones dozens of bins apart."
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
