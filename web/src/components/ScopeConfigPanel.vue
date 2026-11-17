<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import type {
  DeviceRanges,
  OscilloscopeParameters,
  OscilloscopeStartRequest,
  ScopeRunMode,
  ScopeTriggerSlope,
  ScopeTriggerSource,
} from '../api/client'
import { useProjects } from '../composables/useProjects'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import CollapsibleSection from './CollapsibleSection.vue'
import EstimateBadge from './EstimateBadge.vue'
import { clampNumberField } from '../numberField'
import {
  DEFAULT_SCOPE_TRIGGER,
  SCOPE_RUN_MODES,
  SCOPE_RUN_MODE_HINTS,
  SCOPE_TRIGGER_SLOPES,
  SCOPE_TRIGGER_SOURCES,
  triggerSourceProblem,
} from '../scope/triggerSpec'
import { formatScopeTime } from '../scope/timeAxis'

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
const triggerSource = defineModel<ScopeTriggerSource>('triggerSource', { required: true })
const recordSeconds = defineModel<number>('recordSeconds', { required: true })
const preTriggerFraction = defineModel<number>('preTriggerFraction', { required: true })
const mode = defineModel<ScopeRunMode>('mode', { required: true })

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
}>()

const emit = defineEmits<{
  run: [request: OscilloscopeStartRequest]
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
  if (typeof stored.sampleRateHz === 'number' && stored.sampleRateHz > 0) form.sampleRateHz = stored.sampleRateHz
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

  // The models, written here too so a measurement restores as one thing rather than half from this
  // panel and half from App.vue.
  if (typeof stored.triggerLevelV === 'number' && Number.isFinite(stored.triggerLevelV))
    triggerLevelV.value = stored.triggerLevelV
  if (typeof stored.triggerHysteresisV === 'number' && stored.triggerHysteresisV >= 0)
    triggerHysteresisV.value = stored.triggerHysteresisV
  if (stored.triggerSource === 'ChannelA' || stored.triggerSource === 'ChannelB')
    triggerSource.value = stored.triggerSource
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
  () => form.preTriggerPercent,
  (percent) => {
    preTriggerFraction.value = Math.min(1, Math.max(0, percent / 100))
  },
)

watch(
  () => [form, triggerLevelV.value, triggerHysteresisV.value, triggerSource.value, mode.value] as const,
  () => {
    if (!restoring) markDirty()
  },
  { deep: true },
)

/** The 50 % button: the midpoint of what was actually measured. See TriggerAutoLevel.FiftyPercent. */
const canAutoLevel = computed(
  () => Number.isFinite(props.measuredMaxV ?? Number.NaN) && Number.isFinite(props.measuredMinV ?? Number.NaN),
)

function setFiftyPercent() {
  if (!canAutoLevel.value) return
  triggerLevelV.value = ((props.measuredMaxV as number) + (props.measuredMinV as number)) / 2
}

const sourceProblem = computed(() => triggerSourceProblem(triggerSource.value, form.processLeft, form.processRight))

const invalid = computed(() => {
  if (!(form.recordMs >= MIN_RECORD_MS) || form.recordMs > MAX_RECORD_MS)
    return `The record must be between ${MIN_RECORD_MS} ms and ${MAX_RECORD_MS} ms.`
  if (!form.processLeft && !form.processRight) return 'At least one channel must be captured.'
  if (!Number.isFinite(triggerLevelV.value)) return 'The trigger level must be a voltage.'
  if (!(triggerHysteresisV.value >= 0)) return 'The hysteresis must be zero or a positive voltage.'
  if (!(form.holdoffMs >= 0)) return 'The holdoff must be zero or a positive time.'
  if (mode.value === 'Auto' && !(form.autoTimeoutMs > 0)) return 'Auto mode needs a timeout above zero.'
  return sourceProblem.value
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
    triggerKind: 'Edge',
    triggerSource: triggerSource.value,
    triggerSlope: form.slope,
    triggerLevelV: triggerLevelV.value,
    triggerHysteresisV: triggerHysteresisV.value,
    holdoffSeconds: form.holdoffMs / 1000,
    processLeft: form.processLeft,
    processRight: form.processRight,
  }
  return JSON.stringify(parameters)
}

/** A run in whatever mode is staged, or in `Single` for the Single button. */
function run(single = false): void {
  if (invalid.value) return
  emit('run', {
    sampleRateHz: form.sampleRateHz,
    recordSeconds: form.recordMs / 1000,
    preTriggerFraction: form.preTriggerPercent / 100,
    mode: single ? 'Single' : mode.value,
    triggerKind: 'Edge',
    triggerSource: triggerSource.value,
    triggerSlope: form.slope,
    triggerLevelV: triggerLevelV.value,
    triggerHysteresisV: triggerHysteresisV.value,
    holdoffSeconds: form.holdoffMs / 1000,
    autoTimeoutSeconds: form.autoTimeoutMs / 1000,
    processLeft: form.processLeft,
    processRight: form.processRight,
  })
}

defineExpose({ run, invalid, buildParamsJson })

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
        Source
        <select v-model="triggerSource" class="tb-input">
          <option v-for="option in SCOPE_TRIGGER_SOURCES" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
      </label>
      <label class="tb-field">
        Slope
        <select v-model="form.slope" class="tb-input">
          <option v-for="option in SCOPE_TRIGGER_SLOPES" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
      </label>
    </div>

    <!-- The level's box is the precise fallback; the line on the chart is how it is normally set. -->
    <label class="tb-field">
      Level (V)
      <div class="flex gap-1.5">
        <input
          v-model.number="triggerLevelV"
          type="number"
          step="0.01"
          class="tb-input flex-1"
          title="Drag the dashed line on the chart to set this. The box is here for a figure you already know."
        />
        <button
          type="button"
          class="btn-chip shrink-0"
          :disabled="!canAutoLevel"
          title="Half way between the extremes of the record on screen — the midpoint, not the mean, so an uneven duty cycle still puts the level on the edge."
          @click="setFiftyPercent"
        >
          50 %
        </button>
      </div>
    </label>

    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Hysteresis (V)
        <input
          v-model.number="triggerHysteresisV"
          type="number"
          step="0.01"
          class="tb-input"
          title="The noise-reject band, drawn around the level on the chart. Raise it when the trace will not stand still."
        />
      </label>
      <label class="tb-field">
        Holdoff (ms)
        <input
          :value="form.holdoffMs"
          type="number"
          step="1"
          class="tb-input"
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
