<script setup lang="ts">
import { computed, watch } from 'vue'
import CollapsibleSection from './CollapsibleSection.vue'
import AnnotationsPanel from './AnnotationsPanel.vue'
import { OSCILLOSCOPE_ANNOTATION_VIEW } from '../charts/annotationViews'
import { useOscilloscope } from '../composables/useOscilloscope'
import { recordExtremes } from '../scope/measurements'
import {
  VOLTS_PER_DIVISION_LADDER,
  fitVoltWindow,
  formatVoltsPerDivision,
  snapVoltsPerDivision,
} from '../scope/voltAxis'
import { clampWindow, fromChartMs, recordWindow, scopeTimeScale, toChartMs } from '../scope/timeAxis'

/**
 * The oscilloscope chart's display rail: the vertical pair, the time window, one Autofit for both,
 * and the style toggles.
 *
 * <p><b>The vertical pair is V/div and a centre, not a top and a bottom</b> — see
 * <code>scope/voltAxis.ts</code>. A scope is read by counting squares, so a division has to be a
 * round number; the two ends of the window are derived from the pair rather than typed.</p>
 */

const voltsPerDivision = defineModel<number>('voltsPerDivision', { required: true })
const voltOffsetV = defineModel<number>('voltOffsetV', { required: true })
const timeFromS = defineModel<number>('timeFromS', { required: true })
const timeToS = defineModel<number>('timeToS', { required: true })
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
const showTriggerLevel = defineModel<boolean>('showTriggerLevel', { required: true })
const autofitOnDone = defineModel<boolean>('autofitOnDone', { required: true })

const props = defineProps<{
  /** The record the settings ask for, so the window can be fitted before anything is captured. */
  recordSeconds: number
  preTriggerFraction: number
}>()

const { record, recordBounds, running } = useOscilloscope()

/**
 * The bounds a window is clamped to: the record's own span, or — before anything is captured — the
 * span the settings ask for.
 *
 * <p>The settings' span is the right fallback rather than "no bounds at all": an empty scope's axis
 * is where its next capture will land, so a window typed now is checked against the same record it
 * will be read on.</p>
 */
const bounds = computed(
  () => recordBounds.value ?? recordWindow(props.recordSeconds, props.preTriggerFraction),
)

/**
 * The unit the two time boxes are typed in.
 *
 * <p>Taken from the <b>record</b> and not from the window on screen, which is the one choice here
 * that is not obvious: a unit that followed the zoom would change under the user's fingers while
 * they typed a figure into it. The record is fixed for the length of a measurement, so the boxes
 * stay in one unit while the axis labels and the readouts follow the window — which is what those
 * are for.</p>
 */
const boxScale = computed(() => scopeTimeScale(toChartMs(props.recordSeconds)))

/** A stored second as the box shows it, and back. */
function toBox(seconds: number): number {
  const value = toChartMs(seconds) / boxScale.value.msPerUnit
  // Four figures: finer than the box's own step and short enough to read.
  return Number(value.toPrecision(6))
}

function fromBox(value: number): number {
  return fromChartMs(value * boxScale.value.msPerUnit)
}

const fromBoxValue = computed(() => toBox(timeFromS.value))
const toBoxValue = computed(() => toBox(timeToS.value))

/** One step of the boxes: a hundredth of the record, which is about a tenth of a division. */
const boxStep = computed(() => {
  const span = toChartMs(props.recordSeconds) / boxScale.value.msPerUnit
  return Number((span / 100).toPrecision(1)) || 0.001
})

/**
 * Both ends are settled as a pair and only then written.
 *
 * <p>A <code>defineModel</code> with a listener on it does not read back the value just assigned —
 * it emits and the new value arrives as a prop on the parent's next render — so settling one end
 * against the other by reading it back hands out the window being replaced. That is the
 * <code>fitDbWindow</code> lesson, and it is reachable here by typing a From above the current To.</p>
 */
function commitWindow(fromS: number, toS: number) {
  const clamped = clampWindow({ fromS, toS }, bounds.value)
  timeFromS.value = clamped.fromS
  timeToS.value = clamped.toS
}

function onFromChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed)) {
    ;(event.target as HTMLInputElement).value = String(fromBoxValue.value)
    return
  }
  commitWindow(fromBox(typed), timeToS.value)
}

function onToChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed)) {
    ;(event.target as HTMLInputElement).value = String(toBoxValue.value)
    return
  }
  commitWindow(timeFromS.value, fromBox(typed))
}

function onOffsetChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  if (Number.isFinite(typed)) voltOffsetV.value = typed
  else (event.target as HTMLInputElement).value = String(voltOffsetV.value)
}

/** The extremes of the record on screen — exact even on a decimated slice. See scope/measurements.ts. */
const extremes = computed(() => recordExtremes(record.value?.channels))

function autofitVolts() {
  const measured = extremes.value
  if (!measured) return
  const fit = fitVoltWindow(measured.maxV, measured.minV)
  voltsPerDivision.value = fit.voltsPerDivision
  voltOffsetV.value = fit.offsetV
}

/** The whole record, which is what a scope's horizontal autoset lands on. */
function autofitTime() {
  const span = bounds.value
  commitWindow(span.fromS, span.toS)
}

/**
 * One Autofit per chart, not one per axis. The time window is fitted first, so the vertical fit is
 * taken over the record the axis is about to show rather than over whatever the old window held.
 */
function autofitAll() {
  autofitTime()
  autofitVolts()
}

watch(running, (isRunning, wasRunning) => {
  if (wasRunning && !isRunning && autofitOnDone.value) autofitAll()
})

/**
 * In <b>Auto</b> and <b>Normal</b> the run never ends, so the watcher above would never fire — and
 * a scope whose Autofit only works on a Single capture is a scope whose Autofit does not work. The
 * first record of a run fits instead, and only the first: re-fitting on every capture would make
 * the graticule hunt while the signal was being adjusted, which is the opposite of what a V/div
 * control is for.
 */
let fittedForRun = -1
watch(
  () => record.value,
  (next) => {
    if (!next || !autofitOnDone.value) return
    if (fittedForRun >= 0) return
    fittedForRun = 0
    autofitAll()
  },
)
watch(running, (isRunning) => {
  if (isRunning) fittedForRun = -1
})

const verticalLabel = computed(() => formatVoltsPerDivision(voltsPerDivision.value))

/** The window's own reading, so a zoom can be seen in the rail as well as on the chart. */
const windowSpan = computed(() => {
  const span = Math.abs(timeToS.value - timeFromS.value)
  const scale = scopeTimeScale(toChartMs(span))
  return `${(toChartMs(span) / scale.msPerUnit).toPrecision(3)} ${scale.label}`
})

/** Snapped on the way in, so a window arriving from a drag-zoom's "Set axis" lands on a rung. */
function onVoltsPerDivisionChange(event: Event) {
  voltsPerDivision.value = snapVoltsPerDivision(Number((event.target as HTMLSelectElement).value))
}
</script>

<template>
  <!-- The chart's one Autofit, at the top of the rail and in no section at all — the same place on
       every screen. -->
  <button
    type="button"
    class="btn-panel-accent"
    title="Fits both axes to the record on screen: the time window to the whole record, the V/div and centre to the trace inside it."
    @click="autofitAll"
  >
    Autofit
  </button>

  <CollapsibleSection title="Vertical" size="xs">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        V/div
        <select :value="voltsPerDivision" class="tb-input" @change="onVoltsPerDivisionChange">
          <option v-for="rung in VOLTS_PER_DIVISION_LADDER" :key="rung" :value="rung">
            {{ formatVoltsPerDivision(rung).replace('/div', '') }}
          </option>
        </select>
      </label>
      <label class="tb-field">
        Centre (V)
        <input
          :value="voltOffsetV"
          type="number"
          step="0.1"
          class="tb-input"
          title="Where the middle of the graticule sits. A DUT with a DC offset is brought back on screen with this rather than by widening the V/div."
          @change="onOffsetChange"
        />
      </label>
    </div>
    <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      Eight divisions, so the window is {{ verticalLabel }} × 8 centred on the figure beside it.
    </p>
  </CollapsibleSection>

  <CollapsibleSection :title="`Time window (${boxScale.label})`" size="xs">
    <!-- Both ends relative to the trigger at t = 0, negative to its left — the same convention the
         decay surfaces' head start uses, and the one the server's own fromS/toS carry. -->
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        From
        <input
          :value="fromBoxValue"
          type="number"
          :step="boxStep"
          class="tb-input"
          title="Negative is before the trigger."
          @change="onFromChange"
        />
      </label>
      <label class="tb-field">
        To
        <input :value="toBoxValue" type="number" :step="boxStep" class="tb-input" @change="onToChange" />
      </label>
    </div>
    <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      {{ windowSpan }} over ten divisions. Tighten it far enough and the chart draws every sample
      instead of a min/max column pair.
    </p>
  </CollapsibleSection>

  <CollapsibleSection title="Display">
    <label class="tb-check-row">
      <input v-model="autofitOnDone" type="checkbox" class="tb-check" /> Autofit when measurement done
    </label>
    <label class="tb-check-row">
      <input v-model="showTriggerLevel" type="checkbox" class="tb-check" /> Trigger level
    </label>
    <div class="tb-field">
      <span class="tb-group">Style</span>
      <label class="tb-check-row"><input v-model="thickLines" type="checkbox" class="tb-check" /> Thick lines</label>
      <label class="tb-check-row">
        <input v-model="showDataPoints" type="checkbox" class="tb-check" /> Show data points
      </label>
    </div>
    <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      Data points are drawn only where the window is tight enough for the chart to hold real samples
      — a marker on a min/max column would mark the column, not a sample.
    </p>
  </CollapsibleSection>

  <AnnotationsPanel :view="OSCILLOSCOPE_ANNOTATION_VIEW" />
</template>
