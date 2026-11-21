<script setup lang="ts">
import { computed } from 'vue'
import CollapsibleSection from './CollapsibleSection.vue'
import ReadingValue from './ReadingValue.vue'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import { useOscilloscope } from '../composables/useOscilloscope'
import { formatPerDivision, formatScopeTime, toChartMs } from '../scope/timeAxis'

/**
 * What a capture <em>is</em> — the one the settings describe, and the one on screen.
 *
 * <p><b>Facts about the capture, and no readings.</b> Every figure measured <em>from</em> the
 * capture is in the Selected measurements table under the chart, picked from a figure of each: that
 * is where a reader is looking when they notice one is missing, and a second list of the same
 * numbers in the rail is one too many. The ones here are those no measurement can report, because
 * they describe the record rather than the signal in it.</p>
 *
 * <p><b>Two halves, because they are routinely different</b> (Joost, 2026-11-20). The first table
 * is the <em>staged</em> figures — what the next capture will be — and the second is the record
 * that was actually taken. Both are name-and-value rows, where the estimate was four badges
 * until 2026-11-21: two halves saying the same kind of thing read alike, and the pills spent the
 * rail's width on their own chrome rather than on the figures. Changing the sample rate or the timebase while stopped moves the first and
 * leaves the second where it was, which is the whole reason the estimate exists; one list could
 * only be one of them, and before the first capture there is nothing but the estimate. They sat in
 * the Acquisition panel until 2026-11-20, under the controls they are an estimate of, which spent
 * height there on four pills and a paragraph while this panel said the same kind of thing three
 * sections further down.</p>
 *
 * <p>The notes that used to sit under the table — spliced, untriggered, and what the display path
 * is doing — are badges on the chart's own header row, which is where they belong: they qualify the
 * picture, and a paragraph that comes and goes above a chart takes its height off the canvas every
 * time it appears. The analyzer's own 4 µs edge is in the hint, by that same rule: it is an
 * explanation of the figure beside it rather than anything about the present.</p>
 */

const props = defineProps<{
  timeFromS: number
  timeToS: number
  /** The staged record length, in seconds — what the next capture will be. */
  stagedRecordSeconds: number
  /** The staged sample rate. */
  stagedSampleRateHz: number
}>()

const { record } = useOscilloscope()
const { captureDelayMs } = useCaptureDelay()

/** What the chart is showing, over ten divisions — the one row here that is about the view. */
const spanMs = computed(() => Math.abs(toChartMs(props.timeToS) - toChartMs(props.timeFromS)))

/**
 * The estimate the settings add up to.
 *
 * <p>Two is the floor on the sample count for the reason the Acquisition panel's own version had
 * it: a record of one sample is not a record, and the arithmetic has to answer something while a
 * box is being typed into.</p>
 */
const staged = computed(() => {
  const recordMs = props.stagedRecordSeconds * 1000
  const rateHz = props.stagedSampleRateHz
  return {
    recordMs,
    frames: Math.max(2, Math.round(props.stagedRecordSeconds * rateHz)),
    intervalUs: rateHz > 0 ? 1e6 / rateHz : 0,
    // One capture end to end: the settle a stimulus change books, plus the record itself.
    captureMs: captureDelayMs.value + recordMs,
    // Spelled here rather than in the template so the two halves of the panel write a rate the
    // same way: `recordFacts.rate` is this line over the record that arrived.
    rate: (rateHz / 1000).toFixed(0) + ' kS/s',
  }
})

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
    title="Record"
    hint="What a capture is, rather than what it reads: the first table is what the settings add up to — what the next capture will be — and the second is the record on screen, which is a different thing the moment a setting is changed without running again. Nothing faster than about 4 µs can be read off an edge this analyzer produced; that is its own reconstruction filter and not the DUT, and the manual's Oscilloscope page says why. Every figure measured from the record is in the Selected measurements table under the chart."
  >
    <div class="tb-field">
      <span class="tb-group">Next capture</span>
      <table class="w-full text-left text-[11px]">
        <tbody>
          <tr>
            <td
              class="pr-2"
              title="The whole record — ten divisions of the timebase, pre- and post-trigger together — and how many samples it holds at the staged rate. What the timebase costs in memory, which the control itself no longer says."
            >
              Record
            </td>
            <td>
              <ReadingValue :text="formatScopeTime(staged.recordMs, staged.recordMs)" />
              <span class="text-neutral-500">· {{ staged.frames.toLocaleString() }} samples</span>
            </td>
          </tr>
          <tr>
            <td
              class="pr-2"
              title="The staged rate, and one sample interval at it — the floor on anything this measurement can resolve in time. Not the floor on what can be read off an edge: see the hint on this panel."
            >
              Sampled at
            </td>
            <td>
              <span class="font-mono text-neutral-900 dark:text-neutral-100">{{ staged.rate }}</span>
              <span class="text-neutral-500">· {{ formatScopeTime(staged.intervalUs / 1000, 0.05) }} apart</span>
            </td>
          </tr>
          <tr>
            <td class="pr-2" title="One capture end to end: the settle the stimulus change books, plus the record itself.">
              Capture
            </td>
            <td><ReadingValue :text="formatScopeTime(staged.captureMs, staged.captureMs)" /></td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="tb-field">
      <span class="tb-group">On screen</span>
      <table v-if="recordFacts" class="w-full text-left text-[11px]">
        <tbody>
          <tr>
            <td class="pr-2" title="The whole record, pre- and post-trigger together.">Record</td>
            <td>
              <ReadingValue :text="recordFacts.length" />
              <span class="text-neutral-500">· {{ recordFacts.samples }} samples</span>
            </td>
          </tr>
          <tr>
            <td class="pr-2">Sampled at</td>
            <td>
              <span class="font-mono text-neutral-900 dark:text-neutral-100">{{ recordFacts.rate }}</span>
              <span class="text-neutral-500">· {{ recordFacts.interval }} apart</span>
            </td>
          </tr>
          <tr>
            <td class="pr-2" title="How much of the record sits before the trigger.">Pre-trigger</td>
            <td><ReadingValue :text="recordFacts.trigger" /></td>
          </tr>
          <tr>
            <td class="pr-2" title="The window the chart is showing, over ten divisions.">Timebase</td>
            <td>
              <span class="font-mono text-neutral-900 dark:text-neutral-100">{{ formatPerDivision(spanMs) }}</span>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else class="text-xs text-neutral-400 dark:text-neutral-600">Nothing captured yet.</p>
    </div>
  </CollapsibleSection>
</template>
