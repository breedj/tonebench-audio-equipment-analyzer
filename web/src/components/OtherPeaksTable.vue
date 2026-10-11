<script setup lang="ts">
import { computed, ref } from 'vue'
import type { MetersFrame } from '../streaming/protocol'
import CollapsibleSection from './CollapsibleSection.vue'
import PeakSortHeader from './PeakSortHeader.vue'
import { useChartDisplay } from '../composables/useChartDisplay'
import { nextPeakSort, sortPeakRows, type PeakSortColumn } from '../measurements/peakSort'

const props = defineProps<{ meters: Record<number, MetersFrame>; channel: 0 | 1; channelLabel: 'L' | 'R' }>()

const DEFAULT_COUNT = 10

const requestedCount = ref(DEFAULT_COUNT)

// Shared with this table's other channel and saved with the measurement — see HarmonicsTable, and
// useChartDisplay. Its own setting rather than the harmonics table's: the two have different
// natural orders and are usually read for different things.
const { otherPeaksSort } = useChartDisplay()

const available = computed(() => props.meters[props.channel]?.otherPeaks ?? [])

// Server already returns these strongest-first (see PeakFinder.cs); just take the top X.
//
// The rank is taken here, before any sort, and is what the `#` column prints: it is the peak's
// place in the server's ranking, which is what "show top 10" selected on. Renumbering the rows as
// drawn would make `#` say nothing — and would make a sort by level look as though it had done
// nothing at all.
const rows = computed(() => {
  const m = props.meters[props.channel]
  if (!m) return []

  const count = Math.max(1, Math.min(requestedCount.value, available.value.length))
  return available.value.slice(0, count).map((p, i) => ({
    rank: i + 1,
    freqHz: p.freqHz,
    dbv: m.fundamentalDbv + p.dbc,
    dbc: p.dbc,
  }))
})

const sortedRows = computed(() => sortPeakRows(rows.value, otherPeaksSort.value))

function onSort(column: PeakSortColumn) {
  otherPeaksSort.value = nextPeakSort(otherPeaksSort.value, column)
}
</script>

<template>
  <!-- Collapsed by default: the strongest spurs are the exception on a bench, and the table is
       long enough that open it pushes everything under it off screen. Harmonics stays open. -->
  <CollapsibleSection :title="`${channelLabel}: Other peaks`" size="xs" :default-open="false">
    <template #header-right>
      <label class="flex items-center gap-2 text-xs text-neutral-500 dark:text-neutral-400">
        Show top
        <input
          type="number"
          min="1"
          :max="Math.max(1, available.length)"
          v-model.number="requestedCount"
          class="w-16 rounded border border-neutral-300 px-2 py-0.5 dark:border-neutral-700"
        />
      </label>
    </template>

    <p class="text-xs text-neutral-400 dark:text-neutral-600">
      Everything besides the fundamental and its harmonics — mains hum, mixing/intermodulation products, other spurs.
    </p>

    <div v-if="sortedRows.length" class="overflow-x-auto">
      <table class="w-full text-left text-sm">
        <thead>
          <tr class="text-xs text-neutral-500 dark:text-neutral-400">
            <th class="py-1 pr-4 font-medium" title="The peak's place in the strongest-first ranking the top-X selected on.">#</th>
            <th class="py-1 pr-4 font-medium">
              <PeakSortHeader label="Frequency" column="frequency" :sort="otherPeaksSort" @sort="onSort" />
            </th>
            <th class="py-1 pr-4 font-medium">
              <PeakSortHeader label="Level" column="level" :sort="otherPeaksSort" @sort="onSort" />
            </th>
            <th class="py-1 font-medium">
              <PeakSortHeader label="dBc" column="dbc" :sort="otherPeaksSort" @sort="onSort" />
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in sortedRows" :key="row.rank" class="border-t border-neutral-100 font-mono dark:border-neutral-800">
            <td class="py-1 pr-4 text-neutral-400 dark:text-neutral-600">{{ row.rank }}</td>
            <td class="py-1 pr-4">{{ row.freqHz.toFixed(1) }} Hz</td>
            <td class="py-1 pr-4">{{ row.dbv.toFixed(2) }} dBV</td>
            <td class="py-1">{{ row.dbc.toFixed(1) }} dB</td>
          </tr>
        </tbody>
      </table>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Waiting for data…</div>
  </CollapsibleSection>
</template>
