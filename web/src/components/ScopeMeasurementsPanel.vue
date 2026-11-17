<script setup lang="ts">
import { computed } from 'vue'
import CollapsibleSection from './CollapsibleSection.vue'
import ReadingValue from './ReadingValue.vue'
import { useOscilloscope } from '../composables/useOscilloscope'
import { sliceExtremes } from '../scope/measurements'
import { formatVolts } from '../scope/voltAxis'
import { formatPerDivision, formatScopeTime, toChartMs } from '../scope/timeAxis'

/**
 * What the record on screen reads — one block per captured channel, plus the facts about the record
 * itself.
 *
 * <p><b>Three vertical readings and no more, deliberately.</b> A min/max pair per display column
 * keeps both extremes of every column exactly, so Vmax, Vmin and Vpp survive the decimation and are
 * the figures a full-resolution pass would give. Nothing else does — an RMS over column maxima is
 * an RMS of nothing, and a rise time read off a column pair is a reading of the column width, which
 * is the measurement a scope is most often pointed at and the one it would be worst to get plausibly
 * wrong. The rest of the set (RMS, mean, top/base, period, rise, fall, duty, the channel delay) is
 * computed server-side on the committed record and arrives with the measurements phase. See
 * scope/measurements.ts.</p>
 */

const props = defineProps<{
  /** What the vertical readings are written in, so a column of them stays in one unit. */
  voltsPerDivision: number
  timeFromS: number
  timeToS: number
}>()

const { record, spliced, forced } = useOscilloscope()

const blocks = computed(() =>
  (record.value?.channels ?? [])
    .map((slice) => ({
      channel: slice.channel,
      label: slice.channel === 0 ? 'Left' : 'Right',
      extremes: sliceExtremes(slice),
      raw: slice.raw,
    }))
    .filter((block) => block.extremes !== null),
)

function volts(value: number): string {
  return formatVolts(value, props.voltsPerDivision)
}

const spanMs = computed(() => Math.abs(toChartMs(props.timeToS) - toChartMs(props.timeFromS)))

/** The record's own facts: how long it is, how it was sampled, and whether it can be trusted. */
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
  <CollapsibleSection title="Measurements">
    <template v-if="blocks.length > 0">
      <div v-for="block in blocks" :key="block.channel" class="mb-2 last:mb-0" :data-scope-block="block.label">
        <div class="mb-1 text-[11px] font-medium text-neutral-600 dark:text-neutral-300">{{ block.label }}</div>
        <table class="w-full text-left text-[11px]">
          <tbody>
            <tr>
              <td class="pr-2" title="The highest sample in the window — exact even on a decimated slice, because both extremes of every column are kept.">Maximum</td>
              <td><ReadingValue :text="volts(block.extremes!.maxV)" /></td>
            </tr>
            <tr>
              <td class="pr-2" title="The lowest sample in the window.">Minimum</td>
              <td><ReadingValue :text="volts(block.extremes!.minV)" /></td>
            </tr>
            <tr>
              <td class="pr-2" title="Peak to peak — the maximum less the minimum.">Peak to peak</td>
              <td><ReadingValue :text="volts(block.extremes!.ppV)" /></td>
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
      <p v-if="spliced" class="mt-2 text-[11px] text-amber-600 dark:text-amber-400">
        This record spans a dropped chunk or block, so every reading above is taken across a splice.
      </p>
      <p v-else-if="forced" class="mt-2 text-[11px] text-amber-600 dark:text-amber-400">
        Taken by Auto's timeout — nothing crossed the trigger level, so the record is aligned to
        nothing. The readings are still the record's own.
      </p>

      <p class="mt-2 text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        RMS, mean, period, rise and fall are measured on the whole record server-side and are not in
        this build. Reading them off a decimated column pair would be a reading of the decimation.
      </p>
    </template>
    <p v-else class="text-xs text-neutral-400 dark:text-neutral-600">Nothing captured yet.</p>
  </CollapsibleSection>
</template>
