<script setup lang="ts">
import { computed } from 'vue'
import type { MultitoneFrame } from '../streaming/protocol'
import CollapsibleSection from './CollapsibleSection.vue'

const props = defineProps<{
  multitone: Record<number, MultitoneFrame>
  channel: 0 | 1
  channelLabel: 'L' | 'R'
}>()

const frame = computed(() => props.multitone[props.channel] ?? null)

// One row per tone, carrying the gap *above* it: the tone at the row's frequency and, in the same
// row, the highest line and the D+N between it and the next tone. The last tone has no gap above
// it and shows dashes. This is the "TD+N vs frequency" view — each row reads at its tone.
const rows = computed(() => {
  const f = frame.value
  if (!f) return []
  return f.tones.map((tone, i) => {
    const band = f.bands[i] ?? null
    return {
      key: i,
      toneHz: tone.freqHz,
      toneDbv: tone.dbv,
      hasGap: band !== null,
      resolved: band?.resolved ?? false,
      highestHz: band?.highestFreqHz ?? 0,
      highestDbv: band?.highestDbv ?? 0,
      highestDbr: band?.highestDbr ?? 0,
      dnDbr: band?.dnDbr ?? 0,
    }
  })
})

const tdnDb = computed(() => {
  const f = frame.value
  return f && f.tdn > 0 ? 20 * Math.log10(f.tdn) : null
})

function formatHz(hz: number): string {
  return hz >= 1000 ? `${(hz / 1000).toFixed(hz % 1000 === 0 ? 0 : 2)} kHz` : `${hz.toFixed(1)} Hz`
}
</script>

<template>
  <CollapsibleSection :title="`${channelLabel}: Multitone`" size="xs">
    <div v-if="frame" class="overflow-x-auto">
      <table class="w-full text-left text-sm">
        <thead>
          <tr class="text-xs text-neutral-500 dark:text-neutral-400">
            <th class="py-1 pr-4 font-medium">Tone</th>
            <th class="py-1 pr-4 font-medium">Level</th>
            <th class="py-1 pr-4 font-medium" title="The loudest line between this tone and the next, and its level relative to the comb's total RMS.">Highest above</th>
            <th class="py-1 pr-4 font-medium" title="Relative to the comb's total RMS (√ΣV²).">dBr</th>
            <th class="py-1 font-medium" title="Everything between this tone and the next — distortion plus noise — relative to the comb's total RMS.">D+N dBr</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.key" class="border-t border-neutral-100 font-mono dark:border-neutral-800">
            <td class="py-1 pr-4">{{ formatHz(row.toneHz) }}</td>
            <td class="py-1 pr-4">{{ row.toneDbv.toFixed(2) }} dBV</td>
            <template v-if="row.hasGap && row.resolved">
              <td class="py-1 pr-4">{{ formatHz(row.highestHz) }} · {{ row.highestDbv.toFixed(1) }} dBV</td>
              <td class="py-1 pr-4">{{ row.highestDbr.toFixed(1) }} dBr</td>
              <td class="py-1">{{ row.dnDbr.toFixed(1) }} dBr</td>
            </template>
            <template v-else-if="row.hasGap">
              <td class="py-1 pr-4 text-xs text-amber-600 dark:text-amber-400" colspan="3" title="The two tones are closer than 8 bins at this FFT size, so nothing between them can be read. A larger FFT resolves lower bands.">unresolved</td>
            </template>
            <template v-else>
              <td class="py-1 pr-4 text-neutral-400 dark:text-neutral-600" colspan="3">—</td>
            </template>
          </tr>
        </tbody>
        <tfoot class="text-xs">
          <tr class="border-t border-neutral-200 font-mono dark:border-neutral-700">
            <td class="py-1 pr-4 font-sans font-medium" title="Total distortion plus noise: everything between the tones over the resolved gaps, relative to the comb's total RMS.">TD+N</td>
            <td class="py-1 pr-4" colspan="2">{{ (frame.tdn * 100).toFixed(5) }} %{{ tdnDb === null ? '' : ` · ${tdnDb.toFixed(2)} dB` }}</td>
            <td class="py-1 text-neutral-400 dark:text-neutral-600" colspan="2">
              {{ frame.resolvedBands }} of {{ frame.bands.length }} gaps · comb {{ frame.combDbv.toFixed(2) }} dBV
            </td>
          </tr>
        </tfoot>
      </table>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Waiting for data…</div>
  </CollapsibleSection>
</template>
