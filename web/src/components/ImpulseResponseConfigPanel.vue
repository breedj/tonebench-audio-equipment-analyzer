<script setup lang="ts">
import { computed, nextTick, reactive, watch } from 'vue'
import {
  IMPULSE_GATE_WINDOWS,
  IMPULSE_REFERENCE_MODES,
  RESPONSE_RESOLUTIONS,
  RESPONSE_SMOOTHING,
  RESPONSE_SPANS_HZ,
  type ImpulseReferenceModeName,
  type ImpulseResponseParameters,
  type ImpulseResponseStartRequest,
  type SweepLevelMode,
  type SweepLevelUnit,
  type WindowType,
} from '../api/client'
import { useImpulseResponse } from '../composables/useImpulseResponse'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useProjects } from '../composables/useProjects'
import CollapsibleSection from './CollapsibleSection.vue'

defineProps<{ running: boolean }>()
const emit = defineEmits<{ (e: 'capture-reference'): void }>()

const { types, currentTypeId, markDirty } = useProjects()
const { references } = useImpulseResponse()

// Shared with the FFT view's power readouts and the other config panels rather than a second field
// that could disagree with them.
const { loadOhms } = useLoadOhms()

const LEVEL_MODES: { value: SweepLevelMode; label: string }[] = [
  { value: 'GeneratorVoltage', label: 'Input voltage' },
  { value: 'OutputVoltage', label: 'Output voltage' },
  { value: 'OutputPower', label: 'Output power' },
]

const VOLTAGE_UNITS: { value: SweepLevelUnit; label: string }[] = [
  { value: 'MilliVolt', label: 'mV' },
  { value: 'Volt', label: 'V' },
  { value: 'Dbv', label: 'dBV' },
]

/** Kept in step with `MeasurementSaveService.DefaultImpulseResponseParametersJson`. */
const form = reactive({
  frequencySpanHz: 96000 as number,
  resolutionMultiplier: 1,
  levelMode: 'OutputVoltage' as SweepLevelMode,
  levelValue: 0,
  levelUnit: 'Dbv' as SweepLevelUnit,
  smoothingDenominator: 24,
  averageCount: 1,
  referenceMode: 'None' as ImpulseReferenceModeName,
  referenceCalibrationId: null as number | null,
  gatePreMs: 1,
  gatePostMs: 20,
  gateWindow: 'Hann' as WindowType,
  extractHarmonics: true,
  maxHarmonicOrder: 5,
  chirpStartHz: 2,
})

const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)

// Suppresses the dirty watcher while applyStoredParams writes the form — loading a type's saved
// settings is the opposite of an unsaved edit.
let restoring = false

function applyStoredParams(paramsJson: string) {
  let stored: Partial<ImpulseResponseParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<ImpulseResponseParameters>
  } catch {
    return
  }

  // Guarded field by field rather than Object.assign'd: another kind's params_json parses fine and
  // would otherwise overwrite the form with undefined.
  restoring = true
  if (typeof stored.frequencySpanHz === 'number') form.frequencySpanHz = stored.frequencySpanHz
  if (typeof stored.resolutionMultiplier === 'number') form.resolutionMultiplier = stored.resolutionMultiplier
  if (stored.levelMode) form.levelMode = stored.levelMode
  if (typeof stored.levelValue === 'number') form.levelValue = stored.levelValue
  // Watts is never restored into this field, which holds the voltage unit and nothing else — see
  // FrequencyResponseConfigPanel for why restoring it leaves a hidden control holding a unit the
  // server rejects.
  if (stored.levelUnit && stored.levelUnit !== 'Watt') form.levelUnit = stored.levelUnit
  if (typeof stored.smoothingDenominator === 'number') form.smoothingDenominator = stored.smoothingDenominator
  if (typeof stored.averageCount === 'number') form.averageCount = stored.averageCount
  if (stored.referenceMode) form.referenceMode = stored.referenceMode
  form.referenceCalibrationId = typeof stored.referenceCalibrationId === 'number' ? stored.referenceCalibrationId : null
  if (typeof stored.gatePreMs === 'number') form.gatePreMs = stored.gatePreMs
  if (typeof stored.gatePostMs === 'number') form.gatePostMs = stored.gatePostMs
  if (stored.gateWindow) form.gateWindow = stored.gateWindow
  if (typeof stored.extractHarmonics === 'boolean') form.extractHarmonics = stored.extractHarmonics
  if (typeof stored.maxHarmonicOrder === 'number') form.maxHarmonicOrder = stored.maxHarmonicOrder
  if (typeof stored.chirpStartHz === 'number') form.chirpStartHz = stored.chirpStartHz
  if (typeof stored.loadOhms === 'number' && stored.loadOhms > 0) loadOhms.value = stored.loadOhms

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

const isPowerMode = computed(() => form.levelMode === 'OutputPower')
const solvesLevel = computed(() => form.levelMode !== 'GeneratorVoltage')
const usesStoredLoopback = computed(() => form.referenceMode === 'StoredLoopback')

const sampleRateHz = computed(() => form.frequencySpanHz * 2)

/** Mirrors `ImpulseResponseRequest.FftSize`, which delegates to the frequency response's. */
const fftSize = computed(() => Math.round(sampleRateHz.value / (48000 / 32768)) * form.resolutionMultiplier)

const binHz = computed(() => sampleRateHz.value / fftSize.value)

const recordSeconds = computed(() => fftSize.value / sampleRateHz.value)

const chirpEndHz = computed(() => 0.98 * sampleRateHz.value * 0.5)

/**
 * The highest output frequency a harmonic can be read at, which is <b>not</b> simply the sweep's
 * end: above about this fraction of the sample rate what a curve would report is the analyzer's own
 * anti-alias roll-off rather than the DUT. Mirrors
 * `HarmonicPacketExtractor.AntiAliasNyquistFraction`, and it has to — a panel that promised H5 up to
 * the sweep's top over five would name a frequency the chart then refuses to draw to.
 */
const harmonicCeilingHz = computed(() => Math.min(chirpEndHz.value, 0.45 * sampleRateHz.value))

/**
 * The widest gate the sweep geometry allows — half way back to the second-harmonic packet, and
 * forward to half the period. Mirrors `ImpulseWindow.Bounds`, which is derived from
 * `ImpulseGate.ForSweep` so the two cannot drift.
 *
 * This is the *other* gate: it exists to delete the DUT's harmonic distortion packets, and the gate
 * below is always a subset of it. Exceeding it would keep the distortion the sweep gate is there to
 * remove, which is why the server refuses rather than clamping.
 */
const maxGatePreMs = computed(() => {
  const secondHarmonicSamples = (fftSize.value * Math.LN2) / Math.log(chirpEndHz.value / form.chirpStartHz)
  return (Math.max(1, Math.floor(secondHarmonicSamples / 2)) * 1000) / sampleRateHz.value
})

const maxGatePostMs = computed(() => ((fftSize.value / 2) * 1000) / sampleRateHz.value)

/**
 * What the gate's length means for the gated magnitude. The single most important number on this
 * panel: a 5 ms gate says nothing below 200 Hz, however convincing the curve looks down there.
 */
const gateResolutionHz = computed(() => 1000 / (form.gatePreMs + form.gatePostMs))

/** How far a reflection can be from the impulse and still fall inside the gate. */
const gateReachMetres = computed(() => (form.gatePostMs / 1000) * 343)

const estimatedSeconds = computed(() => {
  const perRecord = recordSeconds.value + 0.15
  const probe = solvesLevel.value ? 2 * (16384 / sampleRateHz.value + 0.15) : 0
  return (form.averageCount + 1) * perRecord + probe
})

const targetVolt = computed(() => Math.sqrt(Math.max(form.levelValue, 0) * Math.max(loadOhms.value, 0)))
const targetDbv = computed(() => (targetVolt.value > 0 ? 20 * Math.log10(targetVolt.value) : Number.NaN))

const selectedReference = computed(() =>
  references.value.find((r) => r.id === form.referenceCalibrationId) ?? null,
)

/** A calibration describes filters at one sample rate; using it at another is refused rather than resampled. */
const referenceRateMismatch = computed(
  () => selectedReference.value !== null && Math.abs(selectedReference.value.sampleRateHz - sampleRateHz.value) > 0.5,
)

const invalid = computed<string | null>(() => {
  if (!Number.isFinite(form.levelValue)) return 'Amplitude must be a number.'
  if (isPowerMode.value && !(form.levelValue > 0)) return 'Output power must be above 0 W.'
  if (isPowerMode.value && !(loadOhms.value > 0)) return 'A target power needs a load above 0 Ω.'
  if (!isPowerMode.value && form.levelUnit !== 'Dbv' && form.levelValue <= 0)
    return 'A voltage amplitude must be positive.'
  if (form.averageCount < 1 || form.averageCount > 10) return 'Averages must be between 1 and 10.'
  if (!Number.isFinite(form.chirpStartHz) || form.chirpStartHz < 0.5 || form.chirpStartHz > 1000)
    return 'Chirp start must be between 0.5 and 1000 Hz.'
  if (form.chirpStartHz >= chirpEndHz.value / 10) return 'Chirp start must be at least a decade below the span.'
  if (!(form.gatePreMs > 0)) return 'The gate must keep something before the impulse.'
  if (!(form.gatePostMs > 0)) return 'The gate must keep something after the impulse.'
  if (form.gatePreMs > maxGatePreMs.value)
    return `At this resolution the gate can keep at most ${maxGatePreMs.value.toFixed(2)} ms before the impulse.`
  if (form.gatePostMs > maxGatePostMs.value)
    return `At this resolution the gate can keep at most ${maxGatePostMs.value.toFixed(0)} ms after the impulse.`
  if (usesStoredLoopback.value && form.referenceCalibrationId === null)
    return 'Pick a loopback calibration, or capture one.'
  if (referenceRateMismatch.value)
    return `That calibration was taken at ${(selectedReference.value!.sampleRateHz / 1000).toFixed(0)} kHz and cannot be used at ${(sampleRateHz.value / 1000).toFixed(0)} kHz.`
  return null
})

/**
 * The form as an `ImpulseResponseParameters`, for `measurement_type.params_json`. Every field the
 * record declares is written: the server's deserializer binds constructor parameters and treats a
 * missing one as an error, so a partial object fails to load rather than falling back.
 */
function buildParamsJson(): string {
  const parameters: ImpulseResponseParameters = {
    frequencySpanHz: form.frequencySpanHz,
    resolutionMultiplier: form.resolutionMultiplier,
    levelMode: form.levelMode,
    levelValue: form.levelValue,
    levelUnit: isPowerMode.value ? 'Watt' : form.levelUnit,
    smoothingDenominator: form.smoothingDenominator,
    averageCount: form.averageCount,
    referenceMode: form.referenceMode,
    referenceCalibrationId: usesStoredLoopback.value ? form.referenceCalibrationId : null,
    gatePreMs: form.gatePreMs,
    gatePostMs: form.gatePostMs,
    gateWindow: form.gateWindow,
    extractHarmonics: form.extractHarmonics,
    maxHarmonicOrder: form.maxHarmonicOrder,
    chirpStartHz: form.chirpStartHz,
    loadOhms: loadOhms.value,
    generatorAmpDbv: null,
  }
  return JSON.stringify(parameters)
}

function buildRequest(continuous: boolean): ImpulseResponseStartRequest | null {
  if (invalid.value) return null

  return {
    frequencySpanHz: form.frequencySpanHz,
    resolutionMultiplier: form.resolutionMultiplier,
    levelMode: form.levelMode,
    levelValue: form.levelValue,
    levelUnit: isPowerMode.value ? 'Watt' : form.levelUnit,
    smoothingDenominator: form.smoothingDenominator,
    averageCount: form.averageCount,
    referenceMode: form.referenceMode,
    referenceCalibrationId: usesStoredLoopback.value ? form.referenceCalibrationId : null,
    gatePreMs: form.gatePreMs,
    gatePostMs: form.gatePostMs,
    gateWindow: form.gateWindow,
    extractHarmonics: form.extractHarmonics,
    maxHarmonicOrder: form.maxHarmonicOrder,
    chirpStartHz: form.chirpStartHz,
    loadOhms: loadOhms.value,
    continuous,
  }
}

/** What the capture dialog should default to, so a calibration matches the run it will correct. */
function referenceCaptureDefaults() {
  return {
    frequencySpanHz: form.frequencySpanHz,
    resolutionMultiplier: form.resolutionMultiplier,
    chirpStartHz: form.chirpStartHz,
  }
}

/**
 * Selects a calibration by id and switches the mode to use it. Called after one is captured: going
 * to the trouble of patching the cables and capturing is the whole statement of intent, and leaving
 * the picker on 'none' afterwards would make the next run silently ignore it.
 */
function selectReference(id: number) {
  form.referenceMode = 'StoredLoopback'
  form.referenceCalibrationId = id
}

defineExpose({ buildRequest, invalid, buildParamsJson, referenceCaptureDefaults, selectReference })
</script>

<template>
  <CollapsibleSection title="Measurement settings">
    <label
      class="flex flex-col gap-1 text-xs"
      title="Input voltage drives the generator at the level you enter. Output voltage and output power probe the DUT first and set the generator so this much comes back out."
    >
      Set generator by
      <select
        v-model="form.levelMode"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      >
        <option v-for="mode in LEVEL_MODES" :key="mode.value" :value="mode.value">{{ mode.label }}</option>
      </select>
    </label>

    <div class="grid grid-cols-[1fr_auto] gap-2 text-xs">
      <label class="flex flex-col gap-1">
        {{ isPowerMode ? 'Power (W)' : 'Amplitude' }}
        <input
          v-model.number="form.levelValue"
          type="number"
          step="any"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        />
      </label>
      <label v-if="!isPowerMode" class="flex flex-col gap-1">
        <span class="invisible">Unit</span>
        <select
          v-model="form.levelUnit"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        >
          <option v-for="unit in VOLTAGE_UNITS" :key="unit.value" :value="unit.value">{{ unit.label }}</option>
        </select>
      </label>
    </div>

    <label v-if="isPowerMode" class="flex flex-col gap-1 text-xs" title="Amplifier load: V = sqrt(P x R).">
      Load (Ω)
      <input
        v-model.number="loadOhms"
        type="number"
        step="any"
        min="0.1"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      />
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="The highest frequency measured. The sample rate is twice this, so a wider span costs no extra time — only resolution does."
    >
      Frequency span (Hz)
      <select
        v-model.number="form.frequencySpanHz"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      >
        <option v-for="span in RESPONSE_SPANS_HZ" :key="span" :value="span">{{ span }}</option>
      </select>
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="Sweep length, and so how far apart the harmonic distortion packets land in the impulse response. A longer sweep allows a wider gate."
    >
      Resolution (Hz)
      <select
        v-model.number="form.resolutionMultiplier"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      >
        <option v-for="option in RESPONSE_RESOLUTIONS" :key="option.multiplier" :value="option.multiplier">
          {{ option.label }}
        </option>
      </select>
    </label>

    <label class="flex flex-col gap-1 text-xs" title="Where the sweep starts. The reported band starts 25% above this value.">
      Chirp start (Hz)
      <input
        v-model.number="form.chirpStartHz"
        type="number"
        min="0.5"
        max="1000"
        step="0.5"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      />
    </label>

    <label class="flex flex-col gap-1 text-xs" title="How many sweeps to average. Coherence is only meaningful from two upward.">
      Averages
      <input
        v-model.number="form.averageCount"
        type="number"
        min="1"
        max="10"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      />
    </label>

    <p class="text-[11px] text-neutral-500 dark:text-neutral-400">
      Analysis: {{ (sampleRateHz / 1000).toFixed(0) }} kHz · {{ fftSize }} points ·
      {{ binHz.toFixed(2) }} Hz per bin · {{ recordSeconds.toFixed(2) }} s per record
    </p>
    <p v-if="isPowerMode && targetVolt > 0" class="text-[11px] text-neutral-500 dark:text-neutral-400">
      Target: {{ targetVolt.toFixed(3) }} V rms into {{ loadOhms }} Ω ({{ targetDbv.toFixed(2) }} dBV)
    </p>
    <p class="text-[11px] text-neutral-500 dark:text-neutral-400">≈{{ estimatedSeconds.toFixed(1) }} s</p>
  </CollapsibleSection>

  <CollapsibleSection title="Gate">
    <p class="text-[11px] text-neutral-500 dark:text-neutral-400">
      How much of the impulse response to keep either side of the peak. Everything outside it —
      room reflections, a loudspeaker's surroundings — is cut, and with it every frequency the
      remaining window is too short to resolve.
    </p>

    <div class="grid grid-cols-2 gap-2 text-xs">
      <label class="flex flex-col gap-1" title="Kept before the impulse. A band-limited impulse rings before itself, so this is not zero.">
        Before (ms)
        <input
          v-model.number="form.gatePreMs"
          type="number"
          min="0.05"
          step="0.05"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        />
      </label>
      <label class="flex flex-col gap-1" title="Kept after the impulse. This is what decides the lowest frequency the gated response can describe.">
        After (ms)
        <input
          v-model.number="form.gatePostMs"
          type="number"
          min="0.1"
          step="0.5"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        />
      </label>
    </div>

    <label
      class="flex flex-col gap-1 text-xs"
      title="How the gate's edges are tapered. Rectangular is a bare cut, which is what you want when isolating a reflection exactly; the tapered windows trade a little time resolution for less ringing across the spectrum."
    >
      Gate window
      <select
        v-model="form.gateWindow"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      >
        <option v-for="option in IMPULSE_GATE_WINDOWS" :key="option.value" :value="option.value">
          {{ option.label }}
        </option>
      </select>
    </label>

    <label class="flex flex-col gap-1 text-xs" title="Smoothing applied to the gated magnitude curve.">
      Smoothing
      <select
        v-model.number="form.smoothingDenominator"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      >
        <option v-for="option in RESPONSE_SMOOTHING" :key="option.denominator" :value="option.denominator">
          {{ option.label }}
        </option>
      </select>
    </label>

    <!-- The number this whole panel exists to make visible. A gated response drawn below its own
         resolution limit looks perfectly convincing and means nothing. -->
    <p class="text-[11px] font-medium text-amber-700 dark:text-amber-500">
      Nothing below ≈{{ gateResolutionHz.toFixed(0) }} Hz is real at this gate length.
    </p>
    <p class="text-[11px] text-neutral-500 dark:text-neutral-400">
      Reflections out to {{ gateReachMetres.toFixed(2) }} m of extra path are inside the gate ·
      widest here: {{ maxGatePreMs.toFixed(2) }} / {{ maxGatePostMs.toFixed(0) }} ms
    </p>
  </CollapsibleSection>

  <CollapsibleSection title="Reference">
    <label class="flex flex-col gap-1 text-xs" title="What the captured response is divided by, which decides what the result is a response OF.">
      Divide by
      <select
        v-model="form.referenceMode"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      >
        <option v-for="mode in IMPULSE_REFERENCE_MODES" :key="mode.value" :value="mode.value">{{ mode.label }}</option>
      </select>
    </label>

    <template v-if="usesStoredLoopback">
      <label class="flex flex-col gap-1 text-xs">
        Calibration
        <select
          v-model.number="form.referenceCalibrationId"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        >
          <option :value="null">— none —</option>
          <option v-for="reference in references" :key="reference.id" :value="reference.id">
            {{ reference.name }} ({{ (reference.sampleRateHz / 1000).toFixed(0) }} kHz)
          </option>
        </select>
      </label>
      <button
        type="button"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-100 disabled:opacity-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        @click="emit('capture-reference')"
      >
        Capture new loopback…
      </button>
    </template>

    <p class="text-[11px] text-neutral-500 dark:text-neutral-400">
      <template v-if="form.referenceMode === 'None'">
        The result includes the analyzer's own converters and filters.
      </template>
      <template v-else-if="form.referenceMode === 'RightChannel'">
        Feed the same signal to the right input. Cancels the generator and the cabling, and makes the
        reported delay the DUT's own — but nothing on the ADC side.
      </template>
      <template v-else>
        Divides out the analyzer's own response, measured through a straight wire. Matters most at the
        band edges, where the anti-alias filter would otherwise read as DUT roll-off.
      </template>
    </p>
  </CollapsibleSection>

  <CollapsibleSection title="Harmonics">
    <label
      class="flex items-center gap-1.5 text-xs"
      title="Deconvolving a sweep recovers the DUT's harmonic distortion as separate packets ahead of the impulse. Reading them costs nothing extra — the measurement is already made."
    >
      <input v-model="form.extractHarmonics" type="checkbox" :disabled="running" />
      Extract harmonic distortion
    </label>

    <label v-if="form.extractHarmonics" class="flex flex-col gap-1 text-xs" title="Highest order to read.">
      Up to
      <select
        v-model.number="form.maxHarmonicOrder"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      >
        <option v-for="order in [2, 3, 4, 5]" :key="order" :value="order">H{{ order }}</option>
      </select>
    </label>

    <p v-if="form.extractHarmonics" class="text-[11px] text-neutral-500 dark:text-neutral-400">
      Each curve stops at its own frequency: the k-th harmonic can only be seen for fundamentals up to
      {{ (harmonicCeilingHz / 1000).toFixed(1) }} kHz ÷ k, so H{{ form.maxHarmonicOrder }} ends at
      {{ (harmonicCeilingHz / form.maxHarmonicOrder / 1000).toFixed(2) }} kHz.
    </p>

    <p v-if="invalid" class="text-[11px] text-red-600 dark:text-red-400">{{ invalid }}</p>
  </CollapsibleSection>
</template>
