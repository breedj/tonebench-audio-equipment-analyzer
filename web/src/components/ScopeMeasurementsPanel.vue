<script setup lang="ts">
import { computed } from 'vue'
import CollapsibleSection from './CollapsibleSection.vue'
import ReadingValue from './ReadingValue.vue'
import { useOscilloscope } from '../composables/useOscilloscope'
import { formatPerDivision, formatScopeTime, toChartMs } from '../scope/timeAxis'

/**
 * What the record on screen <em>is</em> — how long, how it was sampled, where its trigger sits, and
 * what the chart is showing it at.
 *
 * <p><b>Facts about the capture, and no readings.</b> Every figure measured <em>from</em> the
 * capture is in the Selected measurements table under the chart, picked from a figure of each: that
 * is where a reader is looking when they notice one is missing, and a second list of the same
 * numbers in the rail is one too many. The four here are the ones no measurement can report,
 * because they describe the record rather than the signal in it.</p>
 *
 * <p>The notes that used to sit under them — spliced, untriggered, and what the display path is
 * doing — are badges on the chart's own header row, which is where they belong: they qualify the
 * picture, and a paragraph that comes and goes above a chart takes its height off the canvas every
 * time it appears.</p>
 */

const props = defineProps<{
  timeFromS: number
  timeToS: number
}>()

const { record } = useOscilloscope()

/** What the chart is showing, over ten divisions — the one row here that is about the view. */
const spanMs = computed(() => Math.abs(toChartMs(props.timeToS) - toChartMs(props.timeFromS)))

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
    hint="What the capture on screen is, rather than what it reads: how long the record is, the rate it was sampled at, how much of it sits before the trigger, and what the chart is showing it at. Every figure measured from the record is in the Selected measurements table under the chart."
  >
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
  </CollapsibleSection>
</template>
