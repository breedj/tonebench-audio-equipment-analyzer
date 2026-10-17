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
import {
  activeLevel,
  applyStoredLevel,
  createLevelForm,
  isPowerMode as modeIsPower,
  levelByModeOf,
  levelInvalid,
  levelTargetVolt,
  POWER_LEVEL_UNITS,
  setLevelUnit,
  setLevelValue,
} from '../measurements/levelMode'
import { useBandwidthReference } from '../composables/useBandwidthReference'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useProjects } from '../composables/useProjects'
import { stimulusFloorNotice } from '../stimulusFloor'
import { spanFallback, supportedSpans } from '../supportedSpans'
import CollapsibleSection from './CollapsibleSection.vue'
import EstimateBadge from './EstimateBadge.vue'

const props = defineProps<{
  running: boolean
  /** The connected analyzer's lowest usable stimulus frequency, in Hz; 0 on one with no floor. */
  minStimulusFrequencyHz?: number
  /** The rates the connected analyzer accepts. A span implies a rate, so this decides which spans are offered. */
  sampleRatesHz?: number[]
}>()



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

/**
 * This panel's own order, mV first, kept from the previous app's dialog. The power mode's units
 * come from measurements/levelMode.ts, which is also what validates a unit against its mode.
 */
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
  /** The level mode and the figure each mode was last set to — see measurements/levelMode.ts. */
  level: createLevelForm({ mode: 'OutputVoltage' }),
  rightChannelIsReference: true,
  smoothingDenominator: 24,
  averageCount: 1,
  chirpStartHz: 2,
})

// Edited in the Measurements panel next to the corners they define, saved and sent from here —
// see useBandwidthReference. Watched with the form below, since they are part of the stored type.
/** Warns before the run that the sweep will start higher than asked. See stimulusFloor.ts. */
const floorNotice = computed(() => stimulusFloorNotice(form.chirpStartHz, props.minStimulusFrequencyHz))

/** Only the spans this analyzer can actually run — a span implies a sample rate. See supportedSpans.ts. */
const availableSpans = computed(() => supportedSpans(RESPONSE_SPANS_HZ, props.sampleRatesHz))

// A span the analyzer cannot run must not be left sitting in the form: the server refuses that run,
// and a panel holding a value it will be refused for is a worse way to find out.
watch(availableSpans, (spans) => {
  const fallback = spanFallback(form.frequencySpanHz, spans)
  if (fallback !== null) form.frequencySpanHz = fallback
})

const { bandwidthReference, bandwidthReferenceLowHz, bandwidthReferenceHighHz } = useBandwidthReference()

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
  // Mode, the selected mode's own value and unit, and the memory of the other two modes — all four
  // in one place, because which of them a stored row carries depends on when it was written.
  applyStoredLevel(form.level, stored)
  if (typeof stored.rightChannelIsReference === 'boolean') {
    form.rightChannelIsReference = stored.rightChannelIsReference
  }
  if (typeof stored.smoothingDenominator === 'number') form.smoothingDenominator = stored.smoothingDenominator
  if (typeof stored.averageCount === 'number') form.averageCount = stored.averageCount
  // Absent on a type saved before the chirp start was configurable, which then keeps the default.
  if (typeof stored.chirpStartHz === 'number') form.chirpStartHz = stored.chirpStartHz
  // Absent on a type saved before the reference was configurable. Those runs were peak-referenced,
  // but the default here is the passband median on purpose: the stored value describes how the type
  // was last configured, not a promise to keep reproducing a reference that was wrong for it.
  if (stored.bandwidthReference) bandwidthReference.value = stored.bandwidthReference
  if (typeof stored.bandwidthReferenceLowHz === 'number') {
    bandwidthReferenceLowHz.value = stored.bandwidthReferenceLowHz
  }
  if (typeof stored.bandwidthReferenceHighHz === 'number') {
    bandwidthReferenceHighHz.value = stored.bandwidthReferenceHighHz
  }
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
  [form, loadOhms, bandwidthReference, bandwidthReferenceLowHz, bandwidthReferenceHighHz],
  () => {
    if (!restoring) markDirty()
  },
  { deep: true },
)

/** Watts, not volts — the amplitude field, its unit and the load all change with this. */
const isPowerMode = computed(() => modeIsPower(form.level.mode))

/** Both output-referred modes solve the generator level by probing; only the direct one does not. */
const solvesLevel = computed(() => form.level.mode !== 'GeneratorVoltage')

/**
 * The level box and its unit dropdown, both reading and writing the *selected* mode's own slot — a
 * writable computed rather than a direct field binding, because which field that is depends on the
 * mode, and so does the list of units the dropdown offers.
 */
const levelValue = computed({
  get: () => activeLevel(form.level).value,
  set: (value: number) => setLevelValue(form.level, value),
})
const levelUnit = computed({
  get: () => activeLevel(form.level).unit,
  set: (unit) => setLevelUnit(form.level, unit),
})
const levelUnits = computed(() => (isPowerMode.value ? POWER_LEVEL_UNITS : VOLTAGE_UNITS))

// Where an output-referred target applies: the same bandwidth reference the Measurements panel
// measures the corners from, so "0 dBV out" is 0 dBV where the corners are counted from. Shown
// under the selector because the coupling is otherwise invisible — a phono stage driven to
// "0 dBV" comes out 20 dB hotter at 20 Hz, and the only way to see what the probe aimed at is
// to know which band it read.
const levelReferenceHint = computed(() => {
  if (!solvesLevel.value) return null
  return bandwidthReference.value === 'Peak'
    ? 'at the response peak (bandwidth reference)'
    : `at ${bandwidthReferenceLowHz.value}–${bandwidthReferenceHighHz.value} Hz (bandwidth reference)`
})

const sampleRateHz = computed(() => form.frequencySpanHz * 2)

/** The band boxes are only read — and so only validated and only shown — by the median reference. */
const usesReferenceBand = computed(() => bandwidthReference.value === 'BandMedian')

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
const targetVolt = computed(() => levelTargetVolt(form.level, loadOhms.value))

const targetDbv = computed(() => (targetVolt.value > 0 ? 20 * Math.log10(targetVolt.value) : Number.NaN))

const invalid = computed<string | null>(() => {
  const levelProblem = levelInvalid(form.level, loadOhms.value)
  if (levelProblem) return levelProblem
  if (form.averageCount < 1 || form.averageCount > 10) return 'Averages must be between 1 and 10.'
  if (!Number.isFinite(form.chirpStartHz) || form.chirpStartHz < 0.5 || form.chirpStartHz > 1000)
    return 'Chirp start must be between 0.5 and 1000 Hz.'
  if (form.chirpStartHz >= (form.frequencySpanHz * 0.98) / 10)
    return 'Chirp start must be at least a decade below the span.'
  // Checked only when a run would read it — an untouched default must never block a measurement
  // that never looks at it, the rule the impedance panel's two stimulus groups already follow.
  if (usesReferenceBand.value) {
    if (!Number.isFinite(bandwidthReferenceLowHz.value) || !Number.isFinite(bandwidthReferenceHighHz.value))
      return 'The reference band must be two numbers.'
    if (bandwidthReferenceLowHz.value < 1 || bandwidthReferenceHighHz.value > 96000)
      return 'The reference band must lie between 1 Hz and 96 kHz.'
    if (bandwidthReferenceHighHz.value <= bandwidthReferenceLowHz.value)
      return 'The reference band must end above where it starts.'
  }
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
    levelMode: form.level.mode,
    // The selected mode's pair is what this type is configured to run at; levelByMode beside it is
    // the memory of the two modes that are not selected. A power unit is only valid with the power
    // mode and a voltage unit only with the other two — the server rejects every other pairing
    // rather than reading the number as volts.
    levelValue: activeLevel(form.level).value,
    levelUnit: activeLevel(form.level).unit,
    levelByMode: levelByModeOf(form.level),
    rightChannelIsReference: form.rightChannelIsReference,
    smoothingDenominator: form.smoothingDenominator,
    averageCount: form.averageCount,
    chirpStartHz: form.chirpStartHz,
    loadOhms: loadOhms.value,
    bandwidthReference: bandwidthReference.value,
    bandwidthReferenceLowHz: bandwidthReferenceLowHz.value,
    bandwidthReferenceHighHz: bandwidthReferenceHighHz.value,
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
    levelMode: form.level.mode,
    levelValue: activeLevel(form.level).value,
    levelUnit: activeLevel(form.level).unit,
    loadOhms: loadOhms.value,
    rightChannelIsReference: form.rightChannelIsReference,
    smoothingDenominator: form.smoothingDenominator,
    averageCount: form.averageCount,
    chirpStartHz: form.chirpStartHz,
    bandwidthReference: bandwidthReference.value,
    bandwidthReferenceLowHz: bandwidthReferenceLowHz.value,
    bandwidthReferenceHighHz: bandwidthReferenceHighHz.value,
    continuous,
  }
}

defineExpose({ buildRequest, invalid, buildParamsJson })
</script>

<template>
  <!-- Grouped as Joost's "Freq. response · P4" mockup groups it: what is played (GENERATOR, CHIRP)
       above how it is read (PROCESSING), each under its own uppercase rule, with the pairs that are
       chosen together — the chirp's span and where it starts, the smoothing and the averages — on
       one row. The analysis the settings add up to is the footer strip rather than three grey
       paragraphs trailing the form. -->
  <CollapsibleSection title="Measurement settings">
    <div class="tb-rule"><span class="tb-group">Generator</span></div>

    <label
      class="tb-field"
      title="Input voltage drives the generator at the level you enter. Output voltage and output power probe the DUT first and set the generator so this much comes back out — the power through the load below, V = sqrt(P x R). The target applies at the bandwidth reference in the Measurements panel: the median over its band, or the peak."
    >
      Set generator by
      <select v-model="form.level.mode" :disabled="running" class="tb-input">
        <option v-for="mode in LEVEL_MODES" :key="mode.value" :value="mode.value">{{ mode.label }}</option>
      </select>
    </label>
    <span v-if="levelReferenceHint" class="-mt-1.5 text-[12px] text-neutral-500 dark:text-neutral-400">
      {{ levelReferenceHint }}
    </span>

    <div class="grid grid-cols-[2fr_1fr] gap-2.5">
      <label
        class="tb-field"
        :title="isPowerMode ? 'The power the DUT should deliver into the load below.' : 'The drive level, in the unit beside it.'"
      >
        {{ isPowerMode ? 'Power' : 'Amplitude' }}
        <input v-model.number="levelValue" type="number" step="any" :disabled="running" class="tb-input" />
      </label>
      <!-- Shown for every mode: the power mode offers W and mW where the two voltage modes offer
           dBV, V and mV, and each mode remembers which of its own it was last set in. -->
      <label class="tb-field">
        Unit
        <select v-model="levelUnit" :disabled="running" class="tb-input">
          <option v-for="unit in levelUnits" :key="unit.value" :value="unit.value">{{ unit.label }}</option>
        </select>
      </label>
    </div>

    <!-- Shared with the FFT view's power readouts and the sweep panel (useLoadOhms) rather than a
         second field that could disagree with them. -->
    <label
      v-if="isPowerMode"
      class="tb-field"
      title="Amplifier load. Used to turn the target watts into the voltage the probe solves for: V = sqrt(P x R)."
    >
      Load (Ω)
      <input v-model.number="loadOhms" type="number" step="any" min="0.1" :disabled="running" class="tb-input" />
    </label>

    <!-- Said before the run rather than only after it: the server clamps and reports, but a sweep
         that quietly starts somewhere else is a surprise worth heading off. A notice, not a
         problem — the measurement is good over the band the card can drive. -->
    <p
      v-if="floorNotice"
      class="rounded-lg border border-amber-300 px-2 py-1.5 text-[12px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
    >
      {{ floorNotice }}
    </p>

    <div class="tb-rule"><span class="tb-group">Chirp</span></div>

    <div class="grid grid-cols-2 gap-2.5">
      <label
        class="tb-field"
        title="The highest frequency measured. The sample rate is twice this, so a wider span costs no extra time — only resolution does."
      >
        Span (Hz)
        <select v-model.number="form.frequencySpanHz" :disabled="running" class="tb-input">
          <option v-for="span in availableSpans" :key="span" :value="span">{{ span }}</option>
        </select>
      </label>
      <label
        class="tb-field"
        title="Where the sweep starts. Below about 10 Hz a transformer-coupled DUT sees several times the core flux it does in the band you read, because flux goes as V/f — so the bottom of the sweep can disturb the measurement above it. Raising this is how you find out. The reported band starts 25% above this value."
      >
        Start (Hz)
        <input
          v-model.number="form.chirpStartHz"
          type="number"
          min="0.5"
          max="1000"
          step="0.5"
          :disabled="running"
          class="tb-input"
        />
      </label>
    </div>

    <label
      class="tb-field"
      title="Bin width. Finer resolution reaches lower in frequency, and costs proportionally more time per record."
    >
      Resolution (Hz)
      <select v-model.number="form.resolutionMultiplier" :disabled="running" class="tb-input">
        <option v-for="option in RESPONSE_RESOLUTIONS" :key="option.multiplier" :value="option.multiplier">
          {{ option.label }}
        </option>
      </select>
    </label>

    <div class="tb-rule"><span class="tb-group">Processing</span></div>

    <div class="grid grid-cols-2 gap-2.5">
      <label
        class="tb-field"
        title="Fractional-octave smoothing of the curve. Wider settings remove ripple; the bandwidth numbers are measured on the smoothed curve."
      >
        Smoothing
        <select v-model.number="form.smoothingDenominator" :disabled="running" class="tb-input">
          <option v-for="option in RESPONSE_SMOOTHING" :key="option.denominator" :value="option.denominator">
            {{ option.label }}
          </option>
        </select>
      </label>
      <label
        class="tb-field"
        title="How many sweeps to average. Coherence is only meaningful from two upward — a single record always reads 1."
      >
        Averages
        <input v-model.number="form.averageCount" type="number" min="1" max="10" :disabled="running" class="tb-input" />
      </label>
    </div>

    <label
      class="tb-check-row"
      title="Feed the same signal to the right input and measure left relative to it, so the source and the cabling divide out. Without it, the response is measured against the generated sweep instead."
    >
      <input v-model="form.rightChannelIsReference" type="checkbox" :disabled="running" class="tb-check" />
      Right channel is reference
    </label>

    <p v-if="invalid" class="text-[12px] text-red-600 dark:text-red-400">{{ invalid }}</p>

    <template #footer>
      <span class="leading-snug">
        Analysis {{ (sampleRateHz / 1000).toFixed(0) }} kHz · {{ fftSize }} pts · {{ binHz.toFixed(2) }} Hz/bin ·
        {{ recordSeconds.toFixed(2) }} s/record
      </span>
      <span v-if="isPowerMode && targetVolt > 0">
        Target {{ targetVolt.toFixed(3) }} V rms into {{ loadOhms }} Ω ({{ targetDbv.toFixed(2) }} dBV)
      </span>
      <div class="flex flex-wrap items-center gap-2">
        <EstimateBadge kind="time" title="Roughly how long one pass will take.">≈ {{ estimatedSeconds.toFixed(1) }} s</EstimateBadge>
      </div>
    </template>
  </CollapsibleSection>
</template>
