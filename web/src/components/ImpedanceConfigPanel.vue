<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import {
  RESPONSE_RESOLUTIONS,
  RESPONSE_SMOOTHING,
  RESPONSE_SPANS_HZ,
  type ImpedanceCableModeName,
  type ImpedanceCalibrationJson,
  type ImpedanceCalTargetName,
  type ImpedanceParameters,
  type ImpedanceStartRequest,
  type ImpedanceStimulusName,
  type SweepLevelUnit,
} from '../api/client'
import {
  adaptiveFftBounds,
  adaptiveFftSize,
  adaptiveSampleRateHz,
  estimateAdaptiveDurationSeconds,
  formatDuration,
  logPerOctave,
} from '../sweep/points'
import { useProjects } from '../composables/useProjects'
import { useImpedance } from '../composables/useImpedance'
import { useImpedanceFixture } from '../composables/useImpedanceFixture'
import { formatHz } from '../response/format'
import { formatOhms } from '../impedance/equivalent'
import { matchesStoredFixture } from '../impedance/fixture'
import CollapsibleSection from './CollapsibleSection.vue'

defineProps<{ running: boolean }>()

const emit = defineEmits<{ (e: 'calibrate', target: ImpedanceCalTargetName): void }>()

const { types, currentTypeId, markDirty } = useProjects()
const { capturedCalibration, calTarget, storedOpenCal, storedShortCal, selectedStimulus } = useImpedance()
const {
  fixture,
  hasCalibration: fixtureHasCalibration,
  error: fixtureError,
  load: loadFixture,
  save: saveFixture,
} = useImpedanceFixture()

void loadFixture()

const VOLTAGE_UNITS: { value: SweepLevelUnit; label: string }[] = [
  { value: 'MilliVolt', label: 'mV' },
  { value: 'Volt', label: 'V' },
  { value: 'Dbv', label: 'dBV' },
]

const STIMULI: { value: ImpedanceStimulusName; label: string }[] = [
  { value: 'Chirp', label: 'Chirp sweep' },
  { value: 'SteppedSine', label: 'Stepped sine' },
  { value: 'PinkNoise', label: 'Pink noise' },
]

/** The stimuli analysed as whole spectra, which share the span, resolution and smoothing controls. */
const BROADBAND_STIMULI: ImpedanceStimulusName[] = ['Chirp', 'PinkNoise']

/**
 * Mirrors ImpedanceRequest.MinFundamentalBins — deliberately below the THD sweep's 5. That figure
 * exists so a harmonic can be separated from its own fundamental; nothing here reads a harmonic, so
 * the only requirement is that the tone's mainlobe clears DC.
 */
const MIN_FUNDAMENTAL_BINS = 3

/** One harmonic, not five: only the tone itself is read, which drops a 20 kHz run from 192 kHz to 48 kHz. */
const HARMONIC_ORDER = 1

/** The raised ceiling a run reaching below the sweeps' own 4.4 Hz floor may ask for — SweepResolution.ExtendedMaxRecordSeconds. */
const EXTENDED_MAX_RECORD_SECONDS = 524288 / 192000
const EXTENDED_MAX_FFT_SIZE = 524288

function powerOfTwoAtMost(value: number): number {
  let n = 1
  while (n * 2 <= value && n < EXTENDED_MAX_FFT_SIZE) n *= 2
  return n
}

const CABLE_MODES: { value: ImpedanceCableModeName; label: string }[] = [
  { value: 'None', label: 'Nothing' },
  { value: 'Measured', label: 'Measured short' },
  { value: 'Fixed', label: 'Fixed value' },
]

/**
 * Kept in step with `MeasurementSaveService.DefaultImpedanceParametersJson`.
 *
 * The sweep starts at 10 Hz rather than the frequency response's 2 Hz: a driver's resonance is the
 * point of this measurement and 10 Hz is below any of them, while a decade lower costs
 * signal-to-noise for nothing. Smoothing is light so a high-Q resonance peak is not blunted.
 */
const form = reactive({
  /**
   * What the DUT is measured with, and — separately — what the open/short captures play.
   *
   * <p>Two controls rather than one because a standard is a curve in <i>frequency</i>: it is
   * interpolated in log f, and a fixture is linear and passive, so what it presents does not depend
   * on how it was excited. A chirp therefore calibrates in seconds a fixture whose DUT is then
   * measured one tone at a time, which is the usual pairing. The literal-minded combination is still
   * available; it just is not forced.</p>
   */
  stimulus: 'Chirp' as ImpedanceStimulusName,
  calibrationStimulus: 'Chirp' as ImpedanceStimulusName,
  startFreqHz: 20,
  endFreqHz: 20000,
  stepsPerOctave: 5,
  adaptiveResolution: true,
  referenceOhms: 100,
  rightChannelIsReference: false,
  frequencySpanHz: 24000 as number,
  resolutionMultiplier: 1,
  levelValue: -20,
  levelUnit: 'Dbv' as SweepLevelUnit,
  calibrationLevelValue: 0,
  smoothingDenominator: 48,
  averageCount: 2,
  chirpStartHz: 10,
  cableMode: 'None' as ImpedanceCableModeName,
  fixedCableOhms: 0,
  fixedCableMillihenry: 0,
  analyzerInputOhms: null as number | null,
  applySweepGate: false,
  openCal: null as ImpedanceCalibrationJson | null,
  shortCal: null as ImpedanceCalibrationJson | null,
  /**
   * Take the reference resistor and both standards from the globally configured fixture instead of
   * this measurement's own.
   *
   * <p>The resistor comes with them and is not separable: both standards are stored as the measured
   * channel ratio, and both depend on R — an open reads <c>a·Z_in/(R + Z_in)</c>, a short reads
   * <c>a·Z_lead/(R + Z_lead)</c>. Applying a fixture's curves at some other resistance
   * mis-corrects every reading with nothing on screen to show it, so adopting the fixture adopts
   * its resistor too.</p>
   */
  useGlobalFixture: false,
})

const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)

// Suppresses the dirty watcher while applyStoredParams writes the form — loading a type's saved
// settings is the opposite of an unsaved edit.
let restoring = false

function applyStoredParams(paramsJson: string) {
  let stored: Partial<ImpedanceParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<ImpedanceParameters>
  } catch {
    return
  }

  // Guarded field by field rather than Object.assign'd: another kind's params_json parses fine and
  // would otherwise overwrite the form with undefined.
  restoring = true
  if (typeof stored.referenceOhms === 'number' && stored.referenceOhms > 0) {
    form.referenceOhms = stored.referenceOhms
  }
  if (typeof stored.rightChannelIsReference === 'boolean') {
    form.rightChannelIsReference = stored.rightChannelIsReference
  }
  if (typeof stored.frequencySpanHz === 'number') form.frequencySpanHz = stored.frequencySpanHz
  if (typeof stored.resolutionMultiplier === 'number') form.resolutionMultiplier = stored.resolutionMultiplier
  if (typeof stored.levelValue === 'number') form.levelValue = stored.levelValue
  if (typeof stored.calibrationLevelValue === 'number') {
    form.calibrationLevelValue = stored.calibrationLevelValue
  }
  // Watts is never restored: a one-port has no output power to aim at, and the server rejects it.
  if (stored.levelUnit && stored.levelUnit !== 'Watt') form.levelUnit = stored.levelUnit
  if (typeof stored.smoothingDenominator === 'number') form.smoothingDenominator = stored.smoothingDenominator
  if (typeof stored.averageCount === 'number') form.averageCount = stored.averageCount
  if (typeof stored.chirpStartHz === 'number') form.chirpStartHz = stored.chirpStartHz
  const known = (value: unknown): value is ImpedanceStimulusName =>
    STIMULI.some((option) => option.value === value)
  if (known(stored.stimulus)) form.stimulus = stored.stimulus
  if (known(stored.calibrationStimulus)) form.calibrationStimulus = stored.calibrationStimulus
  if (typeof stored.startFreqHz === 'number' && stored.startFreqHz > 0) form.startFreqHz = stored.startFreqHz
  if (typeof stored.endFreqHz === 'number' && stored.endFreqHz > 0) form.endFreqHz = stored.endFreqHz
  if (typeof stored.stepsPerOctave === 'number' && stored.stepsPerOctave > 0) {
    form.stepsPerOctave = stored.stepsPerOctave
  }
  if (typeof stored.adaptiveResolution === 'boolean') form.adaptiveResolution = stored.adaptiveResolution
  if (stored.cableMode) form.cableMode = stored.cableMode
  if (typeof stored.fixedCableOhms === 'number') form.fixedCableOhms = stored.fixedCableOhms
  if (typeof stored.fixedCableMillihenry === 'number') form.fixedCableMillihenry = stored.fixedCableMillihenry
  form.analyzerInputOhms =
    typeof stored.analyzerInputOhms === 'number' && stored.analyzerInputOhms > 0
      ? stored.analyzerInputOhms
      : null
  if (typeof stored.applySweepGate === 'boolean') form.applySweepGate = stored.applySweepGate
  if (typeof stored.useGlobalFixture === 'boolean') form.useGlobalFixture = stored.useGlobalFixture

  // The fixture calibration. This is the whole reason it survives a restart — the server keeps none.
  form.openCal = stored.openCal ?? null
  form.shortCal = stored.shortCal ?? null

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
  form,
  () => {
    if (!restoring) markDirty()
  },
  { deep: true },
)

/**
 * A finished calibration run's curve, folded into the form so the next Save writes it into the
 * measurement type. The runner hands it back on the status and keeps no copy, so this watcher is the
 * only route from a cal run to somewhere it outlasts the app.
 */
watch(capturedCalibration, (calibration) => {
  if (!calibration) return

  if (calTarget.value === 'Open') form.openCal = calibration
  else if (calTarget.value === 'Short') form.shortCal = calibration
})

// The panel owns the stored standards (they live in the measurement type's params_json), but the
// chart has to draw them. Published rather than passed down through App.vue: the chart is three
// components away, and this keeps one copy of the answer.
watch(
  [() => form.openCal, () => form.shortCal],
  ([open, short]) => {
    storedOpenCal.value = open
    storedShortCal.value = short
  },
  { immediate: true },
)

/**
 * What the run will actually use. Adopting the fixture takes its resistor and its standards
 * together — see the form field's own remarks on why they cannot be mixed.
 */
const usingFixture = computed(() => form.useGlobalFixture && fixtureHasCalibration.value)

const effectiveReferenceOhms = computed(() =>
  usingFixture.value && fixture.value ? fixture.value.referenceOhms : form.referenceOhms,
)

const effectiveOpenCal = computed(() =>
  usingFixture.value ? (fixture.value?.openCal ?? null) : form.openCal,
)

const effectiveShortCal = computed(() =>
  usingFixture.value ? (fixture.value?.shortCal ?? null) : form.shortCal,
)

/**
 * Whether the sweep runs past the band the stored standards actually cover. Outside it a curve is
 * held flat, which is an extrapolation — silent, and easy to reach with a global calibration that
 * gets reused across spans it was never captured at.
 */
const fixtureBandWarning = computed(() => {
  if (!usingFixture.value || !fixture.value) return null

  const top = fixture.value.capturedHighHz
  const analysisTop = form.frequencySpanHz * 0.95
  if (!(top > 0) || analysisTop <= top * 1.001) return null

  return `This calibration was captured to ${formatHz(top)}; above that it is held flat rather than measured. Recapture it at this span, or lower the span.`
})

/**
 * Which settings groups are live. Both are always sent — the panel does not know which button will
 * be pressed next — so each is shown, and validated, only when some run would actually read it.
 */
const usesChirp = computed(() => form.stimulus === 'Chirp' || form.calibrationStimulus === 'Chirp')
const usesStepped = computed(
  () => form.stimulus === 'SteppedSine' || form.calibrationStimulus === 'SteppedSine',
)

/**
 * The record's own settings — span, resolution, smoothing — belong to the chirp and the noise
 * stimulus alike, which is why these are separate from `usesChirp`. Only the sweep's start frequency
 * is the chirp's alone.
 */
const usesBroadband = computed(
  () =>
    BROADBAND_STIMULI.includes(form.stimulus) || BROADBAND_STIMULI.includes(form.calibrationStimulus),
)

/** Whether the *measurement* (rather than its calibration) analyses a whole spectrum. */
const measuresBroadband = computed(() => BROADBAND_STIMULI.includes(form.stimulus))

/** A stepped measurement is single-shot; App.vue reads this to hide the continuous-run button. */
watch(
  () => form.stimulus,
  (value) => {
    selectedStimulus.value = value
  },
  { immediate: true },
)

// ---- the stepped grid, mirroring ImpedanceRequest's derived members ----

const steppedSampleRateHz = computed(() =>
  form.adaptiveResolution ? adaptiveSampleRateHz(form.endFreqHz, HARMONIC_ORDER) : 192000,
)

/** The pre-snap log grid, shared by the point count and the duration estimate. */
const steppedGridHz = computed(() =>
  logPerOctave(form.startFreqHz, form.endFreqHz, form.stepsPerOctave).filter(
    (hz) => hz > 0 && hz <= 0.98 * (steppedSampleRateHz.value / 2),
  ),
)

const steppedPointCount = computed(() => steppedGridHz.value.length)

const steppedStandardCeiling = computed(() => adaptiveFftBounds(steppedSampleRateHz.value).ceiling)

const steppedStandardLowestHz = computed(
  () => (MIN_FUNDAMENTAL_BINS * steppedSampleRateHz.value) / steppedStandardCeiling.value,
)

/** Raised automatically, and only, when the start asks to go below what the standard ceiling can measure. */
const steppedFftCeiling = computed(() =>
  form.startFreqHz < steppedStandardLowestHz.value
    ? Math.max(
        steppedStandardCeiling.value,
        Math.min(powerOfTwoAtMost(EXTENDED_MAX_RECORD_SECONDS * steppedSampleRateHz.value), EXTENDED_MAX_FFT_SIZE),
      )
    : steppedStandardCeiling.value,
)

const steppedMinFreqHz = computed(
  () => (MIN_FUNDAMENTAL_BINS * steppedSampleRateHz.value) / steppedFftCeiling.value,
)

const steppedMaxFreqHz = computed(() => Math.round(0.98 * (steppedSampleRateHz.value / 2)))

const steppedFftRange = computed(() => {
  if (steppedGridHz.value.length === 0) return null
  const sizes = steppedGridHz.value.map((hz) =>
    adaptiveFftSize(hz, steppedSampleRateHz.value, steppedFftCeiling.value),
  )
  return { max: Math.max(...sizes), min: Math.min(...sizes) }
})

/**
 * One throwaway record after each stimulus change, then one whole record per average.
 *
 * <p>A hop fraction of 1 rather than the sweeps' 0.5, and that is not a copying slip: every average
 * here sits behind its own `Flush(0)`, so the records are independent — which is the only thing that
 * makes the measured coherence mean anything. See ImpedanceRunner.CaptureStepAsync.</p>
 */
const steppedDurationLabel = computed(() =>
  formatDuration(
    estimateAdaptiveDurationSeconds(
      steppedGridHz.value,
      steppedSampleRateHz.value,
      form.averageCount,
      1.0,
      0.1,
      { maxFftSize: steppedFftCeiling.value, recordsBeforeAverages: 1 },
    ),
  ),
)

const steppedNyquistWarning = computed(() =>
  form.endFreqHz > steppedMaxFreqHz.value
    ? `Points above ${formatHz(steppedMaxFreqHz.value)} are dropped at ${steppedSampleRateHz.value / 1000} kHz.`
    : null,
)

const sampleRateHz = computed(() => form.frequencySpanHz * 2)

/** Mirrors `ImpedanceRequest.FftSize`. */
const fftSize = computed(() => Math.round(sampleRateHz.value / (48000 / 32768)) * form.resolutionMultiplier)

const binHz = computed(() => sampleRateHz.value / fftSize.value)

const recordSeconds = computed(() => fftSize.value / sampleRateHz.value)

/**
 * One record per average, plus one thrown away after the stimulus change, plus a short capture for
 * the input-range probe. The same for a chirp and for noise: they differ in what is played, not in
 * how many records it takes.
 */
const estimatedSeconds = computed(() => {
  const perRecord = recordSeconds.value + 0.15
  const probe = 16384 / sampleRateHz.value + 0.15
  return (form.averageCount + 1) * perRecord + probe
})

/**
 * How far the measurement node sits below the drive for a nominal 8 ohm load, which is what says
 * whether the reference resistor is a sensible size. Both channels share one input attenuator, so
 * everything the divider throws away is signal-to-noise the DUT channel does not get — and
 * sensitivity of the ratio to Z is best when the reference is near the impedance being measured.
 */
const dividerHintDb = computed(() => {
  const r = form.referenceOhms
  if (!(r > 0)) return Number.NaN
  return 20 * Math.log10(8 / (r + 8))
})

const hasOpenCal = computed(() => (form.openCal?.freqHz?.length ?? 0) > 1)
const hasShortCal = computed(() => (form.shortCal?.freqHz?.length ?? 0) > 1)

const invalid = computed<string | null>(() => {
  if (!Number.isFinite(form.referenceOhms) || form.referenceOhms < 0.1 || form.referenceOhms > 1e7)
    return 'The reference resistor must be between 0.1 Ω and 10 MΩ.'
  if (!Number.isFinite(form.levelValue)) return 'Amplitude must be a number.'
  if (form.levelUnit !== 'Dbv' && form.levelValue <= 0) return 'A voltage amplitude must be positive.'
  if (!Number.isFinite(form.calibrationLevelValue)) return 'The calibration level must be a number.'
  if (form.levelUnit !== 'Dbv' && form.calibrationLevelValue <= 0)
    return 'A calibration voltage must be positive.'
  if (form.averageCount < 1 || form.averageCount > 10) return 'Averages must be between 1 and 10.'
  if (usesChirp.value) {
    if (!Number.isFinite(form.chirpStartHz) || form.chirpStartHz < 0.5 || form.chirpStartHz > 1000)
      return 'Chirp start must be between 0.5 and 1000 Hz.'
    if (form.chirpStartHz >= (form.frequencySpanHz * 0.98) / 10)
      return 'Chirp start must be at least a decade below the span.'
  }
  if (usesStepped.value) {
    if (!Number.isFinite(form.startFreqHz) || form.startFreqHz <= 0)
      return 'The sweep must start above 0 Hz.'
    if (!Number.isFinite(form.endFreqHz) || form.endFreqHz <= form.startFreqHz)
      return 'The sweep must end above where it starts.'
    if (form.stepsPerOctave < 1 || form.stepsPerOctave > 48)
      return 'Steps per octave must be between 1 and 48.'
    if (form.startFreqHz < steppedMinFreqHz.value)
      return `A stepped sweep cannot read a level below ${steppedMinFreqHz.value.toFixed(2)} Hz.`
    if (steppedPointCount.value < 2)
      return `This grid produces ${steppedPointCount.value} point(s) below ${formatHz(steppedMaxFreqHz.value)}; a sweep needs two.`
  }
  if (form.cableMode === 'Measured' && !effectiveShortCal.value)
    return 'Subtracting a measured short needs one captured first.'
  if (form.useGlobalFixture && !fixtureHasCalibration.value)
    return 'There is no global fixture calibration stored yet.'
  if (form.cableMode === 'Fixed' && (!Number.isFinite(form.fixedCableOhms) || form.fixedCableOhms < 0))
    return 'A cable resistance cannot be negative.'
  if (form.cableMode === 'Fixed' && (!Number.isFinite(form.fixedCableMillihenry) || form.fixedCableMillihenry < 0))
    return 'A cable inductance cannot be negative.'
  if (form.analyzerInputOhms !== null && !(form.analyzerInputOhms > 0))
    return 'The analyzer input impedance must be above 0 Ω, or left blank.'
  return null
})

/**
 * The form as an `ImpedanceParameters`, for `measurement_type.params_json`. Every field the record
 * declares is written: the server's deserializer binds constructor parameters and treats a missing
 * one as an error, so a partial object would fail to load rather than fall back.
 */
function buildParamsJson(): string {
  const parameters: ImpedanceParameters = {
    method: 'ReferenceResistor',
    stimulus: form.stimulus,
    calibrationStimulus: form.calibrationStimulus,
    startFreqHz: form.startFreqHz,
    endFreqHz: form.endFreqHz,
    stepsPerOctave: form.stepsPerOctave,
    adaptiveResolution: form.adaptiveResolution,
    referenceOhms: form.referenceOhms,
    rightChannelIsReference: form.rightChannelIsReference,
    frequencySpanHz: form.frequencySpanHz,
    resolutionMultiplier: form.resolutionMultiplier,
    chirpStartHz: form.chirpStartHz,
    levelValue: form.levelValue,
    levelUnit: form.levelUnit,
    smoothingDenominator: form.smoothingDenominator,
    averageCount: form.averageCount,
    calibrationLevelValue: form.calibrationLevelValue,
    cableMode: form.cableMode,
    fixedCableOhms: form.fixedCableOhms,
    fixedCableMillihenry: form.fixedCableMillihenry,
    analyzerInputOhms: form.analyzerInputOhms,
    applySweepGate: form.applySweepGate,
    openCal: form.openCal,
    shortCal: form.shortCal,
    useGlobalFixture: form.useGlobalFixture,
    generatorAmpDbv: null,
  }
  return JSON.stringify(parameters)
}

/**
 * The form as a start request, or null when it is not measurable. `calTarget` is the caller's to
 * supply, exactly as `continuous` is: RUN, SINGLE and the two calibration buttons are four ways into
 * one request shape, and deciding here would put that choice in two places.
 */
function buildRequest(continuous: boolean, calTarget: ImpedanceCalTargetName = 'None'): ImpedanceStartRequest | null {
  // A calibration is captured once, and it is measured through the *uncorrected* path — so the
  // stored standards are deliberately not sent with one.
  const calibrating = calTarget !== 'None'
  if (invalid.value && !calibrating) return null

  return {
    method: 'ReferenceResistor',
    stimulus: form.stimulus,
    calibrationStimulus: form.calibrationStimulus,
    startFreqHz: form.startFreqHz,
    endFreqHz: form.endFreqHz,
    stepsPerOctave: form.stepsPerOctave,
    adaptiveResolution: form.adaptiveResolution,
    // Resolved here rather than on the server: the fixture is a browser-side convenience, and the
    // request that reaches the runner names exactly what it measured with, so a saved result records
    // the real numbers rather than a reference to settings that may since have changed.
    referenceOhms: effectiveReferenceOhms.value,
    rightChannelIsReference: form.rightChannelIsReference,
    frequencySpanHz: form.frequencySpanHz,
    resolutionMultiplier: form.resolutionMultiplier,
    levelValue: form.levelValue,
    levelUnit: form.levelUnit,
    smoothingDenominator: form.smoothingDenominator,
    averageCount: form.averageCount,
    // A stepped sweep is measured once: the server rejects a continuous one, and a run that is
    // minutes long has nothing to gain from repeating.
    continuous: calibrating || form.stimulus === 'SteppedSine' ? false : continuous,
    calibrationLevelValue: form.calibrationLevelValue,
    chirpStartHz: form.chirpStartHz,
    calTarget,
    cableMode: calibrating ? 'None' : form.cableMode,
    fixedCableOhms: form.fixedCableOhms,
    fixedCableMillihenry: form.fixedCableMillihenry,
    analyzerInputOhms: form.analyzerInputOhms,
    applySweepGate: form.applySweepGate,
    openCal: calibrating ? null : effectiveOpenCal.value,
    shortCal: calibrating ? null : effectiveShortCal.value,
  }
}

/**
 * Promotes what this measurement just captured to the global fixture.
 *
 * <p>This is how the global calibration gets populated at all. The alternative — a second
 * calibration workflow inside the Configuration dialog — would need its own run controls, device
 * access, progress and error handling, duplicating what this panel already has.</p>
 *
 * <p>The reference resistor goes with the curves, because they are only valid at the resistance they
 * were captured through.</p>
 */
/**
 * What the button is doing, so it can say so. A store is a round trip to the server and finishes in
 * a few milliseconds, which is exactly the case where silence is indistinguishable from a dead
 * button — the first version of this reported nothing at all and swallowed failures with it.
 */
const storeState = ref<'idle' | 'saving' | 'stored' | 'failed'>('idle')
let storeResetTimer: number | undefined

onBeforeUnmount(() => window.clearTimeout(storeResetTimer))

async function storeAsGlobalFixture() {
  if (!form.openCal) return

  storeState.value = 'saving'
  const band = form.openCal.freqHz

  try {
    await saveFixture({
      referenceOhms: form.referenceOhms,
      calibrationLevelValue: form.calibrationLevelValue,
      levelUnit: form.levelUnit,
      openCal: form.openCal,
      shortCal: form.shortCal,
      capturedLowHz: band[0] ?? 0,
      capturedHighHz: band[band.length - 1] ?? 0,
      capturedUtc: new Date().toISOString(),
      hasCalibration: true,
    })
    storeState.value = 'stored'
  } catch {
    // The message itself is on the composable's `error`, shown below; this only drives the button.
    storeState.value = 'failed'
  }

  window.clearTimeout(storeResetTimer)
  storeResetTimer = window.setTimeout(() => {
    storeState.value = 'idle'
  }, 4000)
}

/**
 * Whether the global fixture already holds exactly what this measurement has.
 *
 * <p>When it does there is nothing to store, so the button goes away rather than offering an action
 * that would write back what is already there. It comes back the moment anything differs — a fresh
 * open or short capture, or a changed reference resistor, since the standards are only valid at the
 * resistance they were captured through.</p>
 */
const fixtureAlreadyStored = computed(() =>
  matchesStoredFixture(
    { referenceOhms: form.referenceOhms, openCal: form.openCal, shortCal: form.shortCal },
    fixture.value,
  ),
)

/**
 * Shown only when pressing it would change something. Kept visible for a moment after a store so the
 * confirmation can be read — otherwise the button would vanish at the instant it succeeded, which is
 * the same "did that work?" problem in a new disguise.
 */
const canStoreGlobally = computed(
  () => !usingFixture.value && hasOpenCal.value && (!fixtureAlreadyStored.value || storeState.value !== 'idle'),
)

const storeLabel = computed(() => {
  if (storeState.value === 'saving') return 'Storing…'
  if (storeState.value === 'stored') return 'Stored ✓'
  if (storeState.value === 'failed') return 'Store failed'
  return fixtureHasCalibration.value ? 'Replace global calibration' : 'Store as global calibration'
})

function clearCalibration(which: 'open' | 'short') {
  if (which === 'open') form.openCal = null
  else {
    form.shortCal = null
    if (form.cableMode === 'Measured') form.cableMode = 'None'
  }
}

defineExpose({ buildRequest, invalid, buildParamsJson })
</script>

<template>
  <CollapsibleSection title="Fixture">
    <label
      class="flex flex-col gap-1 text-xs"
      title="The resistor in series between the generator and the DUT. The measurement is only as accurate as this number, so use a 1% part and enter its measured value."
    >
      Reference resistor (Ω)
      <input
        v-if="!usingFixture"
        v-model.number="form.referenceOhms"
        type="number"
        min="0.1"
        step="any"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
      <input
        v-else
        :value="effectiveReferenceOhms"
        type="number"
        disabled
        title="Taken from the global fixture, because its calibration is only valid at the resistance it was captured through."
        class="rounded border border-neutral-300 bg-neutral-100 px-2 py-1 text-neutral-500 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-400"
      />
    </label>

    <p class="text-xs text-neutral-500 dark:text-neutral-400">
      An 8 Ω load sits {{ Number.isFinite(dividerHintDb) ? dividerHintDb.toFixed(1) : '—' }} dB below the drive at
      the measurement node. Both channels share one input range, so a reference resistor far from the
      impedance you are measuring costs signal-to-noise on the channel that matters.
    </p>

    <label
      class="flex flex-col gap-1 text-xs"
      title="Which input taps the generator side of the reference resistor. A bench fixture is normally wired left-as-reference; the simulator needs Right, because its fixture models sit on the left channel."
    >
      Reference channel
      <select
        v-model="form.rightChannelIsReference"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option :value="false">Left (generator side)</option>
        <option :value="true">Right (generator side)</option>
      </select>
    </label>
  </CollapsibleSection>

  <CollapsibleSection title="Calibration">
    <label
      v-if="!usingFixture"
      class="flex flex-col gap-1 text-xs"
      title="What the two Measure buttons play. A standard is a curve in frequency and a fixture is linear, so a chirp-captured open or short corrects a stepped measurement unchanged — in seconds rather than minutes."
    >
      Calibration stimulus
      <select
        v-model="form.calibrationStimulus"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="option in STIMULI" :key="option.value" :value="option.value">{{ option.label }}</option>
      </select>
    </label>

    <label
      class="flex items-center gap-2 text-xs"
      :title="
        fixtureHasCalibration
          ? 'Use the calibration stored in Configuration instead of this measurement\'s own. Its reference resistor comes with it, because the standards are only valid at the resistance they were captured through.'
          : 'Nothing stored yet. Capture an open below, then press Store as global calibration.'
      "
    >
      <!--
        Disabled only while a run is going. Deliberately NOT disabled when there is no global
        calibration: a measurement type stored with this ticked, opened on a machine (or after a
        config reset) where the fixture is empty, made `invalid` permanently true — "There is no
        global fixture calibration stored yet" — while the one control that could clear it was
        greyed out. RUN, SINGLE and both Measure buttons were dead with no way back. Ticking it on
        with nothing stored is harmless: it is caught by `invalid` with that same message, which is
        now advice rather than a trap.
      -->
      <input v-model="form.useGlobalFixture" type="checkbox" :disabled="running" />
      Use the global calibration
    </label>

    <!-- Shown whether or not this measurement adopts it, so the stored fixture is always visible
         and pressing Store has something that visibly changes. -->
    <p
      v-if="fixture && fixtureHasCalibration"
      class="text-xs"
      :class="usingFixture ? 'text-emerald-600 dark:text-emerald-400' : 'text-neutral-500 dark:text-neutral-400'"
    >
      Global: {{ formatOhms(fixture.referenceOhms) }} reference ·
      {{ fixture.shortCal ? 'open + short' : 'open only' }} ·
      {{ formatHz(fixture.capturedLowHz) }}–{{ formatHz(fixture.capturedHighHz) }}<span v-if="fixture.capturedUtc">
        · {{ new Date(fixture.capturedUtc).toLocaleString() }}</span>
    </p>

    <p v-if="fixtureBandWarning" class="text-xs text-amber-600 dark:text-amber-400">
      {{ fixtureBandWarning }}
    </p>

    <p v-if="!usingFixture" class="text-xs text-neutral-500 dark:text-neutral-400">
      Open first: with nothing on the DUT terminals both channels should read the same, so whatever
      they do not read is the gain and phase difference between them. That is what makes the phase
      trace believable.
    </p>

    <div v-if="!usingFixture" class="flex items-center justify-between gap-2 text-xs">
      <span :class="hasOpenCal ? 'text-emerald-600 dark:text-emerald-400' : 'text-neutral-500 dark:text-neutral-400'">
        Open: {{ hasOpenCal ? 'captured' : 'not captured' }}
      </span>
      <span class="flex gap-1">
        <button
          type="button"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="emit('calibrate', 'Open')"
        >
          Measure
        </button>
        <button
          v-if="hasOpenCal"
          type="button"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="clearCalibration('open')"
        >
          Clear
        </button>
      </span>
    </div>

    <div v-if="!usingFixture" class="flex items-center justify-between gap-2 text-xs">
      <span :class="hasShortCal ? 'text-emerald-600 dark:text-emerald-400' : 'text-neutral-500 dark:text-neutral-400'">
        Short: {{ hasShortCal ? 'captured' : 'not captured' }}
      </span>
      <span class="flex gap-1">
        <button
          type="button"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="emit('calibrate', 'Short')"
        >
          Measure
        </button>
        <button
          v-if="hasShortCal"
          type="button"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="clearCalibration('short')"
        >
          Clear
        </button>
      </span>
    </div>

    <button
      v-if="canStoreGlobally"
      type="button"
      :disabled="running || storeState === 'saving' || fixtureAlreadyStored"
      class="rounded border px-2 py-1 text-xs disabled:opacity-40"
      :class="
        storeState === 'stored'
          ? 'border-emerald-500 text-emerald-600 dark:border-emerald-600 dark:text-emerald-400'
          : storeState === 'failed'
            ? 'border-red-500 text-red-600 dark:border-red-600 dark:text-red-400'
            : 'border-neutral-300 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800'
      "
      title="Saves these standards and this reference resistor as the machine's global calibration, so every impedance measurement can reuse them. Stored in the configuration database, not with your measurements."
      @click="storeAsGlobalFixture"
    >
      {{ storeLabel }}
    </button>

    <p
      v-if="!usingFixture && hasOpenCal && fixtureAlreadyStored && storeState === 'idle'"
      class="text-xs text-neutral-500 dark:text-neutral-400"
    >
      These standards are already the global calibration.
    </p>

    <p v-if="fixtureError" class="text-xs text-red-600 dark:text-red-400">{{ fixtureError }}</p>

    <label
      v-if="!usingFixture"
      class="flex flex-col gap-1 text-xs"
      title="Calibration runs drive harder than measurements on purpose. A shorted fixture puts the measured channel about 60 dB below the reference one, and both share a single input range set from the loud one — so the short is the hardest thing this measurement has to see. The fixture is linear, so measuring it loudly costs nothing."
    >
      Calibration drive level
      <input
        v-model.number="form.calibrationLevelValue"
        type="number"
        step="any"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>

    <p class="text-xs text-neutral-500 dark:text-neutral-400">
      Measured on a real fixture: at &minus;20 dBV a shorted lead set read 0.21 &Omega; at &minus;22&deg;
      with coherence 0.80; the same leads at 0 dBV read a flat 0.127 &Omega; at +0.8&deg; with coherence
      0.998. If a captured short looks noisy at the bottom of the band, raise this.
    </p>

    <label
      class="flex flex-col gap-1 text-xs"
      title="The leads and connectors between the fixture and the DUT have a resistance of their own, and it is in series with everything you measure."
    >
      Subtract for the leads
      <select
        v-model="form.cableMode"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="mode in CABLE_MODES" :key="mode.value" :value="mode.value">{{ mode.label }}</option>
      </select>
    </label>

    <div v-if="form.cableMode === 'Fixed'" class="grid grid-cols-2 gap-2 text-xs">
      <label class="flex flex-col gap-1">
        Resistance (Ω)
        <input
          v-model.number="form.fixedCableOhms"
          type="number"
          min="0"
          step="any"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
      <label class="flex flex-col gap-1">
        Inductance (mH)
        <input
          v-model.number="form.fixedCableMillihenry"
          type="number"
          min="0"
          step="any"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
    </div>
  </CollapsibleSection>

  <CollapsibleSection title="Measurement settings">
    <label
      class="flex flex-col gap-1 text-xs"
      title="A chirp excites the whole band at once and takes seconds. A stepped sine plays one tone at a time: minutes, but it puts the whole drive on the frequency being read, which is what a high-impedance DUT and the bottom of the band need. Pink noise is as quick as the chirp and keeps improving with every average, and it carries no harmonic packets to gate."
    >
      Stimulus
      <select
        v-model="form.stimulus"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="option in STIMULI" :key="option.value" :value="option.value">{{ option.label }}</option>
      </select>
    </label>

    <div class="grid grid-cols-[1fr_auto] gap-2 text-xs">
      <label class="flex flex-col gap-1" title="The generator level. Lower is gentler on a driver; higher buys signal-to-noise.">
        Drive level
        <input
          v-model.number="form.levelValue"
          type="number"
          step="any"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
      <label class="flex flex-col gap-1">
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

    <!-- Shown whenever *either* selector asks for a whole-spectrum stimulus, so a stepped
         measurement calibrated by chirp can still say what that chirp does. -->
    <label v-if="usesBroadband" class="flex flex-col gap-1 text-xs">
      <span>Frequency span<span v-if="!measuresBroadband" class="text-neutral-500 dark:text-neutral-400"> (calibration)</span></span>
      <select
        v-model.number="form.frequencySpanHz"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="span in RESPONSE_SPANS_HZ" :key="span" :value="span">{{ span / 1000 }} kHz</option>
      </select>
    </label>

    <label v-if="usesBroadband" class="flex flex-col gap-1 text-xs">
      Resolution
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
      v-if="usesChirp"
      class="flex flex-col gap-1 text-xs"
      title="Where the chirp starts. 10 Hz is below any driver resonance; going lower costs signal-to-noise for nothing unless you are measuring a transformer."
    >
      <span>Chirp start (Hz)<span v-if="form.stimulus !== 'Chirp'" class="text-neutral-500 dark:text-neutral-400"> (calibration)</span></span>
      <input
        v-model.number="form.chirpStartHz"
        type="number"
        min="0.5"
        max="1000"
        step="any"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>

    <!--
      Gated on the *measurement's* stimulus, not `usesChirp`, unlike the sweep controls above it.
      Those genuinely configure a chirp calibration; smoothing does not. It is applied by
      ImpedanceTraceBuilder.Build when a curve is drawn, and a calibration's stored standard comes
      from ImpedanceCalibration.Capture, which samples the raw ratio -- so with a stepped measurement
      and a chirp calibration this control could only restyle the open/short curve while it was on
      screen, and never touch a measurement. A control that implies more than it does is worse than
      no control. Noise is smoothed on exactly the same path, so it reads the same control.
    -->
    <label
      v-if="measuresBroadband"
      class="flex flex-col gap-1 text-xs"
      title="Light smoothing keeps a high-Q resonance peak its real height."
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

    <div v-if="usesStepped" class="grid grid-cols-2 gap-2 text-xs">
      <label class="flex flex-col gap-1">
        <span>Sweep from (Hz)<span v-if="form.stimulus !== 'SteppedSine'" class="text-neutral-500 dark:text-neutral-400"> (cal)</span></span>
        <input
          v-model.number="form.startFreqHz"
          type="number"
          step="any"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
      <label class="flex flex-col gap-1">
        To (Hz)
        <input
          v-model.number="form.endFreqHz"
          type="number"
          step="any"
          :disabled="running"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
    </div>

    <label v-if="usesStepped" class="flex flex-col gap-1 text-xs" title="How densely the band is sampled. Every point is a separate capture, so this is what a run's length is made of.">
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
      v-if="usesStepped"
      class="flex items-center gap-2 text-xs"
      title="Sizes each point's record for its own frequency rather than for the bottom of the band, which is most of what makes the top of a sweep fast."
    >
      <input v-model="form.adaptiveResolution" type="checkbox" :disabled="running" />
      Adaptive resolution
    </label>

    <label class="flex flex-col gap-1 text-xs">
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

    <p v-if="measuresBroadband" class="text-xs text-neutral-500 dark:text-neutral-400">
      {{ binHz.toFixed(2) }} Hz per bin · {{ recordSeconds.toFixed(2) }} s per record · about
      {{ estimatedSeconds.toFixed(1) }} s per run
    </p>
    <p v-else class="text-xs text-neutral-500 dark:text-neutral-400">
      ≈{{ steppedPointCount }} points · {{ steppedSampleRateHz / 1000 }} kHz ·
      <span v-if="steppedFftRange">
        {{ steppedFftRange.max }}–{{ steppedFftRange.min }} point records ·
      </span>
      about {{ steppedDurationLabel }}
    </p>
    <p v-if="form.stimulus === 'SteppedSine' && steppedNyquistWarning" class="text-xs text-amber-600 dark:text-amber-400">
      {{ steppedNyquistWarning }}
    </p>
  </CollapsibleSection>

  <CollapsibleSection title="Advanced" :default-open="false">
    <!-- The chirp's alone: those packets are a product of sweep deconvolution, so there is nothing
         for a gate to delete in a noise or stepped estimate and the server ignores the flag there.
         Hidden rather than disabled, since a control that cannot apply is not a setting. -->
    <label
      v-if="form.stimulus === 'Chirp'"
      class="flex items-center gap-2 text-xs"
      title="Removes the harmonic packets a swept measurement of a distorting DUT picks up, at a factor of two in frequency resolution. Rarely needed for a passive load."
    >
      <input v-model="form.applySweepGate" type="checkbox" :disabled="running" />
      Gate the sweep's harmonic packets
    </label>

    <label
      class="flex flex-col gap-1 text-xs"
      title="The analyzer's own input impedance sits across the measurement node. Leave blank for anything up to a few kilohms; it only matters when the reference resistor approaches it, as when measuring a valve grid input."
    >
      Analyzer input impedance (Ω, blank = ignore)
      <input
        v-model.number="form.analyzerInputOhms"
        type="number"
        min="1"
        step="any"
        placeholder="from the QA403 datasheet"
        :disabled="running"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>
  </CollapsibleSection>

  <p v-if="invalid" class="text-xs text-amber-600 dark:text-amber-400">{{ invalid }}</p>
</template>
