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
  voltWindow,
} from '../scope/voltAxis'
import { clampWindow, fromChartMs, recordWindow, scopeTimeScale, toChartMs } from '../scope/timeAxis'
import { cursorPairNeedsPlacing, cursorReadings, placeCursorPair, type ScopeCursors } from '../scope/cursors'

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
const showLeft = defineModel<boolean>('showLeft', { required: true })
const showRight = defineModel<boolean>('showRight', { required: true })
const showResidual = defineModel<boolean>('showResidual', { required: true })

/** The cursors. See scope/cursors.ts — session state, so they are not in <code>params_json</code>. */
const cursors = defineModel<ScopeCursors>('cursors', { required: true })

const emit = defineEmits<{
  /**
   * A cursor has been moved, or switched on or off: the parent gates the measurements once.
   *
   * <p>Emitted from the control rather than watched upstream, for the chart's own reason — the gate
   * is a fetch, and a watcher would ask the server for a window per keystroke.</p>
   */
  cursorsCommitted: []
}>()

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

/**
 * Which inputs the record on hand carries — what the measurement is capturing.
 *
 * <p>A channel the Acquisition panel is not capturing has nothing to show, so its box here says so
 * and is disabled rather than being a tick that does nothing. Before the first capture there is no
 * record to ask, and both are offered.</p>
 */
const captured = computed(() => {
  const channels = record.value?.channels
  if (!channels?.length) return { left: true, right: true }
  return {
    left: channels.some((c) => c.channel === 0),
    right: channels.some((c) => c.channel === 1),
  }
})

/**
 * The extremes of what is <b>drawn</b> — exact even on a decimated slice. See scope/measurements.ts.
 *
 * <p>Only the visible channels, because this is what Autofit frames: fitting a hidden trace would
 * leave the one on screen in a corner of a window sized for something the reader cannot see.</p>
 */
const extremes = computed(() =>
  recordExtremes(
    (record.value?.channels ?? []).filter((slice) => (slice.channel === 1 ? showRight.value : showLeft.value)),
  ),
)

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


// ---------------------------------------------------------------------------
// Cursors
// ---------------------------------------------------------------------------

/**
 * Switching a pair on, which also <b>places it where it can be seen</b>.
 *
 * <p>The defaults are both cursors at zero, and a pair left over from another measurement may be
 * off this record entirely — either way there is nothing to read and nothing to grab. See
 * <code>cursorPairNeedsPlacing</code>; the placement happens only on the way <em>on</em>, so a pair
 * the reader has put somewhere is never moved for them.</p>
 */
function toggleTimeCursors(on: boolean) {
  const next: ScopeCursors = { ...cursors.value, timeOn: on }
  if (on && cursorPairNeedsPlacing(next.timeAS, next.timeBS, timeFromS.value, timeToS.value)) {
    const placed = placeCursorPair(timeFromS.value, timeToS.value)
    next.timeAS = placed.a
    next.timeBS = placed.b
  }
  cursors.value = next
  emit('cursorsCommitted')
}

function toggleVoltCursors(on: boolean) {
  const next: ScopeCursors = { ...cursors.value, voltOn: on }
  const level = voltWindow(voltsPerDivision.value, voltOffsetV.value)
  if (on && cursorPairNeedsPlacing(next.voltAV, next.voltBV, level.bottomV, level.topV)) {
    const placed = placeCursorPair(level.bottomV, level.topV)
    next.voltAV = placed.a
    next.voltBV = placed.b
  }
  cursors.value = next
  emit('cursorsCommitted')
}

/** A cursor written from a box: replaced, never mutated, and committed, a typed figure being a commit. */
function setCursor(field: 'timeAS' | 'timeBS' | 'voltAV' | 'voltBV', value: number) {
  cursors.value = { ...cursors.value, [field]: value }
  emit('cursorsCommitted')
}

/** The time boxes are in the window boxes' own unit, so the four figures can be compared by eye. */
const cursorABoxValue = computed(() => toBox(cursors.value.timeAS))
const cursorBBoxValue = computed(() => toBox(cursors.value.timeBS))

function onCursorTimeChange(event: Event, field: 'timeAS' | 'timeBS') {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed)) {
    ;(event.target as HTMLInputElement).value = String(
      field === 'timeAS' ? cursorABoxValue.value : cursorBBoxValue.value,
    )
    return
  }
  // Not clamped to the window, deliberately: a cursor off the window is drawn nowhere and still
  // reads its own position, and clamping would silently move a pair a zoom is about to bring back.
  setCursor(field, fromBox(typed))
}

function onCursorVoltChange(event: Event, field: 'voltAV' | 'voltBV') {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed)) {
    ;(event.target as HTMLInputElement).value = String(cursors.value[field])
    return
  }
  setCursor(field, typed)
}

/** One press of a level cursor's spinner: a tenth of a division, which is how finely one is placed. */
const cursorVoltStep = computed(() => voltsPerDivision.value / 10)

/** What the pairs read — Δt, 1/Δt and ΔV. See cursorReadings. */
const cursorRows = computed(() =>
  cursorReadings(
    cursors.value,
    voltsPerDivision.value,
    Math.abs(toChartMs(timeToS.value) - toChartMs(timeFromS.value)),
  ),
)

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
        <!-- One press of the spinner is one division, which is how a scope's vertical position
             knob is read: "bring it down two squares". A fixed step is a different distance at
             every V/div, and at the bottom of the ladder it is 10 000 of them. -->
        <input
          :value="voltOffsetV"
          type="number"
          :step="voltsPerDivision"
          class="tb-input"
          title="Where the middle of the graticule sits. A DUT with a DC offset is brought back on screen with this rather than by widening the V/div. The arrows move it one division at a time."
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
      <span class="tb-group">Channels</span>
      <label class="tb-check-row">
        <input
          v-model="showLeft"
          type="checkbox"
          class="tb-check"
          :disabled="!captured.left"
          :title="captured.left ? 'Draw the left input.' : 'Not being captured — tick Left under Acquisition ▸ Channels.'"
        />
        Left
      </label>
      <label class="tb-check-row">
        <input
          v-model="showRight"
          type="checkbox"
          class="tb-check"
          :disabled="!captured.right"
          :title="captured.right ? 'Draw the right input.' : 'Not being captured — tick Right under Acquisition ▸ Channels.'"
        />
        Right
      </label>
    </div>

    <label class="tb-check-row">
      <input
        v-model="showResidual"
        type="checkbox"
        class="tb-check"
        title="Draws each channel again with its fitted fundamental subtracted - what is left is the distortion and the noise. The fit is a least-squares sine with its frequency refined, so a tone off a bin cancels too; a record with no fundamental has nothing to remove and nothing is drawn."
      />
      Residual
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


  <CollapsibleSection title="Cursors" size="xs">
    <!-- A cursor is a transient reading that belongs to the session, not a note that belongs to the
         measurement - so none of this is in params_json, and none of it is in a report. -->
    <label class="tb-check-row">
      <input
        :checked="cursors.timeOn"
        type="checkbox"
        class="tb-check"
        title="Two vertical cursors reading the time between them and its reciprocal. While they are on, the Measurements panel reads the stretch of the record between them instead of the whole window."
        @change="toggleTimeCursors(($event.target as HTMLInputElement).checked)"
      />
      Time cursors
    </label>
    <div v-if="cursors.timeOn" class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        A ({{ boxScale.label }})
        <input
          :value="cursorABoxValue"
          type="number"
          :step="boxStep"
          class="tb-input"
          @change="onCursorTimeChange($event, 'timeAS')"
        />
      </label>
      <label class="tb-field">
        B ({{ boxScale.label }})
        <input
          :value="cursorBBoxValue"
          type="number"
          :step="boxStep"
          class="tb-input"
          @change="onCursorTimeChange($event, 'timeBS')"
        />
      </label>
    </div>

    <label class="tb-check-row">
      <input
        :checked="cursors.voltOn"
        type="checkbox"
        class="tb-check"
        title="Two horizontal cursors reading the level difference between them. They read levels only and do not gate the measurements - a pair of levels names no stretch of the record."
        @change="toggleVoltCursors(($event.target as HTMLInputElement).checked)"
      />
      Level cursors
    </label>
    <div v-if="cursors.voltOn" class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        A (V)
        <input
          :value="cursors.voltAV"
          type="number"
          :step="cursorVoltStep"
          class="tb-input"
          @change="onCursorVoltChange($event, 'voltAV')"
        />
      </label>
      <label class="tb-field">
        B (V)
        <input
          :value="cursors.voltBV"
          type="number"
          :step="cursorVoltStep"
          class="tb-input"
          @change="onCursorVoltChange($event, 'voltBV')"
        />
      </label>
    </div>

    <table v-if="cursorRows.length > 0" class="w-full text-left text-[11px]">
      <tbody>
        <tr v-for="row in cursorRows" :key="row.label">
          <td class="pr-2" :title="row.title">{{ row.label }}</td>
          <td class="font-mono tabular-nums">{{ row.value }}</td>
        </tr>
      </tbody>
    </table>
    <p v-else class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      Switch a pair on and drag the lettered grips on the chart - time along the bottom, level down
      the left.
    </p>
  </CollapsibleSection>

  <AnnotationsPanel :view="OSCILLOSCOPE_ANNOTATION_VIEW" />
</template>
