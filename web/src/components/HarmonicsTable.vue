<script setup lang="ts">
import { computed } from 'vue'
import type { MetersFrame } from '../streaming/protocol'
import CollapsibleSection from './CollapsibleSection.vue'
import PeakSortHeader from './PeakSortHeader.vue'
import { useChartDisplay } from '../composables/useChartDisplay'
import { nextPeakSort, sortPeakRows, type PeakSortColumn } from '../measurements/peakSort'

const props = defineProps<{ meters: Record<number, MetersFrame>; channel: 0 | 1; channelLabel: 'L' | 'R' }>()

// The sort comes from the shared display store rather than from a prop, for two reasons: the L and
// R instances of this table have to agree (they are one table read twice, and a per-channel sort
// would mean clicking the same header twice to sort what looks like one listing), and the choice is
// saved with the measurement type like every other display preference here. See useChartDisplay.
const { harmonicsSort } = useChartDisplay()

// meters.harmonics[0] is always order 1 (the fundamental itself, dBc 0 by definition) — see
// StreamFrame.EncodeMeters. Index i (i>=1) is order i+1, i.e. H(i+1).
const rows = computed(() => {
  const m = props.meters[props.channel]
  if (!m) return []

  return m.harmonics.map((h, i) => ({
    label: i === 0 ? 'Fundamental' : `H${i + 1}`,
    freqHz: h.freqHz,
    dbc: h.dbc,
    dbv: m.fundamentalDbv + h.dbc,
  }))
})

/**
 * The rows as drawn. Natural order is by order — the fundamental, then H2, H3 … — and that is what
 * no sort means; the Order column keeps saying which is which however they are arranged, so a sort
 * does not have to hold the fundamental in place.
 */
const sortedRows = computed(() => sortPeakRows(rows.value, harmonicsSort.value))

function onSort(column: PeakSortColumn) {
  harmonicsSort.value = nextPeakSort(harmonicsSort.value, column)
}
</script>

<template>
  <CollapsibleSection :title="`${channelLabel}: Harmonics`" size="xs">
    <div v-if="sortedRows.length" class="overflow-x-auto">
      <table class="w-full text-left text-sm">
        <thead>
          <tr class="text-xs text-neutral-500 dark:text-neutral-400">
            <th class="py-1 pr-4 font-medium">Order</th>
            <th class="py-1 pr-4 font-medium">
              <PeakSortHeader label="Frequency" column="frequency" :sort="harmonicsSort" @sort="onSort" />
            </th>
            <th class="py-1 pr-4 font-medium">
              <PeakSortHeader label="Level" column="level" :sort="harmonicsSort" @sort="onSort" />
            </th>
            <th class="py-1 font-medium">
              <PeakSortHeader label="dBc" column="dbc" :sort="harmonicsSort" @sort="onSort" />
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in sortedRows" :key="row.label" class="border-t border-neutral-100 font-mono dark:border-neutral-800">
            <td class="py-1 pr-4">{{ row.label }}</td>
            <td class="py-1 pr-4">{{ row.freqHz.toFixed(1) }} Hz</td>
            <td class="py-1 pr-4">{{ row.dbv.toFixed(2) }} dBV</td>
            <td class="py-1">{{ row.label === 'Fundamental' ? '—' : `${row.dbc.toFixed(1)} dB` }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Waiting for data…</div>
  </CollapsibleSection>
</template>
