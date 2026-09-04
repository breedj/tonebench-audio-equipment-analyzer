<script setup lang="ts">
import { computed, nextTick, reactive, watch } from 'vue'
import {
  RESPONSE_RESOLUTIONS,
  RESPONSE_SMOOTHING,
  RESPONSE_SPANS_HZ,
  type FrequencyResponseParameters,
  type FrequencyResponseStartRequest,
  type SweepLevelMode,
  type SweepLevelUnit,
} from '../api/client'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useProjects } from '../composables/useProjects'
import CollapsibleSection from './CollapsibleSection.vue'

defineProps<{ running: boolean }>()

const { types, currentTypeId, markDirty } = useProjects()

// Shared with the FFT view's power readouts and the sweep panel (useLoadOhms) rather than a second
// field that could disagree with them.
const { loadOhms } = useLoadOhms()

/**
 * How the drive level is specified. "Input voltage" is the level fed *into* the DUT, i.e. the
 * generator's own amplitude. The other two name a level at the DUT's *output*, which the server
 * solves for by probing with a chirp.
 *
 * "Output power" is that same probe with one conversion in front of it: V = sqrt(P·R) through the
 * load below. What it does not claim is a power held constant across the sweep — the generator is
 * solved once, from the chirp's median gain, so the watts are the band-average ones. On a DUT with
 * a flat response that is the same thing; on one with a corner in the band it is not, and the
 * response curve itself is what shows the difference.
 */
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

/**
 * The previous app's own defaults (`frmFrequencyResponse.cs:142-157`): the widest span, the coarsest
 * of the three resolutions, 0 dBV of DUT output, 1/24 octave, one average, right channel as the
 * reference. Kept in step with `MeasurementSaveService.DefaultFrequencyResponseParametersJson`.
 */
const form = reactive({
  frequencySpanHz: 96000 as number,
  resolutionMultiplier: 1,
  levelMode: 'OutputVoltage' as SweepLevelMode,
  levelValue: 0,
  levelUnit: 'Dbv' as SweepLevelUnit,
  rightChannelIsReference: true,
  smoothingDenominator: 24,
  averageCount: 1,
  chirpStartHz: 2,
})

const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)

// Suppresses the dirty watcher while applyStoredParams writes the form — loading a type's saved
// settings is the opposite of an unsaved edit, and marking it dirty there would light the Save
// button up on every selection.
let restoring = false

function applyStoredParams(paramsJson: string) {
  let stored: Partial<FrequencyResponseParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<FrequencyResponseParameters>
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
  // Watts is never restored into this field, which holds the *voltage* unit and nothing else: the
  // unit a run actually sends is derived from the mode (see buildRequest), and the power mode hides
  // this control entirely. Restoring it would leave the field holding "Watt" behind that hidden
  // control, so switching the type back to a voltage mode would send a voltage level in watts —
  // which the server rejects outright, from a form that looks perfectly ordinary.
  if (stored.levelUnit && stored.levelUnit !== 'Watt') form.levelUnit = stored.levelUnit
  if (typeof stored.rightChannelIsReference === 'boolean') {
    form.rightChannelIsReference = stored.rightChannelIsReference
  }
  if (typeof stored.smoothingDenominator === 'number') form.smoothingDenominator = stored.smoothingDenominator
  if (typeof stored.averageCount === 'number') form.averageCount = stored.averageCount
  // Absent on a type saved before the chirp start was configurable, which then keeps the default.
  if (typeof stored.chirpStartHz === 'number') form.chirpStartHz = stored.chirpStartHz
  // Shared state rather than a form field, so it is written through the composable — a type that
  // stored a load restores it for every screen reading the same one.
  if (typeof stored.loadOhms === 'number' && stored.loadOhms > 0) loadOhms.value = stored.loadOhms

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
// so "something in the form moved" is exactly the condition. The load is watched alongside it
// because it is saved with the type but lives in a shared composable, not in the form.
watch(
  [form, loadOhms],
  () => {
    if (!restoring) markDirty()
  },
  { deep: true },
)

/** Watts, not volts — the amplitude field, its unit and the load all change with this. */
const isPowerMode = computed(() => form.levelMode === 'OutputPower')

/** Both output-referred modes solve the generator level by probing; only the direct one does not. */
const solvesLevel = computed(() => form.levelMode !== 'GeneratorVoltage')

const sampleRateHz = computed(() => form.frequencySpanHz * 2)

/**
 * The analysis size. The base size scales with the sample rate (32768 / 65536 / 131072 for
 * 48 / 96 / 192 kHz), so the bin width depends only on the resolution multiplier — which is why one
 * resolution list serves all three spans. Mirrors `FrequencyResponseRequest.FftSize`.
 */
const fftSize = computed(() => Math.round(sampleRateHz.value / (48000 / 32768)) * form.resolutionMultiplier)

const binHz = computed(() => sampleRateHz.value / fftSize.value)

const recordSeconds = computed(() => fftSize.value / sampleRateHz.value)

/**
 * Roughly how long a run takes: one record per average, plus one thrown away after the stimulus
 * change (see FrequencyResponseRunner.MeasureOnceAsync), plus the flush and the audio still queued
 * for the DAC between them, plus a couple of probe captures when the level has to be solved for.
 */
const estimatedSeconds = computed(() => {
  const perRecord = recordSeconds.value + 0.15
  const probe = solvesLevel.value ? 2 * (16384 / sampleRateHz.value + 0.15) : 0
  return (form.averageCount + 1) * perRecord + probe
})

/**
 * The voltage a power target actually asks for, shown back to the user. Watts are the unit an
 * amplifier is specified in, but dBV is the unit everything downstream of here works in — the
 * generator level, the input range ladder, the trace itself — so printing both is what makes a
 * surprising trace traceable to the number that was typed.
 */
const targetVolt = computed(() => Math.sqrt(Math.max(form.levelValue, 0) * Math.max(loadOhms.value, 0)))

const targetDbv = computed(() => (targetVolt.value > 0 ? 20 * Math.log10(targetVolt.value) : Number.NaN))

const invalid = computed<string | null>(() => {
  if (!Number.isFinite(form.levelValue)) return 'Amplitude must be a number.'
  if (isPowerMode.value && !(form.levelValue > 0)) return 'Output power must be above 0 W.'
  if (isPowerMode.value && !(loadOhms.value > 0)) return 'A target power needs a load above 0 Ω.'
  if (!isPowerMode.value && form.levelUnit !== 'Dbv' && form.levelValue <= 0)
    return 'A voltage amplitude must be positive.'
  if (form.averageCount < 1 || form.averageCount > 10) return 'Averages must be between 1 and 10.'
  if (!Number.isFinite(form.chirpStartHz) || form.chirpStartHz < 0.5 || form.chirpStartHz > 1000)
    return 'Chirp start must be between 0.5 and 1000 Hz.'
  if (form.chirpStartHz >= (form.frequencySpanHz * 0.98) / 10)
    return 'Chirp start must be at least a decade below the span.'
  return null
})

/**
 * The form as a `FrequencyResponseParameters`, for `measurement_type.params_json`. Every field the
 * record declares is written: the server's deserializer binds constructor parameters and treats a
 * missing one as an error, so a partial object would fail to load rather than fall back.
 *
 * `generatorAmpDbv` is null here by design — it is a *result*, solved by probing during a run, and
 * a configured type has no value for it yet.
 */
function buildParamsJson(): string {
  const parameters: FrequencyResponseParameters = {
    frequencySpanHz: form.frequencySpanHz,
    resolutionMultiplier: form.resolutionMultiplier,
    levelMode: form.levelMode,
    levelValue: form.levelValue,
    // Watts is the only unit the power mode means anything in, and the only mode it means anything
    // for — the server rejects every other pairing rather than reading the number as volts.
    levelUnit: isPowerMode.value ? 'Watt' : form.levelUnit,
    rightChannelIsReference: form.rightChannelIsReference,
    smoothingDenominator: form.smoothingDenominator,
    averageCount: form.averageCount,
    chirpStartHz: form.chirpStartHz,
    loadOhms: loadOhms.value,
    generatorAmpDbv: null,
  }
  return JSON.stringify(parameters)
}

/**
 * The form as a start request, or null when it is not measurable. Returned rather than emitted,
 * because the caller supplies the one thing the panel does not know: whether this is RUN (keep
 * measuring) or SINGLE (once). Two emits, or an emit plus a mode ref, would put that decision in two
 * places.
 */
function buildRequest(continuous: boolean): FrequencyResponseStartRequest | null {
  if (invalid.value) return null

  return {
    frequencySpanHz: form.frequencySpanHz,
    resolutionMultiplier: form.resolutionMultiplier,
    levelMode: form.levelMode,
    levelValue: form.levelValue,
    levelUnit: isPowerMode.value ? 'Watt' : form.levelUnit,
    loadOhms: loadOhms.value,
    rightChannelIsReference: form.rightChannelIsReference,
    smoothingDenominator: form.smoothingDenominator,
    averageCount: form.averageCount,
    chirpStartHz: form.chirpStartHz,
    continuous,
  }
}

defineExpose({ buildRequest, invalid, buildParamsJson })
</script>

<template>
  <CollapsibleSection title="Measurement settings">
    <label
      class="flex flex-col gap-1 text-xs"
      title="Input voltage drives the generator at the level you enter. Output voltage and output power probe the DUT first and set the generator so this much comes back out — the power through the load below, V = sqrt(P x R)."
    >
      Set generator by
      <select
        v-model="form.levelMode"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="mode in LEVEL_MODES" :key="mode.value" :value="mode.value">{{ mode.label }}</option>
      </select>
    </label>

    <div class="grid grid-cols-[1fr_auto] gap-2 text-xs">
      <label
        class="flex flex-col gap-1"
        :title="isPowerMode ? 'The power the DUT should deliver into the load below.' : 'The drive level, in the unit beside it.'"
      >
        {{ isPowerMode ? 'Power (W)' : 'Amplitude' }}
        <input
          v-model.number="form.levelValue"
          type="number"
          step="any"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
      <!-- Hidden rather than disabled in power mode: watts is the only unit that mode accepts, so a
           unit picker there could only ever offer the wrong answers. -->
      <label v-if="!isPowerMode" class="flex flex-col gap-1">
        <span class="invisible">Unit</span>
        <select
          v-model="form.levelUnit"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        >
          <option v-for="unit in VOLTAGE_UNITS" :key="unit.value" :value="unit.value">{{ unit.label }}</option>
        </select>
      </label>
    </div>

    <!-- Shared with the FFT view's power readouts and the sweep panel (useLoadOhms) rather than a
         second field that could disagree with them. -->
    <label
      v-if="isPowerMode"
      class="flex flex-col gap-1 text-xs"
      title="Amplifier load. Used to turn the target watts into the voltage the probe solves for: V = sqrt(P x R)."
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

    <label
      class="flex flex-col gap-1 text-xs"
      title="The highest frequency measured. The sample rate is twice this, so a wider span costs no extra time — only resolution does."
    >
      Frequency span (Hz)
      <select
        v-model.number="form.frequencySpanHz"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="span in RESPONSE_SPANS_HZ" :key="span" :value="span">{{ span }}</option>
      </select>
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="Where the sweep starts. Below about 10 Hz a transformer-coupled DUT sees several times the core flux it does in the band you read, because flux goes as V/f — so the bottom of the sweep can disturb the measurement above it. Raising this is how you find out. The reported band starts 25% above this value."
    >
      Chirp start (Hz)
      <input
        v-model.number="form.chirpStartHz"
        type="number"
        min="0.5"
        max="1000"
        step="0.5"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="Bin width. Finer resolution reaches lower in frequency, and costs proportionally more time per record."
    >
      Resolution (Hz)
      <select
        v-model.number="form.resolutionMultiplier"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="option in RESPONSE_RESOLUTIONS" :key="option.multiplier" :value="option.multiplier">
          {{ option.label }}
        </option>
      </select>
    </label>

    <label
      class="flex items-center gap-1.5 text-xs"
      title="Feed the same signal to the right input and measure left relative to it, so the source and the cabling divide out. Without it, the response is measured against the generated sweep instead."
    >
      <input v-model="form.rightChannelIsReference" type="checkbox" :disabled="running" />
      Right channel is reference
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="Fractional-octave smoothing of the curve. Wider settings remove ripple; the bandwidth numbers are measured on the smoothed curve."
    >
      Smoothing
      <select
        v-model.number="form.smoothingDenominator"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="option in RESPONSE_SMOOTHING" :key="option.denominator" :value="option.denominator">
          {{ option.label }}
        </option>
      </select>
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="How many sweeps to average. Coherence is only meaningful from two upward — a single record always reads 1."
    >
      Averages
      <input
        v-model.number="form.averageCount"
        type="number"
        min="1"
        max="10"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
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
    <p v-if="invalid" class="text-[11px] text-red-600 dark:text-red-400">{{ invalid }}</p>
  </CollapsibleSection>
</template>
