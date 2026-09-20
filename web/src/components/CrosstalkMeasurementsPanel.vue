<script setup lang="ts">
import { computed } from 'vue'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { crosstalkDirectionLabel, crosstalkReadings, formatHz, type CrosstalkReadings } from '../crosstalk/readings'
import CollapsibleSection from './CollapsibleSection.vue'

/**
 * The figures a crosstalk curve is quoted by — at 1 kHz, the worst point, and the band's ends —
 * one block per direction measured. Read through crosstalk/readings.ts, which the report reads
 * too, so paper and screen quote the same numbers.
 */
const props = defineProps<{
  traces: ResponseTrace[]
  /** The generator level the run used (the last pass's, while running), in dBV. */
  generatorAmpDbv: number
}>()

const blocks = computed(() =>
  props.traces
    .filter((t) => t.kind === 'CrosstalkLeftToRight' || t.kind === 'CrosstalkRightToLeft')
    .map((t) => ({ kind: t.kind, label: crosstalkDirectionLabel(t.kind), readings: crosstalkReadings(t) }))
    .filter((b): b is { kind: ResponseTrace['kind']; label: string; readings: CrosstalkReadings } => b.readings !== null),
)

function fmtDb(db: number): string {
  return `${db.toFixed(1)} dB`
}
</script>

<template>
  <CollapsibleSection title="Measurements">
    <template v-if="blocks.length > 0">
      <div v-for="block in blocks" :key="block.kind" class="mb-2 last:mb-0" :data-crosstalk-block="block.kind">
        <div class="mb-1 text-[11px] font-medium text-neutral-600 dark:text-neutral-300">{{ block.label }}</div>
        <table class="w-full text-left text-[11px] whitespace-nowrap">
          <tbody class="font-mono">
            <tr>
              <td class="pr-2 font-sans" title="The measured point nearest 1 kHz — the figure a specification quotes.">At {{ formatHz(block.readings.at.freqHz) }}</td>
              <td>{{ fmtDb(block.readings.at.db) }}</td>
            </tr>
            <tr>
              <td class="pr-2 font-sans" title="The highest (least negative) point on the curve, and where it is.">Worst</td>
              <td>{{ fmtDb(block.readings.worst.db) }} <span class="text-neutral-500">at {{ formatHz(block.readings.worst.freqHz) }}</span></td>
            </tr>
            <tr>
              <td class="pr-2 font-sans">At {{ formatHz(block.readings.low.freqHz) }}</td>
              <td>{{ fmtDb(block.readings.low.db) }}</td>
            </tr>
            <tr>
              <td class="pr-2 font-sans">At {{ formatHz(block.readings.high.freqHz) }}</td>
              <td>{{ fmtDb(block.readings.high.db) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <p class="mt-1 text-[11px] text-neutral-500 dark:text-neutral-400" title="The aggressor's drive. In the output-referred modes each direction solves its own; this is the most recent.">
        Generator {{ props.generatorAmpDbv.toFixed(2) }} dBV
      </p>
    </template>
    <p v-else class="text-xs text-neutral-400 dark:text-neutral-600">No crosstalk measured yet.</p>
  </CollapsibleSection>
</template>
