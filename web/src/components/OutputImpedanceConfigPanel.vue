<script setup lang="ts">
import { computed, nextTick, reactive, watch } from 'vue'
import type {
  OutputImpedanceParameters,
  OutputImpedanceStartRequest,
  WindowType,
} from '../api/client'
import {
  activeLevel,
  applyStoredLevel,
  createLevelForm,
  isPowerMode as modeIsPower,
  LEVEL_MODES,
  levelByModeOf,
  levelInvalid,
  levelUnitsFor,
  setLevelUnit,
  setLevelValue,
} from '../measurements/levelMode'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import { useProjects } from '../composables/useProjects'
import CollapsibleSection from './CollapsibleSection.vue'
import EstimateBadge from './EstimateBadge.vue'
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
 * Everything an output-impedance (damping factor) run needs, staged locally and only sent on Run —
 * the crosstalk panel's shape with the Direction replaced by the two resistances and the reference
 * checkbox. The grid, the level modes and the adaptive-resolution arithmetic are the stepped
 * panel's verbatim, because the run underneath is the stepped sweep, twice (see OutputImpedanceRunner).
 *
 * The load is the shared `useLoadOhms` — the amplifier load the FFT screen's power readouts and the
 * sweeps use — because it is the same resistor: what is connected for the first pass is what a
 * power target converts through. The nominal impedance is this panel's own, since it names a
 * convention (8 Ω on every specification sheet) rather than a part on the bench.
 */

defineProps<{
  running: boolean
}>()

const emit = defineEmits<{
  run: [request: OutputImpedanceStartRequest]
}>()

const { loadOhms } = useLoadOhms()
const { types, currentTypeId, markDirty } = useProjects()

/**
 * The fixed analysis preset, used only when adaptive resolution is off — the same 192 kHz / 131072
 * both sweeps fix, so a run taken this way is comparable with a THD sweep of the same DUT.
 */
const FIXED_SAMPLE_RATE_HZ = 192000
const FIXED_FFT_SIZE = 131072
const FIXED_BIN_SIZE_HZ = FIXED_SAMPLE_RATE_HZ / FIXED_FFT_SIZE

/**
 * Harmonics of the top point that have to stay in band. One, not the THD sweep's five: this
 * measurement reads only the tone. Mirrors OutputImpedanceRequest.EffectiveSampleRateHz.
 */
const HARMONIC_ORDER = 1

/**
 * Bins the tone must be up before its level can be read — OutputImpedanceRequest.MinFundamentalBins,
 * and deliberately below the THD sweep's 5. That figure exists so a harmonic can be separated from
 * its own fundamental; with no harmonics to measure, the only requirement is that the tone's
 * mainlobe clears DC.
 */
const MIN_FUNDAMENTAL_BINS = 3

/** Hann, like both sweeps: every tone is snapped to a coherent bin, which is what a window is chosen against. */
const WINDOW: WindowType = 'Hann'

/**
 * The longest record a run reaching below the sweeps' own 4.4 Hz floor may ask for — mirrors
 * SteppedResponseRequest.ExtendedMaxRecordSeconds, which this request shares.
 *
 * The sweeps cap every record at 0.6827 s, which is 1.465 Hz bins at every supported rate and so a
 * 4.4 Hz floor at three bins. That cap exists so adaptive resolution never makes a THD sweep slower
 * than fixed mode; here the bottom of the band is the whole point and only a handful of points are
 * down there. Raised automatically, and only, when the start frequency asks for it.
 *
 * A *time* bound, like the one it replaces, so the floor it yields is the same 1.1 Hz at 48, 96 and
 * 192 kHz alike. 2.73 s is the chirp's own longest record.
 */
const EXTENDED_MAX_RECORD_SECONDS = 524288 / 192000
const EXTENDED_MAX_FFT_SIZE = 524288

function powerOfTwoAtMost(value: number): number {
  let n = 1
  while (n * 2 <= value && n < EXTENDED_MAX_FFT_SIZE) n *= 2
  return n
}

const form = reactive({
  startFreqHz: 20,
  endFreqHz: 20000,
  stepsPerOctave: 3,
  /**
   * The tutorial's run: 1 W into 8 Ω, probed on the first pass. Each mode keeps its own figure and
   * its own unit — see measurements/levelMode.ts.
   */
  level: createLevelForm({ mode: 'OutputPower' }),
  /**
   * The second pass's load. 4 Ω rather than the tutorial's open circuit: an output transformer with
   * signal on it and no load is how tube amplifiers are killed. `secondPassOpen` is the explicit
   * opt-in for amplifiers that can take it.
   */
  secondLoadOhms: 4,
  secondPassOpen: false,
  /** What the damping factor is quoted against. 8 Ω whatever the load, unless the tap says otherwise. */
  nominalOhms: 8,
  /** Off by default: the referenced method needs the right output looped to the right input. */
  rightChannelIsReference: false,
  averageCount: 1,
  /**
   * On by default, unlike the THD sweep's. A fixed size is chosen for the bottom of the range and
   * then paid at every point above it — and here there are no harmonics to keep in band, so the
   * sample rate falls out with it: a 20 kHz run costs a quarter as much at 48 kHz as at 192 kHz.
   */
  adaptiveResolution: true,
})

/**
 * An output-impedance type's settings live here and nowhere else — none of these are server state
 * the host could snapshot on its own. So this panel both reads and writes
 * `measurement_type.params_json` for the OutputImpedance kind.
 */
const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)

// Suppresses the dirty watcher while applyStoredParams writes the form — loading a type's saved
// settings is the opposite of an unsaved edit.
let restoring = false

function applyStoredParams(paramsJson: string) {
  let stored: Partial<OutputImpedanceParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<OutputImpedanceParameters>
  } catch {
    return
  }

  // Guarded field by field rather than Object.assign'd: another kind's params_json parses fine and
  // would otherwise overwrite the form with undefined.
  restoring = true
  if (typeof stored.startFreqHz === 'number') form.startFreqHz = stored.startFreqHz
  if (typeof stored.endFreqHz === 'number') form.endFreqHz = stored.endFreqHz
  if (typeof stored.stepsPerOctave === 'number') form.stepsPerOctave = stored.stepsPerOctave
  // Mode, the selected mode's own value and unit, and the memory of the other two modes — all four
  // in one place, because which of them a stored row carries depends on when it was written.
  applyStoredLevel(form.level, stored)
  if (typeof stored.loadOhms === 'number' && stored.loadOhms > 0) loadOhms.value = stored.loadOhms
  // A stored null means the open second pass; an absent field (a type written before it existed)
  // keeps the safe default.
  if ('secondLoadOhms' in stored) {
    if (typeof stored.secondLoadOhms === 'number' && stored.secondLoadOhms > 0) {
      form.secondLoadOhms = stored.secondLoadOhms
      form.secondPassOpen = false
    } else if (stored.secondLoadOhms === null) {
      form.secondPassOpen = true
    }
  }
  if (typeof stored.nominalOhms === 'number' && stored.nominalOhms > 0) form.nominalOhms = stored.nominalOhms
  if (typeof stored.rightChannelIsReference === 'boolean') form.rightChannelIsReference = stored.rightChannelIsReference
  if (typeof stored.averageCount === 'number') form.averageCount = stored.averageCount
  if (typeof stored.adaptiveResolution === 'boolean') form.adaptiveResolution = stored.adaptiveResolution

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

const isPowerMode = computed(() => modeIsPower(form.level.mode))

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
const levelUnits = computed(() => levelUnitsFor(form.level.mode))

/** Loaded, then open: the grid is always walked twice. */
const passCount = 2

/** Mirrors OutputImpedanceRequest.EffectiveSampleRateHz. */
const effectiveSampleRateHz = computed(() =>
  form.adaptiveResolution ? adaptiveSampleRateHz(form.endFreqHz, HARMONIC_ORDER) : FIXED_SAMPLE_RATE_HZ,
)

/** The pre-snap log grid, shared by the point count and the duration estimate. */
const gridHz = computed(() =>
  logPerOctave(form.startFreqHz, form.endFreqHz, form.stepsPerOctave).filter(
    (hz) => hz > 0 && hz <= 0.98 * (effectiveSampleRateHz.value / 2),
  ),
)

const pointCount = computed(() => gridHz.value.length)

/** FFT size at the two ends of the run — the range the readout quotes for adaptive mode. */
const adaptiveFftRange = computed(() => {
  if (gridHz.value.length === 0) return null
  const sizes = gridHz.value.map((hz) => adaptiveFftSize(hz, effectiveSampleRateHz.value, fftCeiling.value))
  return { max: Math.max(...sizes), min: Math.min(...sizes) }
})

// Two records before the averages, not one: the record armed by each stimulus change is thrown
// away because it holds part of the previous tone — see SteppedCaptureSupport.CaptureWithStimulusAsync.
const RECORDS_BEFORE_AVERAGES = 2

// Discarded at the front of every point on top of the ~65 ms stimulus latency -- see
// useCaptureDelay -- so it belongs in the estimate the same way the latency does.
const { captureDelayMs } = useCaptureDelay()
const perPointDiscardSeconds = computed(() => 0.065 + captureDelayMs.value / 1000)

const durationLabel = computed(() =>
  formatDuration(
    passCount *
    (form.adaptiveResolution
      ? estimateAdaptiveDurationSeconds(
          gridHz.value, effectiveSampleRateHz.value, form.averageCount, 0.5, perPointDiscardSeconds.value,
          { maxFftSize: fftCeiling.value, recordsBeforeAverages: RECORDS_BEFORE_AVERAGES },
        )
      : estimateDurationSeconds(
          pointCount.value, FIXED_FFT_SIZE, FIXED_SAMPLE_RATE_HZ, form.averageCount, 0.5, perPointDiscardSeconds.value,
          RECORDS_BEFORE_AVERAGES,
        )),
  ),
)

/** The highest frequency the run can actually reach — points above it are dropped server-side. */
const maxFreqHz = computed(() => Math.round(0.98 * (effectiveSampleRateHz.value / 2)))

/** The sweeps' own ceiling at this rate, and the floor it implies — 4.4 Hz at every supported rate. */
const standardCeiling = computed(() => adaptiveFftBounds(effectiveSampleRateHz.value).ceiling)
const standardLowestHz = computed(
  () => (MIN_FUNDAMENTAL_BINS * effectiveSampleRateHz.value) / standardCeiling.value,
)

/**
 * Raised automatically, and only, when the start frequency asks to go below what the standard
 * ceiling can measure — so an ordinary 20 Hz sweep is completely unaffected and pays nothing for it.
 * Mirrors OutputImpedanceRequest.FftCeiling.
 */
const fftCeiling = computed(() =>
  form.startFreqHz < standardLowestHz.value
    ? Math.max(
        standardCeiling.value,
        Math.min(powerOfTwoAtMost(EXTENDED_MAX_RECORD_SECONDS * effectiveSampleRateHz.value), EXTENDED_MAX_FFT_SIZE),
      )
    : standardCeiling.value,
)

/** The lowest frequency this run can read a level at — matches the server's rejection. */
const minFreqHz = computed(() => {
  const binSizeHz = form.adaptiveResolution
    ? effectiveSampleRateHz.value / fftCeiling.value
    : FIXED_BIN_SIZE_HZ
  return MIN_FUNDAMENTAL_BINS * binSizeHz
})

const truncated = computed(() => form.endFreqHz > maxFreqHz.value)

const invalid = computed(() => {
  if (!(form.startFreqHz > 0) || !(form.endFreqHz > form.startFreqHz)) return 'End frequency must be above the start.'
  if (form.startFreqHz < minFreqHz.value)
    return `Start frequency must be at least ${minFreqHz.value.toFixed(1)} Hz — below that the tone is too few bins up to read a level from.`
  if (pointCount.value < 2) return 'An output-impedance curve needs at least two points below Nyquist.'
  if (!(loadOhms.value > 0)) return 'Load 1 must be above 0 Ω.'
  if (!form.secondPassOpen && !(form.secondLoadOhms > 0)) return 'Load 2 must be above 0 Ω, or the second pass taken open.'
  if (!form.secondPassOpen && Math.abs(form.secondLoadOhms - loadOhms.value) < 1e-6 * Math.max(1, loadOhms.value))
    return 'The two loads must differ — two passes into the same load say nothing about the output impedance.'
  if (!(form.nominalOhms > 0)) return 'The nominal impedance the damping factor is quoted against must be above 0 Ω.'
  return levelInvalid(form.level, loadOhms.value)
})

defineExpose({ run, invalid, buildParamsJson })

/**
 * The form as an `OutputImpedanceParameters`, for `measurement_type.params_json`. Every field the
 * record declares is written: the server's deserializer binds constructor parameters and treats a
 * missing one as an error. The generator level and the input range are omitted by design — they
 * are *result* fields, found during a run, and a configured type has no value for them yet.
 */
function buildParamsJson(): string {
  const parameters: OutputImpedanceParameters = {
    startFreqHz: form.startFreqHz,
    endFreqHz: form.endFreqHz,
    stepsPerOctave: form.stepsPerOctave,
    levelMode: form.level.mode,
    // The selected mode's pair is what this type is configured to run at; levelByMode beside it is
    // the memory of the two modes that are not selected.
    levelValue: activeLevel(form.level).value,
    levelUnit: activeLevel(form.level).unit,
    levelByMode: levelByModeOf(form.level),
    loadOhms: loadOhms.value,
    secondLoadOhms: form.secondPassOpen ? null : form.secondLoadOhms,
    nominalOhms: form.nominalOhms,
    rightChannelIsReference: form.rightChannelIsReference,
    fftSize: FIXED_FFT_SIZE,
    window: WINDOW,
    averageCount: form.averageCount,
    sampleRateHz: FIXED_SAMPLE_RATE_HZ,
    adaptiveResolution: form.adaptiveResolution,
  }
  return JSON.stringify(parameters)
}

function run() {
  if (invalid.value) return
  emit('run', {
    startFreqHz: form.startFreqHz,
    endFreqHz: form.endFreqHz,
    stepsPerOctave: form.stepsPerOctave,
    levelMode: form.level.mode,
    // A power unit is only valid with the power mode and a voltage unit only with the other two —
    // the server rejects the other pairings rather than guessing, and the panel cannot offer one.
    levelValue: activeLevel(form.level).value,
    levelUnit: activeLevel(form.level).unit,
    loadOhms: loadOhms.value,
    secondLoadOhms: form.secondPassOpen ? null : form.secondLoadOhms,
    nominalOhms: form.nominalOhms,
    rightChannelIsReference: form.rightChannelIsReference,
    averageCount: form.averageCount,
    adaptiveResolution: form.adaptiveResolution,
  })
}
</script>

<template>
  <CollapsibleSection title="Output impedance sweep">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field" title="Lowest frequency measured. The sweep steps upward from here.">
        Start (Hz)
        <input
          v-model.number="form.startFreqHz"
          type="number"
          min="1"
          :disabled="running"
          class="tb-input"
        />
      </label>
      <label class="tb-field" title="Highest frequency measured. Anything above 98% of Nyquist is dropped.">
        End (Hz)
        <input
          v-model.number="form.endFreqHz"
          type="number"
          min="2"
          :disabled="running"
          class="tb-input"
        />
      </label>
    </div>

    <div class="grid grid-cols-2 gap-2.5">
      <label
        class="tb-field"
        title="How many measurement points per octave. More points resolve narrow features but take proportionally longer — every point is its own capture."
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
      <label class="tb-field" title="Averages per point. More averages lower the noise floor of every reading, at a proportional cost in time.">
        Averages per point
        <input
          v-model.number="form.averageCount"
          type="number"
          min="1"
          max="10"
          :disabled="running"
          class="tb-input"
        />
      </label>
    </div>

    <label
      class="tb-field"
      title="Generator voltage holds the drive constant. The two output modes probe at 1 kHz first, in the first pass (into Load 1), and solve for the generator level that reaches the target at the amplifier's output; the second pass reuses that level."
    >
      Level mode
      <select
        v-model="form.level.mode"
        :disabled="running"
        class="tb-input"
      >
        <option v-for="mode in LEVEL_MODES" :key="mode.value" :value="mode.value">{{ mode.label }}</option>
      </select>
    </label>

    <!-- Level is as narrow as Steps per octave, the THD sweep's width for a short number: a level
         is a few digits, and a field the width of the rail reads as if it expected more. -->
    <div class="grid grid-cols-[2fr_1fr] items-end gap-2.5">
      <label class="tb-field">
        {{ isPowerMode ? 'Power' : 'Level' }}
        <input
          v-model.number="levelValue"
          type="number"
          step="any"
          :disabled="running"
          class="tb-input"
        />
      </label>
      <!-- Shown for every mode: the power mode's units are W and mW where the two voltage modes'
           are dBV, V and mV, and each mode remembers which of its own it was last set in. -->
      <label class="tb-field">
        Unit
        <select
          v-model="levelUnit"
          :disabled="running"
          class="tb-input"
        >
          <option v-for="unit in levelUnits" :key="unit.value" :value="unit.value">{{ unit.label }}</option>
        </select>
      </label>
    </div>

    <!-- Load 1 is shared with the FFT view's power readouts (useLoadOhms) rather than a second field
         that could disagree with it: the resistor connected for the first pass is the one a power
         target converts through. Load 2 is this panel's own. Both always shown, whatever the level
         mode, because they are the R₁ and R₂ the output impedance is solved from. -->
    <div class="grid grid-cols-2 gap-2.5">
      <label
        class="tb-field"
        title="The resistor connected to the amplifier's output for the first pass — non-inductive, close to the terminals. It is R₁ in the solution, and what a power target converts through."
      >
        Load 1 (Ω)
        <input
          v-model.number="loadOhms"
          type="number"
          step="any"
          min="0.1"
          :disabled="running"
          class="tb-input"
        />
      </label>
      <label
        class="tb-field"
        :class="{ 'opacity-50': form.secondPassOpen }"
        title="The resistor connected for the second pass — a different value, so the two passes solve for the source behind them. 4 Ω after 8 Ω is the usual pair, and safe for any amplifier."
      >
        Load 2 (Ω)
        <input
          v-model.number="form.secondLoadOhms"
          type="number"
          step="any"
          min="0.1"
          :disabled="running || form.secondPassOpen"
          class="tb-input"
        />
      </label>
    </div>

    <label
      class="tb-check-row"
      title="Take the second pass with nothing on the output — the QA40x tutorial's arrangement. Only for an amplifier that can drive an open circuit with signal on it: never a tube amplifier, whose output transformer needs its load."
    >
      <input v-model="form.secondPassOpen" type="checkbox" :disabled="running" />
      Second pass open (no load) — not for tube amplifiers
    </label>

    <label
      class="tb-field"
      title="What the damping factor is quoted against: DF = nominal / |Zout|. Specification sheets always use 8 Ω; set it to the tap you measured on if you want the DF for that tap."
    >
      Nominal (Ω)
      <input v-model.number="form.nominalOhms" type="number" step="any" min="0.1" :disabled="running" class="tb-input" />
    </label>

    <label
      class="tb-check-row"
      title="Loop the right output to the right input, in both passes, and measure each gain as a complex value. Off, the ratio is one of levels and Zout is assumed in phase with the load — an output coil's reactance is then under-read (the tutorial's 0.066 Ω against 0.19 Ω from its 1.5 µH). On, Zout comes out with its phase and the coil is recovered."
    >
      <input v-model="form.rightChannelIsReference" type="checkbox" :disabled="running" />
      Right channel is reference
    </label>

    <label
      class="tb-check-row"
      title="Sizes the FFT per point instead of holding one size across the whole run, and picks the sample rate from the top of the frequency range. This measurement reads only the tone — no harmonics — so a 20 kHz run needs 48 kHz rather than 192 kHz, a quarter of the work. Turn it off for a fixed 192 kHz / 131072-point reference comparable with a THD sweep: slower, but the same resolution at every frequency."
    >
      <input v-model="form.adaptiveResolution" type="checkbox" :disabled="running" />
      Adaptive resolution
    </label>



    <p v-if="invalid" class="text-xs text-red-600 dark:text-red-400">{{ invalid }}</p>

    <template #footer>
      <p
        v-if="form.adaptiveResolution && adaptiveFftRange"
        class="leading-snug"
        title="The FFT size varies per point so each record holds a similar number of cycles of the tone. The largest size is used at the bottom of the sweep, the smallest at the top."
      >
        Analysis: {{ effectiveSampleRateHz / 1000 }} kHz ·
        {{ adaptiveFftRange.max }}–{{ adaptiveFftRange.min }} points ·
        {{ (effectiveSampleRateHz / adaptiveFftRange.max).toFixed(3) }}–{{
          (effectiveSampleRateHz / adaptiveFftRange.min).toFixed(2)
        }}
        Hz per bin
      </p>
      <p v-else class="leading-snug">
        Analysis: {{ FIXED_SAMPLE_RATE_HZ / 1000 }} kHz · {{ FIXED_FFT_SIZE }} points ·
        {{ FIXED_BIN_SIZE_HZ.toFixed(3) }} Hz per bin
      </p>
      <div class="flex flex-wrap items-center gap-2">
        <EstimateBadge kind="points" title="How many frequencies each of the two passes will measure.">
          ≈ {{ pointCount }} points × 2
        </EstimateBadge>
        <EstimateBadge kind="time" title="Roughly how long both passes will take — the load change in between is yours.">
          ≈ {{ durationLabel }}
        </EstimateBadge>
      </div>
      <span>…plus however long the load change takes.</span>
      <span v-if="truncated" class="text-amber-600 dark:text-amber-500">
        Above {{ maxFreqHz }} Hz is beyond Nyquist — the sweep will stop there.
      </span>
    </template>
  </CollapsibleSection>
</template>
