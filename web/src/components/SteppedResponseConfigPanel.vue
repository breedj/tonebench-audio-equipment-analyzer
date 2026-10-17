<script setup lang="ts">
import { computed, nextTick, reactive, watch } from 'vue'
import type {
  SteppedResponseParameters,
  SteppedResponseStartRequest,
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
import { useBandwidthReference } from '../composables/useBandwidthReference'
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
 * Everything a stepped-sine Bode plot needs, staged locally and only sent on Run — the same
 * staged-not-live model SweepConfigPanel uses, and for the same reason: none of it can change
 * mid-run anyway.
 *
 * The stimulus half *is* the THD sweep's: same grid, same three level modes, same load. What it
 * drops is the noise-floor pass (there is no distortion trace for a residual to sit under) and what
 * it adds is the chirp measurement's reference-channel switch, which is what makes the gain trace a
 * real transfer function rather than a level shifted by the drive.
 */

defineProps<{
  running: boolean
}>()

const emit = defineEmits<{
  run: [request: SteppedResponseStartRequest]
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
 * measurement reads only the tone. Mirrors SteppedResponseRequest.EffectiveSampleRateHz.
 */
const HARMONIC_ORDER = 1

/**
 * Bins the tone must be up before its level can be read — SteppedResponseRequest.MinFundamentalBins,
 * and deliberately below the THD sweep's 5. That figure exists so a harmonic can be separated from
 * its own fundamental; with no harmonics to measure, the only requirement is that the tone's
 * mainlobe clears DC.
 */
const MIN_FUNDAMENTAL_BINS = 3

/** Hann, like both sweeps: every tone is snapped to a coherent bin, which is what a window is chosen against. */
const WINDOW: WindowType = 'Hann'

/**
 * The longest record a run reaching below the sweeps' own 4.4 Hz floor may ask for — mirrors
 * SteppedResponseRequest.ExtendedMaxRecordSeconds.
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
  stepsPerOctave: 5,
  /** The level mode and the figure each mode was last set to — see measurements/levelMode.ts. */
  level: createLevelForm({ mode: 'GeneratorVoltage' }),
  generateLeft: true,
  generateRight: true,
  averageCount: 1,
  rightChannelIsReference: true,
  /**
   * On by default, unlike the THD sweep's. A fixed size is chosen for the bottom of the range and
   * then paid at every point above it — and here there are no harmonics to keep in band, so the
   * sample rate falls out with it: a 20 kHz run costs a quarter as much at 48 kHz as at 192 kHz.
   */
  adaptiveResolution: true,
})

// Edited in the Measurements panel next to the corners they define, saved and sent from here —
// see useBandwidthReference. Watched with the form below, since they are part of the stored type.
const { bandwidthReference, bandwidthReferenceLowHz, bandwidthReferenceHighHz } = useBandwidthReference()

/**
 * A stepped-response type's settings live here and nowhere else — as with the sweeps, none of these
 * are server state the host could snapshot on its own. So this panel both reads and writes
 * `measurement_type.params_json` for the SteppedResponse kind.
 */
const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)

// Suppresses the dirty watcher while applyStoredParams writes the form — loading a type's saved
// settings is the opposite of an unsaved edit.
let restoring = false

function applyStoredParams(paramsJson: string) {
  let stored: Partial<SteppedResponseParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<SteppedResponseParameters>
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
  if (typeof stored.generateLeft === 'boolean') form.generateLeft = stored.generateLeft
  if (typeof stored.generateRight === 'boolean') form.generateRight = stored.generateRight
  if (typeof stored.averageCount === 'number') form.averageCount = stored.averageCount
  if (typeof stored.rightChannelIsReference === 'boolean') form.rightChannelIsReference = stored.rightChannelIsReference
  if (typeof stored.adaptiveResolution === 'boolean') form.adaptiveResolution = stored.adaptiveResolution
  // Absent on a type saved before the reference was configurable, which then keeps the default.
  if (stored.bandwidthReference) bandwidthReference.value = stored.bandwidthReference
  if (typeof stored.bandwidthReferenceLowHz === 'number') {
    bandwidthReferenceLowHz.value = stored.bandwidthReferenceLowHz
  }
  if (typeof stored.bandwidthReferenceHighHz === 'number') {
    bandwidthReferenceHighHz.value = stored.bandwidthReferenceHighHz
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
  [form, loadOhms, bandwidthReference, bandwidthReferenceLowHz, bandwidthReferenceHighHz],
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

/**
 * The reference has to actually be fed the stimulus. Turning the right generator channel off while
 * it is the reference would divide the left channel by whatever noise that input is sitting on — a
 * plausible-looking trace of nothing — so the two move together rather than leaving the server to
 * reject the combination after the fact.
 */
watch(
  () => form.generateRight,
  (on) => {
    if (!on) form.rightChannelIsReference = false
  },
)

watch(
  () => form.rightChannelIsReference,
  (isReference) => {
    if (isReference) form.generateRight = true
  },
)

/** Mirrors SteppedResponseRequest.EffectiveSampleRateHz. */
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
// away because it holds part of the previous tone — see SteppedResponseRunner.CaptureWithStimulusAsync.
const RECORDS_BEFORE_AVERAGES = 2

// Discarded at the front of every point on top of the ~65 ms stimulus latency -- see
// useCaptureDelay -- so it belongs in the estimate the same way the latency does.
const { captureDelayMs } = useCaptureDelay()
const perPointDiscardSeconds = computed(() => 0.065 + captureDelayMs.value / 1000)

const durationLabel = computed(() =>
  formatDuration(
    form.adaptiveResolution
      ? estimateAdaptiveDurationSeconds(
          gridHz.value, effectiveSampleRateHz.value, form.averageCount, 0.5, perPointDiscardSeconds.value,
          { maxFftSize: fftCeiling.value, recordsBeforeAverages: RECORDS_BEFORE_AVERAGES },
        )
      : estimateDurationSeconds(
          pointCount.value, FIXED_FFT_SIZE, FIXED_SAMPLE_RATE_HZ, form.averageCount, 0.5, perPointDiscardSeconds.value,
          RECORDS_BEFORE_AVERAGES,
        ),
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
 * Mirrors SteppedResponseRequest.FftCeiling.
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
  if (pointCount.value < 2) return 'A response needs at least two points below Nyquist.'
  if (!form.generateLeft && !form.generateRight) return 'The tone has to be played on at least one channel.'
  if (form.rightChannelIsReference && !form.generateRight)
    return 'The right input is the reference, so the tone has to be played on the right channel too.'
  if (form.level.mode !== 'GeneratorVoltage' && !(loadOhms.value > 0)) return 'An output-referred level needs a load above 0 Ω.'
  const levelProblem = levelInvalid(form.level, loadOhms.value)
  if (levelProblem) return levelProblem
  // Checked only when a run would read it — an untouched default must never block a measurement that
  // never looks at it.
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

/** The band boxes are only read — and so only validated and only shown — by the median reference. */
const usesReferenceBand = computed(() => bandwidthReference.value === 'BandMedian')

defineExpose({ run, invalid, buildParamsJson, rightChannelIsReference: computed(() => form.rightChannelIsReference) })

/**
 * The form as a `SteppedResponseParameters`, for `measurement_type.params_json`. Every field the
 * record declares is written: the server's deserializer binds constructor parameters and treats a
 * missing one as an error, so a partial object would fail to load rather than fall back.
 *
 * `generatorAmpDbv` is omitted by design — it is a *result* field, solved by probing during a run,
 * and a configured type has no value for it yet.
 */
function buildParamsJson(): string {
  const parameters: SteppedResponseParameters = {
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
    generateLeft: form.generateLeft,
    generateRight: form.generateRight,
    fftSize: FIXED_FFT_SIZE,
    window: WINDOW,
    averageCount: form.averageCount,
    sampleRateHz: FIXED_SAMPLE_RATE_HZ,
    adaptiveResolution: form.adaptiveResolution,
    rightChannelIsReference: form.rightChannelIsReference,
    bandwidthReference: bandwidthReference.value,
    bandwidthReferenceLowHz: bandwidthReferenceLowHz.value,
    bandwidthReferenceHighHz: bandwidthReferenceHighHz.value,
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
    generateLeft: form.generateLeft,
    generateRight: form.generateRight,
    averageCount: form.averageCount,
    rightChannelIsReference: form.rightChannelIsReference,
    adaptiveResolution: form.adaptiveResolution,
    bandwidthReference: bandwidthReference.value,
    bandwidthReferenceLowHz: bandwidthReferenceLowHz.value,
    bandwidthReferenceHighHz: bandwidthReferenceHighHz.value,
  })
}
</script>

<template>
  <CollapsibleSection title="Stepped sweep">
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
      title="Generator voltage holds the drive constant. The two output modes probe at 1 kHz first and solve for the generator level that reaches the target at the DUT's output."
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

    <!-- Shared with the FFT view's power readouts (useLoadOhms) rather than a second field that
         could disagree with it. -->
    <label
      v-if="form.level.mode !== 'GeneratorVoltage'"
      class="tb-field"
      title="Amplifier load. Used to turn watts into a target voltage."
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

    <!-- Which channels the tone is *played* on. Both inputs are always analyzed: the gain trace is
         a ratio between them, so the two have to come from the same capture. -->
    <div class="tb-field">
      <span class="tb-group">Generator channels</span>
      <div class="flex gap-3">
        <label class="tb-check-row" title="Play the tone on the left output — the one feeding the DUT.">
          <input v-model="form.generateLeft" type="checkbox" :disabled="running" />
          Left
        </label>
        <!-- Locked on while the right input is the reference: that is the signal the left channel is
             compared against, so it has to be generated. Disabled rather than merely watched back
             on, so the checkbox states why it cannot move instead of springing back. -->
        <label
          class="tb-check-row"
          :title="
            form.rightChannelIsReference
              ? 'Locked on because the right input is the reference — it has to be fed the tone it is compared against. Turn the reference off to measure something else on the right input.'
              : 'Play the tone on the right output. Turn this on to measure a second path and get a phase difference between the two inputs.'
          "
        >
          <input
            v-model="form.generateRight"
            type="checkbox"
            :disabled="running || form.rightChannelIsReference"
          />
          Right
        </label>
      </div>
    </div>

    <label
      class="tb-check-row"
      title="Loop the right output back to the right input and compare the two. This is what makes gain a real transfer function and gives it a phase — without it, gain is the left channel's own level shifted by the drive, and there is no phase to report at all."
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
        <EstimateBadge kind="points" title="How many frequencies the sweep will measure.">≈ {{ pointCount }} points</EstimateBadge>
        <EstimateBadge kind="time" title="Roughly how long the whole sweep will take.">≈ {{ durationLabel }}</EstimateBadge>
      </div>
      <span v-if="truncated" class="text-amber-600 dark:text-amber-500">
        Above {{ maxFreqHz }} Hz is beyond Nyquist — the sweep will stop there.
      </span>
    </template>
  </CollapsibleSection>
</template>
