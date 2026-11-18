<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import type {
  DeviceRanges,
  OscilloscopeParameters,
  OscilloscopeStartRequest,
  ScopeRunMode,
  ScopeTriggerKind,
  ScopeTriggerSlope,
  ScopeTriggerSource,
} from '../api/client'
import { useProjects } from '../composables/useProjects'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import CollapsibleSection from './CollapsibleSection.vue'
import EstimateBadge from './EstimateBadge.vue'
import { clampNumberField } from '../numberField'
import {
  DEFAULT_SCOPE_AVERAGE_COUNT,
  MAX_SCOPE_AVERAGE_COUNT,
  MIN_SCOPE_AVERAGE_COUNT,
  SCOPE_ACCUMULATIONS,
  SCOPE_ACCUMULATION_HINTS,
  accumulationSettleSeconds,
  accumulationUnavailableNote,
  clampAverageCount,
  modeCanAccumulate,
  type ScopeAccumulation,
} from '../scope/acquisition'
import {
  DEFAULT_SCOPE_TRIGGER,
  SCOPE_PULSE_CONDITIONS,
  SCOPE_RUN_MODES,
  SCOPE_RUN_MODE_HINTS,
  SCOPE_TRIGGER_KINDS,
  SCOPE_TRIGGER_KIND_HINTS,
  SCOPE_TRIGGER_SLOPES,
  SCOPE_TRIGGER_SLOPE_HINTS,
  SCOPE_TRIGGER_SOURCES,
  SCOPE_TRIGGER_SOURCE_HINTS,
  dropoutProblem,
  pulseConditionFor,
  pulseWidthBounds,
  pulseWidthFor,
  pulseWidthProblem,
  stimulusSourceProblem,
  triggerIsStimulus,
  triggerSourceProblem,
  windowLevelsProblem,
  type ScopePulseCondition,
} from '../scope/triggerSpec'
import { formatScopeTime } from '../scope/timeAxis'
import { roundVoltsForWindow } from '../scope/voltAxis'

/**
 * Everything an oscilloscope run needs, staged locally and only sent on Run or Single.
 *
 * <p><b>One panel with two sections rather than the two separate components the plan named.</b> The
 * acquisition settings and the trigger are one <code>params_json</code> and one start request, so
 * splitting them would leave two components each owning half a form — and a trigger panel would
 * then have to reach into this one's state to build either. Two <code>CollapsibleSection</code>s
 * give the groups their own headings, which is what the split was for.</p>
 *
 * <p>The six settings the <em>chart</em> also needs are <code>defineModel</code>s rather than
 * internal state: the trigger level because the chart <b>writes</b> it (dragging the line is how a
 * level is set, and the box here is the precise fallback), and the record length, the pre-trigger
 * fraction, the hysteresis, the source and the mode because the chart draws them — including before
 * anything has been captured, where the axis it shows is the one the settings ask for.</p>
 */

const triggerLevelV = defineModel<number>('triggerLevelV', { required: true })
const triggerHysteresisV = defineModel<number>('triggerHysteresisV', { required: true })
/**
 * The kind, and the window trigger's other bound — models rather than internal state because the
 * <em>chart</em> draws them: a window trigger's band is to that kind what the hysteresis band is to
 * the edge trigger, the one thing that makes two numbers in a rail aimable at a signal.
 */
const triggerKind = defineModel<ScopeTriggerKind>('triggerKind', { default: 'Edge' })
const triggerLevelLowV = defineModel<number>('triggerLevelLowV', { default: 0 })
const triggerSource = defineModel<ScopeTriggerSource>('triggerSource', { required: true })
const recordSeconds = defineModel<number>('recordSeconds', { required: true })
const preTriggerFraction = defineModel<number>('preTriggerFraction', { required: true })
const mode = defineModel<ScopeRunMode>('mode', { required: true })
/**
 * The staged capture rate, read by the generator panel in the same rail: it decides whether the
 * analyzer could play what is composed there at all (384 kHz is capture-only on a QA403), and it
 * is the rate a generator-only stream should come up at. Kept in step with the form below, like
 * the record length and the pre-trigger fraction.
 */
const sampleRateHz = defineModel<number>('sampleRateHz', { required: true })

const props = defineProps<{
  running: boolean
  /** The analyzer's own rate list. Undefined until it has reported. */
  ranges?: DeviceRanges | null
  /**
   * The extremes of the record on screen, for the trigger's 50 % button — null with nothing
   * captured, which is when that button has nothing to read and is disabled.
   */
  measuredMaxV?: number | null
  measuredMinV?: number | null
  /**
   * What the generator panel in the same rail is staging, as a kind.
   *
   * <p>Only the stimulus trigger reads it, and it has to: a stimulus has a repeat to lock to only
   * where it was built to a whole number of frames, so the source and the stimulus are one setting
   * in two panels. Passed down rather than read from the store here, because the store is the
   * other panel's and a second reader of it is a second thing to keep in step.</p>
   */
  stimulusKind?: string | null
}>()

const emit = defineEmits<{
  run: [request: OscilloscopeStartRequest]
  retune: [request: OscilloscopeStartRequest]
}>()

const { markDirty } = useProjects()
const { captureDelayMs } = useCaptureDelay()

/** Mirrors `OscilloscopeRequest.MaxRecordSeconds` / `MinRecordSeconds`, which refuse anything outside. */
const MAX_RECORD_MS = 1000
const MIN_RECORD_MS = 0.02

/**
 * The record lengths the dropdown offers, in milliseconds.
 *
 * <p>A 1-2-5 ladder, which is how a scope's timebase knob is stepped, from the shortest record a
 * chart can draw anything on up to the one-second ceiling. The box beside it takes any figure in
 * between — the record length is a free time here rather than a knob position, because unlike a
 * real scope the timebase is a view <em>into</em> the record and so costs nothing to set oddly.</p>
 */
const RECORD_MS_LADDER = [0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000]

const form = reactive({
  sampleRateHz: 192000,
  /** In milliseconds, which is what the box shows; the request carries seconds. */
  recordMs: 20,
  preTriggerPercent: 25,
  processLeft: true,
  processRight: false,
  slope: DEFAULT_SCOPE_TRIGGER.slope as ScopeTriggerSlope,
  holdoffMs: 0,
  autoTimeoutMs: 500,
  /**
   * The width condition, and its one or two bounds in microseconds.
   *
   * <p>Microseconds because that is the scale this control is for: a crossover notch or a switching
   * glitch is tens of them, and one sample is 5.2 µs at 192 kS/s — a box in milliseconds would ask
   * for 0.042 and lose a decimal point. The panel's own sample-interval readout is in µs for the
   * same reason.</p>
   */
  pulseCondition: 'ShorterThan' as ScopePulseCondition,
  pulseWidthUs: 50,
  pulseWidthHiUs: 200,
  /**
   * The dropout timeout, in milliseconds.
   *
   * <p>Milliseconds rather than the widths' microseconds because it is a different scale: what this
   * is set for is an amplifier that mutes, an oscillator that stops or a connection that goes open,
   * none of which is measured in microseconds. 20 ms is twenty cycles of a 1 kHz tone — long enough
   * not to fire on an ordinary waveform's own quiet moments, short enough to catch a dropout a
   * listener would hear.</p>
   */
  dropoutMs: 20,
})

/** The rates this analyzer can capture at, newest-first as the device reports them. */
const sampleRates = computed(() => props.ranges?.sampleRatesHz ?? [48000, 96000, 192000])

/** How many frames the record comes to at the staged rate — what the badge quotes. */
const recordFrames = computed(() => Math.max(2, Math.round((form.recordMs / 1000) * form.sampleRateHz)))

/** The sample interval, which is the floor on anything this measurement can resolve in time. */
const sampleIntervalUs = computed(() => (form.sampleRateHz > 0 ? 1e6 / form.sampleRateHz : 0))

/**
 * How long one capture takes before the trigger is even armed: the stimulus latency the generator
 * change books plus the DUT's settle time.
 *
 * <p>The capture delay is read through its own setting rather than assumed, which is the rule every
 * runner's per-point timeout follows — a long settle otherwise reads as a wedged device.</p>
 */
const armDelayMs = computed(() => captureDelayMs.value)

/** What a single capture costs, end to end, at the staged settings. */
const captureMs = computed(() => armDelayMs.value + form.recordMs)

// Suppresses the dirty watcher while applyStoredParams writes the form — loading a type's saved
// settings is not an edit of them.
let restoring = false

function applyStoredParams(paramsJson: string) {
  let stored: Partial<OscilloscopeParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<OscilloscopeParameters>
  } catch {
    return
  }
  if (!stored || typeof stored !== 'object') return

  restoring = true
  if (typeof stored.sampleRateHz === 'number' && stored.sampleRateHz > 0) {
    form.sampleRateHz = stored.sampleRateHz
    sampleRateHz.value = stored.sampleRateHz
  }
  if (typeof stored.recordSeconds === 'number' && stored.recordSeconds > 0)
    form.recordMs = stored.recordSeconds * 1000
  if (typeof stored.preTriggerFraction === 'number')
    form.preTriggerPercent = Math.min(100, Math.max(0, stored.preTriggerFraction * 100))
  if (typeof stored.processLeft === 'boolean') form.processLeft = stored.processLeft
  if (typeof stored.processRight === 'boolean') form.processRight = stored.processRight
  if (stored.triggerSlope === 'Rising' || stored.triggerSlope === 'Falling' || stored.triggerSlope === 'Either')
    form.slope = stored.triggerSlope
  if (typeof stored.holdoffSeconds === 'number' && stored.holdoffSeconds >= 0)
    form.holdoffMs = stored.holdoffSeconds * 1000
  if (SCOPE_ACCUMULATIONS.some((a) => a.value === stored.accumulation))
    accumulation.value = stored.accumulation as ScopeAccumulation
  if (typeof stored.averageCount === 'number' && Number.isFinite(stored.averageCount))
    averageCount.value = clampAverageCount(stored.averageCount)

  // The models, written here too so a measurement restores as one thing rather than half from this
  // panel and half from App.vue.
  if (typeof stored.triggerLevelV === 'number' && Number.isFinite(stored.triggerLevelV))
    triggerLevelV.value = stored.triggerLevelV
  if (typeof stored.triggerHysteresisV === 'number' && stored.triggerHysteresisV >= 0)
    triggerHysteresisV.value = stored.triggerHysteresisV
  if (stored.triggerSource === 'ChannelA' || stored.triggerSource === 'ChannelB')
    triggerSource.value = stored.triggerSource
  if (SCOPE_TRIGGER_KINDS.some((k) => k.value === stored.triggerKind))
    triggerKind.value = stored.triggerKind as ScopeTriggerKind
  if (typeof stored.triggerLevelLowV === 'number' && Number.isFinite(stored.triggerLevelLowV))
    triggerLevelLowV.value = stored.triggerLevelLowV

  // The condition is read back from the pair of bounds rather than stored on its own, so there is
  // one statement of what the capture did and the panel cannot disagree with it. A measurement saved
  // as "shorter than 50 us" reopens saying that, not "between 0 and 50".
  if (typeof stored.dropoutSeconds === 'number' && stored.dropoutSeconds > 0)
    form.dropoutMs = stored.dropoutSeconds * 1000

  if (stored.triggerKind === 'PulseWidth') {
    const condition = pulseConditionFor(stored.pulseWidthLoSeconds, stored.pulseWidthHiSeconds)
    form.pulseCondition = condition
    const width = pulseWidthFor(
      condition,
      typeof stored.pulseWidthLoSeconds === 'number' ? stored.pulseWidthLoSeconds : 0,
      typeof stored.pulseWidthHiSeconds === 'number' ? stored.pulseWidthHiSeconds : null,
    )
    if (width > 0) form.pulseWidthUs = width * 1e6
    if (condition === 'Between' && typeof stored.pulseWidthHiSeconds === 'number' && stored.pulseWidthHiSeconds > 0)
      form.pulseWidthHiUs = stored.pulseWidthHiSeconds * 1e6
  }
  if (SCOPE_RUN_MODES.some((m) => m.value === stored.mode)) mode.value = stored.mode as ScopeRunMode
  if (typeof stored.recordSeconds === 'number' && stored.recordSeconds > 0)
    recordSeconds.value = stored.recordSeconds
  if (typeof stored.preTriggerFraction === 'number')
    preTriggerFraction.value = Math.min(1, Math.max(0, stored.preTriggerFraction))

  void Promise.resolve().then(() => {
    restoring = false
  })
}

const { types, currentTypeId } = useProjects()

watch(
  () => types.value.find((t) => t.id === currentTypeId.value)?.paramsJson ?? null,
  (paramsJson) => {
    if (paramsJson) applyStoredParams(paramsJson)
  },
  { immediate: true },
)

// The two models this panel writes derive from the form, so they are kept in step here rather than
// read back out of it — a record length that disagreed with the axis the chart draws would make an
// empty scope promise a window its next capture does not land in.
watch(
  () => form.recordMs,
  (ms) => {
    recordSeconds.value = ms / 1000
  },
)
watch(
  () => form.sampleRateHz,
  (hz) => {
    sampleRateHz.value = hz
  },
)
watch(
  () => form.preTriggerPercent,
  (percent) => {
    preTriggerFraction.value = Math.min(1, Math.max(0, percent / 100))
  },
)

watch(
  () =>
    [
      form,
      triggerLevelV.value,
      triggerHysteresisV.value,
      triggerSource.value,
      mode.value,
      // Both bound outside `form`, so they have to be listed by hand in BOTH watchers -- the rule
      // this project has already paid for twice. Missing from the retune one, switching Average
      // back to Normal left the analyzer still averaging: the picture went on reading "x16" with
      // the control saying Normal and nothing anywhere saying why. Missing from the dirty one, the
      // change is discarded on the next measurement switch without a word.
      accumulation.value,
      averageCount.value,
      // And so do these two, for the same reason: bound outside `form`, so a kind switched or a
      // second level typed would be discarded on the next measurement switch without a word.
      triggerKind.value,
      triggerLevelLowV.value,
    ] as const,
  () => {
    if (!restoring) markDirty()
  },
  { deep: true },
)

/** The 50 % button: the midpoint of what was actually measured. See TriggerAutoLevel.FiftyPercent. */
/**
 * True while the mode is Roll, where the trigger controls below have no effect at all.
 *
 * <p>They are disabled rather than hidden, which is the display rail's own answer for a channel
 * that is not being captured: hiding them takes away the only place a level can be typed before
 * switching back to a triggered mode, and leaving them live offers four controls that do nothing.</p>
 */
const rolling = computed(() => mode.value === 'Roll')

/**
 * Whether the records are combined, and over how many — see scope/acquisition.ts.
 *
 * <p>Measurement settings, not display ones: they decide what the analyzer <em>does</em> with each
 * capture, so they are stored with the measurement and sent on the run request.</p>
 */
const accumulation = defineModel<ScopeAccumulation>('accumulation', { default: 'Normal' })
const averageCount = defineModel<number>('averageCount', { default: DEFAULT_SCOPE_AVERAGE_COUNT })

/** Disabled under Single and Roll, which produce no records to combine. */
const canAccumulate = computed(() => modeCanAccumulate(mode.value))
const accumulationNote = computed(() => accumulationUnavailableNote(mode.value))

/**
 * How long the accumulation takes to settle, as a sentence, or null.
 *
 * <p>Said before the run rather than discovered during it: 256 records of the one-second capture
 * this app allows is over four minutes, and a display nobody waits for is one nobody believes.</p>
 */
const settleNote = computed(() => {
  const seconds = accumulationSettleSeconds(accumulation.value, averageCount.value, captureMs.value / 1000)
  if (seconds === null) return null
  return seconds >= 1 ? `${seconds.toFixed(1)} s to settle` : `${(seconds * 1000).toFixed(0)} ms to settle`
})

function onAverageCountChange(event: Event) {
  averageCount.value = clampAverageCount(Number((event.target as HTMLInputElement).value))
}

const canAutoLevel = computed(
  () => Number.isFinite(props.measuredMaxV ?? Number.NaN) && Number.isFinite(props.measuredMinV ?? Number.NaN),
)

function setFiftyPercent() {
  if (!canAutoLevel.value) return
  const max = props.measuredMaxV as number
  const min = props.measuredMinV as number
  // Rounded against the span it was taken from, the way a dragged level is rounded against the
  // window it was read from: the midpoint of two measured extremes carries every digit the
  // arithmetic produced, and "-0.0000049938025" in the box is a reading of the noise floor rather
  // than a level anybody set.
  triggerLevelV.value = roundVoltsForWindow((max + min) / 2, Math.abs(max - min))
}

/**
 * Ground.
 *
 * <p>The one level that needs no measurement — which is also why it is here beside the 50 % button
 * rather than instead of it: 50 % is disabled until something has been captured, so on an empty
 * scope, or after a drag has left the line somewhere odd, this is the only way back to a level
 * worth starting from. Zero is where an AC-coupled signal crosses, so on most benches it is where
 * the trigger belongs anyway.</p>
 */
function setZero() {
  triggerLevelV.value = 0
}

const sourceProblem = computed(() => triggerSourceProblem(triggerSource.value, form.processLeft, form.processRight))

/** Which extra controls the staged kind needs. */
const kindIsWindow = computed(() => triggerKind.value === 'Window')
const kindIsPulse = computed(() => triggerKind.value === 'PulseWidth')
const kindIsDropout = computed(() => triggerKind.value === 'Dropout')

/**
 * True while the trigger fires on the generator's own repeat.
 *
 * <p>Everything a trigger normally needs — the kind, the level, the slope, a width — is about the
 * <em>signal</em>, and this one reads none of it. The controls are hidden rather than disabled,
 * which is the opposite of what Roll does with the same controls and is right for the opposite
 * reason: Roll is a mode you come back from, so the level typed under it has to survive, while the
 * source is a choice about what the trigger <em>is</em> and the kind beside it would be a second
 * answer to that question. The server refuses the two together by name.</p>
 */
const sourceIsStimulus = computed(() => triggerIsStimulus(triggerSource.value))
const kindIsRunt = computed(() => triggerKind.value === 'Runt')

/**
 * The window's band and the runt's pair of thresholds are the same two numbers read two ways, so
 * one box serves both — with its own label, since a band and a pair of thresholds a pulse has to
 * reach are not the same idea.
 */
const needsSecondLevel = computed(() => kindIsWindow.value || kindIsRunt.value)
const secondLevelLabel = computed(() => (kindIsRunt.value ? 'Second threshold (V)' : 'Second level (V)'))

/**
 * The width condition as the two bounds the wire carries, in seconds.
 *
 * <p>One computed read by the request, the params and the validation, so the figure that is checked
 * is the figure that is sent.</p>
 */
const pulseBounds = computed(() =>
  pulseWidthBounds(form.pulseCondition, form.pulseWidthUs / 1e6, form.pulseWidthHiUs / 1e6),
)

const stimulusProblem = computed(() =>
  sourceIsStimulus.value ? stimulusSourceProblem(props.stimulusKind) : null,
)

const windowProblem = computed(() =>
  needsSecondLevel.value
    ? windowLevelsProblem(triggerLevelV.value, triggerLevelLowV.value, triggerKind.value)
    : null,
)

const dropoutProblemText = computed(() =>
  kindIsDropout.value ? dropoutProblem(form.dropoutMs / 1000) : null,
)

const pulseProblem = computed(() =>
  kindIsPulse.value
    ? pulseWidthProblem(
        form.pulseCondition,
        form.pulseWidthUs / 1e6,
        form.pulseWidthHiUs / 1e6,
        sampleIntervalUs.value / 1e6,
      )
    : null,
)

const invalid = computed(() => {
  if (!(form.recordMs >= MIN_RECORD_MS) || form.recordMs > MAX_RECORD_MS)
    return `The record must be between ${MIN_RECORD_MS} ms and ${MAX_RECORD_MS} ms.`
  if (!form.processLeft && !form.processRight) return 'At least one channel must be captured.'
  if (!Number.isFinite(triggerLevelV.value)) return 'The trigger level must be a voltage.'
  if (!(triggerHysteresisV.value >= 0)) return 'The hysteresis must be zero or a positive voltage.'
  if (!(form.holdoffMs >= 0)) return 'The holdoff must be zero or a positive time.'
  if (mode.value === 'Auto' && !(form.autoTimeoutMs > 0)) return 'Auto mode needs a timeout above zero.'
  // The stimulus problem first: under that source every other check is about a control the run
  // will not read.
  if (stimulusProblem.value) return stimulusProblem.value
  if (sourceIsStimulus.value) return sourceProblem.value

  return windowProblem.value ?? pulseProblem.value ?? dropoutProblemText.value ?? sourceProblem.value
})

/**
 * The form as an `OscilloscopeParameters`, for `measurement_type.params_json`.
 *
 * <p>Every field the record declares without a default is written: the server's deserializer binds
 * constructor parameters and treats a missing one as an error. The display half is added by App.vue
 * from <code>scopeDisplayToParams</code>, which is the one place that knows what the chart is
 * showing.</p>
 */
function buildParamsJson(): string {
  const parameters: OscilloscopeParameters = {
    sampleRateHz: form.sampleRateHz,
    recordSeconds: form.recordMs / 1000,
    preTriggerFraction: form.preTriggerPercent / 100,
    mode: mode.value,
    // Edge under the stimulus source, which is what the server takes: the source replaces the kind
    // rather than qualifying it, and sending the staged kind alongside would be refused.
    triggerKind: sourceIsStimulus.value ? 'Edge' : triggerKind.value,
    triggerSource: triggerSource.value,
    triggerSlope: form.slope,
    triggerLevelV: triggerLevelV.value,
    triggerHysteresisV: triggerHysteresisV.value,
    // Only the kind that reads each one, which is what the server's own save does: a stored window
    // level on an edge-triggered measurement would reopen showing a band the trigger never had.
    // Undefined rather than null, so JSON.stringify leaves the field out altogether.
    triggerLevelLowV: needsSecondLevel.value ? triggerLevelLowV.value : undefined,
    pulseWidthLoSeconds: kindIsPulse.value ? pulseBounds.value.loS : undefined,
    pulseWidthHiSeconds: kindIsPulse.value ? (pulseBounds.value.hiS ?? undefined) : undefined,
    dropoutSeconds: kindIsDropout.value ? form.dropoutMs / 1000 : undefined,
    holdoffSeconds: form.holdoffMs / 1000,
    processLeft: form.processLeft,
    processRight: form.processRight,
  }
  return JSON.stringify(parameters)
}

/** The staged settings as the server takes them. One builder, so a start and a retune agree. */
function buildRequest(single = false): OscilloscopeStartRequest {
  return {
    sampleRateHz: form.sampleRateHz,
    recordSeconds: form.recordMs / 1000,
    preTriggerFraction: form.preTriggerPercent / 100,
    mode: single ? 'Single' : mode.value,
    triggerKind: sourceIsStimulus.value ? 'Edge' : triggerKind.value,
    triggerSource: triggerSource.value,
    triggerSlope: form.slope,
    triggerLevelV: triggerLevelV.value,
    triggerHysteresisV: triggerHysteresisV.value,
    triggerLevelLowV: needsSecondLevel.value ? triggerLevelLowV.value : undefined,
    pulseWidthLoSeconds: kindIsPulse.value ? pulseBounds.value.loS : undefined,
    // Left out entirely where the condition has no upper bound: absent is "longer than", which is a
    // condition of its own rather than a missing figure -- and an infinity cannot ride on JSON.
    pulseWidthHiSeconds: kindIsPulse.value ? (pulseBounds.value.hiS ?? undefined) : undefined,
    dropoutSeconds: kindIsDropout.value ? form.dropoutMs / 1000 : undefined,
    holdoffSeconds: form.holdoffMs / 1000,
    autoTimeoutSeconds: form.autoTimeoutMs / 1000,
    // Normal where the staged mode cannot produce records to combine, so the Single button never
    // sends a combination the analyzer would refuse -- which would be a refusal the user did not
    // cause, from a button that has nothing to do with the setting.
    accumulation: single || !canAccumulate.value ? 'Normal' : accumulation.value,
    averageCount: clampAverageCount(averageCount.value),
    processLeft: form.processLeft,
    processRight: form.processRight,
  }
}

/** A run in whatever mode is staged, or in `Single` for the Single button. */
function run(single = false): void {
  if (invalid.value) return
  emit('run', buildRequest(single))
}

/**
 * How long a change waits before it is sent to a running capture.
 *
 * <p>Long enough that dragging the trigger line sends one request rather than one per pointer move,
 * short enough that a typed figure lands before the eye leaves the box. The drag's own release
 * flushes whatever is still owed, so the wait is never felt at the end of a gesture.</p>
 */
const RETUNE_DEBOUNCE_MS = 80

let retuneHandle: ReturnType<typeof setTimeout> | null = null

/**
 * Sends the staged settings to the capture already running, now.
 *
 * <p>Called on the release of a trigger-line drag, where waiting out the debounce would leave the
 * line and the capture disagreeing for a tenth of a second after the user has let go.</p>
 */
function flushRetune(): void {
  if (retuneHandle !== null) {
    clearTimeout(retuneHandle)
    retuneHandle = null
  }
  if (!props.running || invalid.value) return
  emit('retune', buildRequest())
}

/**
 * Every control takes effect where it is turned.
 *
 * <p>A scope is set up while it is running — the trigger level above all else, which is <em>set</em>
 * by dragging it — so a change that needed a stop and a start would be a change the user has to
 * work around. Nothing here restarts the run: <code>/retune</code> hands the settings to the
 * capture in flight, and the one setting that cannot be handed over, the sample rate, is answered
 * by App.vue restarting the run rather than by an error the user did not cause.</p>
 *
 * <p>Gated on `restoring` for the dirty watcher's reason: loading a type's saved settings is not a
 * turn of a knob. An invalid form sends nothing and the next valid change catches up.</p>
 */
watch(
  () =>
    [
      form,
      triggerLevelV.value,
      triggerHysteresisV.value,
      triggerSource.value,
      mode.value,
      // Both bound outside `form`, so they have to be listed by hand in BOTH watchers -- the rule
      // this project has already paid for twice. Missing from the retune one, switching Average
      // back to Normal left the analyzer still averaging: the picture went on reading "x16" with
      // the control saying Normal and nothing anywhere saying why. Missing from the dirty one, the
      // change is discarded on the next measurement switch without a word.
      accumulation.value,
      averageCount.value,
      triggerKind.value,
      triggerLevelLowV.value,
    ] as const,
  () => {
    if (restoring || !props.running) return
    if (retuneHandle !== null) clearTimeout(retuneHandle)
    retuneHandle = setTimeout(() => {
      retuneHandle = null
      flushRetune()
    }, RETUNE_DEBOUNCE_MS)
  },
  { deep: true },
)

defineExpose({ run, flushRetune, invalid, buildParamsJson })

/**
 * The two voltages are bound <code>:value</code> + <code>@change</code> and <b>not</b> with
 * <code>v-model</code>, which is not a style choice and must not be tidied into one.
 *
 * <p>A <code>defineModel</code> with a listener on it does not take the value assigned to it: it
 * emits, and the new value arrives as a prop on the parent's next render. <code>applyStoredParams</code>
 * assigns during <em>setup</em>, so this panel's first render still draws the old value — and
 * <code>vModelText</code> writes the element in a <b>mounted</b> directive hook, which Vue defers
 * to the post-flush queue. The update carrying the stored value is patched first and writes
 * <code>0.25</code> into the element; the deferred mount hook then runs with the binding from that
 * first render and puts <code>0</code> straight back. So a saved trigger level and hysteresis read
 * zero on the first selection of a measurement, with the chart's own trigger line sitting correctly
 * at the stored level beside them — reported 2026-11-17 and reproduced every time.</p>
 *
 * <p><code>vModelSelect</code> has an <code>updated</code> hook as well as a mounted one, and both
 * are deferred in order, so the update wins and Mode and Source restore correctly. That difference
 * is the whole of why two of the six controls were wrong and four were not.</p>
 *
 * <p>A plain <code>:value</code> binding is patched synchronously with the element's other props,
 * at mount and on every update, so there is no deferred hook to overwrite it. It is also what every
 * other number box in this panel and in the display rail already does.</p>
 */
function onLevelChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  if (Number.isFinite(typed)) triggerLevelV.value = typed
  else (event.target as HTMLInputElement).value = String(triggerLevelV.value)
}

function onSecondLevelChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  if (Number.isFinite(typed)) triggerLevelLowV.value = typed
  else (event.target as HTMLInputElement).value = String(triggerLevelLowV.value)
}

/**
 * A width box.
 *
 * <p>Clamped to a positive time on the way in, which is the one figure neither condition can mean:
 * a pulse of zero width is not a pulse. <code>clampNumberField</code> for the reason every number
 * box in this app uses it — the step grid is anchored on <code>min</code>, so a floor that is not a
 * multiple of the step makes the spinner offer absurd values, and the floor has to live here
 * instead.</p>
 */
function onPulseWidthChange(event: Event) {
  form.pulseWidthUs = readWidthUs(event, form.pulseWidthUs)
}

function onPulseWidthHiChange(event: Event) {
  form.pulseWidthHiUs = readWidthUs(event, form.pulseWidthHiUs)
}

/** A typed width, clamped to a positive time, with the box put back where it cannot be read. */
function readWidthUs(event: Event, current: number): number {
  const box = event.target as HTMLInputElement
  const typed = Number(box.value)
  if (!Number.isFinite(typed)) {
    box.value = String(current)
    return current
  }

  const clamped = clampNumberField(typed, 0.01)
  box.value = String(clamped)
  return clamped
}

function onDropoutChange(event: Event) {
  form.dropoutMs = readWidthUs(event, form.dropoutMs)
}

function onHysteresisChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed)) {
    ;(event.target as HTMLInputElement).value = String(triggerHysteresisV.value)
    return
  }
  // A negative band is the one figure this control cannot mean; clamped rather than refused, so the
  // spinner stops at zero instead of putting the run's own refusal on screen.
  triggerHysteresisV.value = clampNumberField(typed, 0)
  ;(event.target as HTMLInputElement).value = String(triggerHysteresisV.value)
}

function onRecordChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  form.recordMs = clampNumberField(typed, MIN_RECORD_MS, MAX_RECORD_MS)
  ;(event.target as HTMLInputElement).value = String(form.recordMs)
}

function onPreTriggerChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  form.preTriggerPercent = clampNumberField(typed, 0, 100)
  ;(event.target as HTMLInputElement).value = String(form.preTriggerPercent)
}

function onHoldoffChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  form.holdoffMs = clampNumberField(typed, 0, MAX_RECORD_MS * 10)
  ;(event.target as HTMLInputElement).value = String(form.holdoffMs)
}

function onAutoTimeoutChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  form.autoTimeoutMs = clampNumberField(typed, 10, 60000)
  ;(event.target as HTMLInputElement).value = String(form.autoTimeoutMs)
}
</script>

<template>
  <CollapsibleSection title="Acquisition">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Sample rate
        <select v-model.number="form.sampleRateHz" class="tb-input" :disabled="running">
          <option v-for="hz in sampleRates" :key="hz" :value="hz">{{ (hz / 1000).toFixed(0) }} kS/s</option>
        </select>
      </label>
      <label class="tb-field">
        Record (ms)
        <input
          :value="form.recordMs"
          type="number"
          step="1"
          :max="MAX_RECORD_MS"
          :list="'scope-record-ladder'"
          class="tb-input"
          :disabled="running"
          title="The whole record, pre- and post-trigger together. Up to one second; longer captures are the stream recorder's job."
          @change="onRecordChange"
        />
        <datalist id="scope-record-ladder">
          <option v-for="ms in RECORD_MS_LADDER" :key="ms" :value="ms"></option>
        </datalist>
      </label>
    </div>

    <label class="tb-field">
      Pre-trigger (%)
      <input
        :value="form.preTriggerPercent"
        type="number"
        step="5"
        max="100"
        class="tb-input"
        :disabled="running"
        title="How much of the record sits before the trigger. 100 % is a whole record of history — free here, because the analyzer streams continuously and the trigger is a window chosen out of it."
        @change="onPreTriggerChange"
      />
    </label>

    <div class="tb-field">
      <span class="tb-group">Channels</span>
      <label class="tb-check-row">
        <input v-model="form.processLeft" type="checkbox" class="tb-check" :disabled="running" /> Left
      </label>
      <label class="tb-check-row">
        <input v-model="form.processRight" type="checkbox" class="tb-check" :disabled="running" /> Right
      </label>
    </div>

    <div class="flex flex-wrap gap-1.5">
      <EstimateBadge kind="points" title="How many samples the record holds at the staged rate.">
        {{ recordFrames.toLocaleString() }} samples
      </EstimateBadge>
      <EstimateBadge kind="time" title="One sample interval - the floor on anything this measurement can resolve in time.">
        {{ formatScopeTime(sampleIntervalUs / 1000, 0.05) }} / sample
      </EstimateBadge>
      <EstimateBadge kind="time" title="One capture end to end: the settle the stimulus change books, plus the record itself.">
        {{ formatScopeTime(captureMs, captureMs) }} / capture
      </EstimateBadge>
    </div>
    <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      Nothing faster than about 4 µs can be read off an edge this analyzer produced — that is its own
      reconstruction filter, not the DUT. See the manual's Oscilloscope page.
    </p>
  </CollapsibleSection>

  <CollapsibleSection title="Trigger">
    <label class="tb-field">
      Mode
      <select v-model="mode" class="tb-input" :title="SCOPE_RUN_MODE_HINTS[mode]">
        <option v-for="option in SCOPE_RUN_MODES" :key="option.value" :value="option.value">{{ option.label }}</option>
      </select>
    </label>

    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Records
        <select
          v-model="accumulation"
          class="tb-input"
          :disabled="!canAccumulate"
          :title="SCOPE_ACCUMULATION_HINTS[accumulation]"
        >
          <option v-for="option in SCOPE_ACCUMULATIONS" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
      </label>
      <label v-if="accumulation === 'Average'" class="tb-field">
        Average over
        <input
          :value="averageCount"
          type="number"
          step="1"
          :max="MAX_SCOPE_AVERAGE_COUNT"
          class="tb-input"
          :disabled="!canAccumulate"
          :title="`How many records the mean is of, ${MIN_SCOPE_AVERAGE_COUNT} to ${MAX_SCOPE_AVERAGE_COUNT}. The noise falls as the square root of this.`"
          @change="onAverageCountChange"
        />
      </label>
    </div>

    <p v-if="accumulationNote" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      {{ accumulationNote }}
    </p>
    <p v-else-if="settleNote" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      {{ settleNote }}
    </p>

    <!--
      Roll has no trigger at all, so everything below is disabled rather than hidden — the
      display rail's own rule for a channel that is not being captured. Hiding them would take
      away the only place a level can be typed before switching back to a triggered mode, and
      leaving them live would offer four controls that do nothing.
    -->
    <p v-if="rolling" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      Roll draws the newest samples as they arrive, with t&nbsp;=&nbsp;0 at the right-hand edge.
      Nothing below has any effect until you pick Auto, Normal or Single.
    </p>

    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Source
        <select
          v-model="triggerSource"
          class="tb-input"
          :disabled="rolling"
          :title="SCOPE_TRIGGER_SOURCE_HINTS[triggerSource]"
        >
          <option v-for="option in SCOPE_TRIGGER_SOURCES" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
      </label>
      <!--
        Hidden rather than disabled under the stimulus source, which is the opposite of what Roll
        does with these controls and is right for the opposite reason: Roll is a mode you come back
        from, so what was typed under it has to survive, while the source is a choice about what the
        trigger IS and a kind beside it would be a second answer to the same question.
      -->
      <label v-if="!sourceIsStimulus" class="tb-field">
        Kind
        <select v-model="triggerKind" class="tb-input" :disabled="rolling" :title="SCOPE_TRIGGER_KIND_HINTS[triggerKind]">
          <option v-for="option in SCOPE_TRIGGER_KINDS" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
      </label>
    </div>

    <p
      v-if="sourceIsStimulus || triggerKind !== 'Edge'"
      class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400"
    >
      {{ sourceIsStimulus ? SCOPE_TRIGGER_SOURCE_HINTS.Stimulus : SCOPE_TRIGGER_KIND_HINTS[triggerKind] }}
    </p>

    <label v-if="!sourceIsStimulus" class="tb-field">
      Slope
      <!--
        The word is the same for every kind and the thing it selects is not: on a pulse-width
        trigger it decides which stretch IS the pulse, and on a window trigger whether entering or
        leaving fires. Hence the title rather than three dropdowns.
      -->
      <select v-model="form.slope" class="tb-input" :disabled="rolling" :title="SCOPE_TRIGGER_SLOPE_HINTS[triggerKind]">
        <option v-for="option in SCOPE_TRIGGER_SLOPES" :key="option.value" :value="option.value">
          {{ option.label }}
        </option>
      </select>
    </label>

    <!-- The level's box is the precise fallback; the line on the chart is how it is normally set. -->
    <label v-if="!sourceIsStimulus" class="tb-field">
      Level (V)
      <div class="flex items-center gap-1.5">
        <input
          :value="triggerLevelV"
          type="number"
          step="0.01"
          class="tb-input w-24 shrink-0"
          :disabled="rolling"
          title="Drag the dashed line on the chart to set this. The box is here for a figure you already know."
          @change="onLevelChange"
        />
        <button
          type="button"
          class="btn-chip shrink-0"
          :disabled="rolling"
          title="Zero volts, which needs nothing to have been captured — where an AC-coupled signal crosses, and the way back after a drag has left the line somewhere odd."
          @click="setZero"
        >
          0 V
        </button>
        <button
          type="button"
          class="btn-chip shrink-0"
          :disabled="rolling || !canAutoLevel"
          title="Half way between the extremes of the record on screen — the midpoint, not the mean, so an uneven duty cycle still puts the level on the edge."
          @click="setFiftyPercent"
        >
          50 %
        </button>
      </div>
    </label>

    <!--
      The window's other bound. Beside the level rather than inside it, because the two are a pair
      of bounds in either order and the chart draws the band between them whichever way round they
      are typed.
    -->
    <label v-if="needsSecondLevel && !sourceIsStimulus" class="tb-field">
      {{ secondLevelLabel }}
      <input
        :value="triggerLevelLowV"
        type="number"
        step="0.01"
        class="tb-input"
        :disabled="rolling"
        title="The band's other bound. The order does not matter — the chart and the capture both sort the pair."
        @change="onSecondLevelChange"
      />
    </label>

    <label v-if="kindIsDropout && !sourceIsStimulus" class="tb-field">
      No edge within (ms)
      <input
        :value="form.dropoutMs"
        type="number"
        step="1"
        class="tb-input"
        :disabled="rolling"
        title="How long an absence of qualifying edges has to last before the analyzer takes a record. Milliseconds: this is for a signal that stops, not for a glitch."
        @change="onDropoutChange"
      />
    </label>

    <template v-if="kindIsPulse && !sourceIsStimulus">
      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          Condition
          <select v-model="form.pulseCondition" class="tb-input" :disabled="rolling">
            <option v-for="option in SCOPE_PULSE_CONDITIONS" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
        </label>
        <label class="tb-field">
          {{ form.pulseCondition === 'Between' ? 'From (µs)' : 'Width (µs)' }}
          <input
            :value="form.pulseWidthUs"
            type="number"
            step="1"
            class="tb-input"
            :disabled="rolling"
            :title="`Microseconds. One sample is ${sampleIntervalUs.toFixed(2)} µs at this rate, which is the narrowest pulse any record can hold.`"
            @change="onPulseWidthChange"
          />
        </label>
      </div>
      <label v-if="form.pulseCondition === 'Between'" class="tb-field">
        To (µs)
        <input
          :value="form.pulseWidthHiUs"
          type="number"
          step="1"
          class="tb-input"
          :disabled="rolling"
          title="The longer bound of the range that qualifies."
          @change="onPulseWidthHiChange"
        />
      </label>
      <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        {{
          form.pulseCondition === 'LongerThan'
            ? 'Fires as soon as the pulse has lasted this long, without waiting for it to end — so a level that has stuck still triggers.'
            : 'Fires on the pulse’s trailing edge, where its width becomes known.'
        }}
      </p>
    </template>

    <div class="grid grid-cols-2 gap-2.5">
      <label v-if="!sourceIsStimulus" class="tb-field">
        Hysteresis (V)
        <input
          :value="triggerHysteresisV"
          type="number"
          step="0.01"
          class="tb-input"
          :disabled="rolling"
          title="The noise-reject band, drawn around the level on the chart. Raise it when the trace will not stand still."
          @change="onHysteresisChange"
        />
      </label>
      <label class="tb-field">
        Holdoff (ms)
        <input
          :value="form.holdoffMs"
          type="number"
          step="1"
          class="tb-input"
          :disabled="rolling"
          title="How long after a capture the trigger refuses to fire again — for a signal with several edges per cycle."
          @change="onHoldoffChange"
        />
      </label>
    </div>

    <label v-if="mode === 'Auto'" class="tb-field">
      Auto timeout (ms)
      <input
        :value="form.autoTimeoutMs"
        type="number"
        step="50"
        class="tb-input"
        title="How long Auto waits for a crossing before drawing an untriggered record anyway. That record is marked on the chart, because a trace aligned to nothing must not look like one that was triggered."
        @change="onAutoTimeoutChange"
      />
    </label>

    <p v-if="invalid" class="text-xs text-red-600 dark:text-red-400">{{ invalid }}</p>
  </CollapsibleSection>
</template>
