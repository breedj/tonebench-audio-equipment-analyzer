<script setup lang="ts">
import { computed } from 'vue'
import CollapsibleSection from './CollapsibleSection.vue'
import ReadingValue from './ReadingValue.vue'
import { useOscilloscope } from '../composables/useOscilloscope'
import { mathReadings, residualReadings, scopeChannelReadings } from '../scope/readings'
import { mathAxisLabel, type ScopeMathOperation } from '../scope/math'
import { correlationDescription, xyReadings } from '../scope/xy'
import { eyeNote, eyeReadings } from '../scope/eye'
import { formatSeconds } from '../scope/readings'
import { formatPerDivision, formatScopeTime, toChartMs } from '../scope/timeAxis'
import { useScopeMeasurements } from '../composables/useScopeMeasurements'

/**
 * What the record on screen reads — one block per captured channel, plus the facts about the record
 * itself.
 *
 * <p><b>Every figure here was measured on the analyzer, and none of it is read off the picture.</b>
 * Vmax, Vmin and Vpp would survive the decimation — a min/max pair per column keeps both extremes
 * of every column exactly — and nothing else would: a mean of column maxima is the mean of an
 * envelope, an RMS over them is an RMS of nothing, and a rise time read off a column pair is a
 * reading of the column width, which is the measurement a scope is most often pointed at and the
 * one it would be worst to get plausibly wrong. So the whole set travels beside the picture
 * (<code>ScopeChannelSlice.measurements</code>), measured over the window on screen at full
 * resolution, and this panel formats it through <code>scope/readings.ts</code> — the same function
 * the report prints from, so the paper and the screen cannot disagree.</p>
 */

const props = withDefaults(
  defineProps<{
    /** What the vertical readings are written in, so a column of them stays in one unit. */
    voltsPerDivision: number
    timeFromS: number
    timeToS: number
    /**
     * Which inputs are drawn — the display rail's own pair.
     *
     * <p>The readings follow the picture, as a bench scope's do: a channel taken off the screen
     * takes its measurements with it. What a hidden channel reads is still in the record, and
     * ticking it back brings both back together.</p>
     */
    showLeft?: boolean
    showRight?: boolean
    /**
     * What the display path is doing to the trace, or null for a bare record.
     *
     * <p>Printed under the readings because it qualifies every one of them: a low-pass lowers a
     * peak to peak and lengthens a rise time, a notch removes a component the RMS was counting.
     * None of that is wrong — the figures are of the trace on screen, which is the only honest
     * arrangement — but nothing else here would say which trace that is.</p>
     */
    pathNote?: string | null
  }>(),
  { showLeft: true, showRight: true, pathNote: null },
)

const { record, spliced, forced, gate } = useOscilloscope()

/**
 * The controls for the readout under the chart, which live here rather than beside it.
 *
 * <p>A bench instrument puts the readings on the screen and the controls that decide them in a
 * menu, and this screen's menu is its rail — every other thing a reader sets about this
 * measurement is in it. Keeping the table itself free of controls also keeps it the one thing on
 * the page that is purely what was measured.</p>
 */
const {
  selected: selectedMeasurements,
  statistics,
  setStatistics,
  resetStats,
  statCount,
} = useScopeMeasurements()

const emit = defineEmits<{
  /** Open the picker. The dialog belongs to the screen, which is the one thing that can mount it. */
  pickMeasurements: []
}>()

/**
 * Whether the figures below are the cursors' or the window's.
 *
 * <p>Read off the <b>slice</b> and not off the cursors, which is the one choice here worth stating:
 * the gate rides on the same fetch as the picture, so a slice carrying a gated set is a slice whose
 * gated set is of that record. Keying the heading on the cursors instead would label the previous
 * capture's figures "between the cursors" for one tick after a drag — which is exactly the
 * plausible wrong answer the server refuses to give by answering null.</p>
 */
const gated = computed(() => (record.value?.channels ?? []).some((slice) => slice.gated !== null))

const blocks = computed(() =>
  scopeChannelReadings(
    record.value?.channels,
    props.voltsPerDivision,
    props.showLeft,
    props.showRight,
    gated.value ? 'gate' : 'window',
  ),
)

/**
 * The residual's own readings, per drawn channel.
 *
 * <p>Taken off the <b>window's</b> slice whether or not the cursors are gating, because the
 * residual is a property of what is drawn: the fit is made on the window on screen, and the trace
 * beside these figures is that window's.</p>
 */
const residualBlocks = computed(() =>
  (record.value?.channels ?? [])
    .filter((slice) => (slice.channel === 1 ? props.showRight : props.showLeft))
    .map((slice) => ({
      channel: slice.channel,
      label: slice.channel === 0 ? 'Left' : 'Right',
      readings: residualReadings(slice.residual, props.voltsPerDivision),
    }))
    .filter((block) => block.readings.length > 0),
)

/**
 * The math channel's readings, and what to call them.
 *
 * <p>The <b>gated</b> set when the cursors are gating, unlike the residual's: a difference, a
 * product or an integral over a stretch of the record is a reading of that stretch the same way a
 * channel's own is, where the residual's fit belongs to the whole drawn window.</p>
 *
 * <p>The heading carries the expression <i>and</i> its unit, because that is where a reader finds
 * out these figures are not volts.</p>
 */
const mathBlock = computed(() => {
  const math = record.value?.math
  if (!math) return null
  const readings = mathReadings(math, gated.value ? 'gate' : 'window')
  if (!readings.length) return null
  return { label: mathAxisLabel(math.operation as ScopeMathOperation), clamped: math.clampedSamples, readings }
})

/**
 * The X–Y figure's own readings, when one was fetched.
 *
 * <p>Shown whenever the record carries a figure rather than only while that view is on screen: the
 * fetch already asked for it, the three numbers are about the pair of channels rather than about
 * the picture, and a correlation worth seeing is worth seeing next to the waveforms that produced
 * it.</p>
 */
const xyBlock = computed(() => {
  const figure = record.value?.xy
  if (!figure) return null
  const readings = xyReadings(figure)
  if (!readings.length) return null
  return { description: correlationDescription(figure.correlation), readings }
})

/**
 * The eye diagram's own readings, when one was fetched.
 *
 * <p>Shown whenever the record carries one, like the figure above and for the same reason: the
 * fetch already asked for it, and a cycle-to-cycle spread is a fact about the signal rather than
 * about the picture — worth reading beside the waveform that produced it.</p>
 */
const eyeBlock = computed(() => {
  const figure = record.value?.eye
  if (!figure) return null
  const readings = eyeReadings(figure)
  if (!readings.length) return null
  return { note: eyeNote(figure), readings }
})

/** The gate as the heading quotes it, so the reader can see which stretch the figures are of. */
const gateNote = computed(() => {
  const window = gate.value
  if (!window || !gated.value) return null
  return `${formatSeconds(window.fromS)} to ${formatSeconds(window.toS)} · ${formatSeconds(window.toS - window.fromS)}`
})

const spanMs = computed(() => Math.abs(toChartMs(props.timeToS) - toChartMs(props.timeFromS)))

/** The record's own facts: how long it is, how it was sampled, and whether it can be trusted. */
/**
 * Why an envelope's figures are only three, printed where the figures are.
 *
 * <p>The chart's badge carries the same sentence as a tooltip, and this is the place it is readable
 * without hovering — the Measurements panel's own rule for the spliced and untriggered flags. An
 * envelope is not a waveform, so a mean, a period or a rise time of one would be arithmetic on
 * something that was never a signal.</p>
 */
const envelopeNote = computed(() =>
  record.value?.envelope === true
    ? `The band ${(record.value.averagesTaken ?? 1).toLocaleString()} records have swept, not a waveform — so only its extremes have readings. Clear to start the band again.`
    : null,
)

const recordFacts = computed(() => {
  const current = record.value
  if (!current) return null
  return {
    samples: current.frameCount.toLocaleString(),
    rate: `${(current.sampleRateHz / 1000).toFixed(0)} kS/s`,
    length: formatScopeTime(toChartMs(current.toS - current.fromS), toChartMs(current.toS - current.fromS)),
    interval: formatScopeTime(toChartMs(1 / current.sampleRateHz), 0.05),
    trigger: formatScopeTime(toChartMs(current.triggerOffsetS), toChartMs(current.triggerOffsetS) || 1),
  }
})
</script>

<template>
  <CollapsibleSection
    title="Measurements"
    hint="Every figure here is measured on the analyzer, on the samples of the window on screen at full resolution. A reading a waveform does not have is left out rather than dashed — a period needs two crossings, an edge needs two settled levels, and a record with one level has neither. The mean is the input’s own residual offset on a quiet input, both this analyzer and a sound card being AC-coupled, and nothing corrects it."
  >
    <!-- What the readout under the chart shows, and whether it keeps statistics. Above the readings
         because it is a setting rather than a figure, and because the list below is long. -->
    <div class="mb-2 flex flex-wrap items-center gap-2 border-b border-neutral-200 pb-2 dark:border-neutral-800">
      <button
        type="button"
        class="btn-panel-accent"
        title="Choose which readings are shown under the chart, from a figure of each."
        @click="emit('pickMeasurements')"
      >
        Choose measurements…
      </button>
      <!-- "chosen" rather than "shown": each one is a row per drawn channel, so the two counts
           differ on a stereo capture and the smaller is the one this button changes. -->
      <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
        {{ selectedMeasurements.length }} chosen
      </span>
    </div>
    <div class="mb-2 flex flex-wrap items-center gap-2 border-b border-neutral-200 pb-2 dark:border-neutral-800">
      <label
        class="tb-check-row text-[11px]"
        title="Accumulates every chosen reading across records: its mean, its extremes, how far it has wandered, and how many records have been counted."
      >
        <input
          type="checkbox"
          class="tb-check"
          :checked="statistics"
          @change="setStatistics(($event.target as HTMLInputElement).checked)"
        />
        Statistics
      </label>
      <button
        type="button"
        class="btn-panel-accent"
        :disabled="statCount === 0"
        title="Throws the accumulated statistics away and starts counting again from the next record. The readings themselves are unaffected."
        @click="resetStats()"
      >
        Reset statistics
      </button>
      <span v-if="statCount > 0" class="text-[11px] text-neutral-500 dark:text-neutral-400">
        {{ statCount.toLocaleString() }} {{ statCount === 1 ? 'record' : 'records' }}
      </span>
    </div>

    <template v-if="blocks.length > 0">
      <p
        v-if="envelopeNote"
        class="mb-2 rounded bg-amber-500/10 px-1.5 py-1 text-[11px] leading-snug text-amber-700 dark:text-amber-300"
      >
        {{ envelopeNote }}
      </p>
      <p
        v-if="gateNote"
        class="mb-2 rounded bg-teal-500/10 px-1.5 py-1 text-[11px] leading-snug text-teal-700 dark:text-teal-300"
      >
        Between the cursors — {{ gateNote }}. Re-measured on the samples in that stretch, not read
        off the window's own figures.
      </p>

      <div v-for="block in blocks" :key="block.channel" class="mb-2 last:mb-0" :data-scope-block="block.label">
        <div class="mb-1 text-[11px] font-medium text-neutral-600 dark:text-neutral-300">{{ block.label }}</div>
        <table class="w-full text-left text-[11px]">
          <tbody>
            <tr v-for="reading in block.readings" :key="reading.label">
              <td class="pr-2" :title="reading.title">{{ reading.label }}</td>
              <td><ReadingValue :text="reading.value" /></td>
            </tr>
          </tbody>
        </table>
      </div>

      <div
        v-if="residualBlocks.length > 0"
        class="mt-2 border-t border-neutral-200 pt-2 dark:border-neutral-800"
      >
        <div class="mb-1 text-[11px] font-medium text-neutral-600 dark:text-neutral-300">Residual</div>
        <div v-for="block in residualBlocks" :key="`res-${block.channel}`" class="mb-2 last:mb-0">
          <table class="w-full text-left text-[11px]">
            <tbody>
              <tr v-for="(reading, i) in block.readings" :key="`${reading.label}-${i}`">
                <td class="pr-2" :title="reading.title">{{ block.label }} {{ reading.label.toLowerCase() }}</td>
                <td><ReadingValue :text="reading.value" /></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div
        v-if="mathBlock"
        class="mt-2 border-t border-neutral-200 pt-2 dark:border-neutral-800"
      >
        <div class="mb-1 text-[11px] font-medium text-violet-600 dark:text-violet-400">
          {{ mathBlock.label }}
        </div>
        <table class="w-full text-left text-[11px]">
          <tbody>
            <tr v-for="reading in mathBlock.readings" :key="`math-${reading.label}`">
              <td class="pr-2" :title="reading.title">{{ reading.label }}</td>
              <td><ReadingValue :text="reading.value" /></td>
            </tr>
          </tbody>
        </table>
        <p v-if="mathBlock.clamped > 0" class="mt-1 text-[11px] leading-snug text-amber-600 dark:text-amber-400">
          {{ mathBlock.clamped.toLocaleString() }} samples railed at ±1000, so every figure above is
          a reading of the clamp as much as of the ratio.
        </p>
      </div>

      <div
        v-if="xyBlock"
        class="mt-2 border-t border-neutral-200 pt-2 dark:border-neutral-800"
      >
        <div class="mb-1 text-[11px] font-medium text-teal-700 dark:text-teal-300">
          X–Y · {{ xyBlock.description }}
        </div>
        <table class="w-full text-left text-[11px]">
          <tbody>
            <tr v-for="reading in xyBlock.readings" :key="`xy-${reading.label}`">
              <td class="pr-2" :title="reading.title">{{ reading.label }}</td>
              <td><ReadingValue :text="reading.value" /></td>
            </tr>
          </tbody>
        </table>
      </div>

      <div
        v-if="eyeBlock"
        class="mt-2 border-t border-neutral-200 pt-2 dark:border-neutral-800"
      >
        <div class="mb-1 text-[11px] font-medium text-blue-700 dark:text-blue-300">
          Eye · {{ eyeBlock.note }}
        </div>
        <table class="w-full text-left text-[11px]">
          <tbody>
            <tr v-for="reading in eyeBlock.readings" :key="`eye-${reading.label}`">
              <td class="pr-2" :title="reading.title">{{ reading.label }}</td>
              <td><ReadingValue :text="reading.value" /></td>
            </tr>
          </tbody>
        </table>
      </div>

      <div v-if="recordFacts" class="mt-2 border-t border-neutral-200 pt-2 dark:border-neutral-800">
        <table class="w-full text-left text-[11px]">
          <tbody>
            <tr>
              <td class="pr-2" title="The whole record, pre- and post-trigger together.">Record</td>
              <td><ReadingValue :text="recordFacts.length" /> <span class="text-neutral-500">· {{ recordFacts.samples }} samples</span></td>
            </tr>
            <tr>
              <td class="pr-2">Sampled at</td>
              <td><span class="font-mono text-neutral-900 dark:text-neutral-100">{{ recordFacts.rate }}</span> <span class="text-neutral-500">· {{ recordFacts.interval }} apart</span></td>
            </tr>
            <tr>
              <td class="pr-2" title="How much of the record sits before the trigger.">Pre-trigger</td>
              <td><ReadingValue :text="recordFacts.trigger" /></td>
            </tr>
            <tr>
              <td class="pr-2" title="The window the chart is showing, over ten divisions.">Timebase</td>
              <td><span class="font-mono text-neutral-900 dark:text-neutral-100">{{ formatPerDivision(spanMs) }}</span></td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- Both of these make an ordinary-looking trace mean something else, so they are said here as
           well as on the chart. -->
      <p v-if="pathNote" class="mt-2 text-[11px] leading-snug text-blue-600 dark:text-blue-400">
        Read through the display path: {{ pathNote }}. Every level figure above is of the coupled
        trace, moved by the record’s own mean. What is stored is always the capture as it
        arrived, so a saved result can be reopened and read a different way, or raw.
      </p>

      <p v-if="spliced" class="mt-2 text-[11px] text-amber-600 dark:text-amber-400">
        This record spans a dropped chunk or block, so every reading above is taken across a splice.
      </p>
      <p v-else-if="forced" class="mt-2 text-[11px] text-amber-600 dark:text-amber-400">
        Taken by Auto's timeout — nothing crossed the trigger level, so the record is aligned to
        nothing. The readings are still the record's own.
      </p>

    </template>
    <p v-else class="text-xs text-neutral-400 dark:text-neutral-600">Nothing captured yet.</p>
  </CollapsibleSection>
</template>
