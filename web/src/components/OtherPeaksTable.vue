<script setup lang="ts">
import { computed, ref } from 'vue'
import type { MetersFrame } from '../streaming/protocol'

const props = defineProps<{ meters: Record<number, MetersFrame>; channel: 0 | 1; channelLabel: 'L' | 'R' }>()

const DEFAULT_COUNT = 10

const requestedCount = ref(DEFAULT_COUNT)

const available = computed(() => props.meters[props.channel]?.otherPeaks ?? [])

// Server already returns these strongest-first (see PeakFinder.cs); just take the top X.
const rows = computed(() => {
  const m = props.meters[props.channel]
  if (!m) return []

  const count = Math.max(1, Math.min(requestedCount.value, available.value.length))
  return available.value.slice(0, count).map((p) => ({
    freqHz: p.freqHz,
    dbv: m.fundamentalDbv + p.dbc,
    dbc: p.dbc,
  }))
})
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <div class="mb-2 flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>{{ channelLabel }}: Other peaks</span>
      <label class="flex items-center gap-2">
        Show top
        <input
          type="number"
          min="1"
          :max="Math.max(1, available.length)"
          v-model.number="requestedCount"
          class="w-16 rounded border border-neutral-300 px-2 py-0.5 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
    </div>

    <p class="mb-2 text-xs text-neutral-400 dark:text-neutral-600">
      Everything besides the fundamental and its harmonics — mains hum, mixing/intermodulation products, other spurs.
    </p>

    <div v-if="rows.length" class="overflow-x-auto">
      <table class="w-full text-left text-sm">
        <thead>
          <tr class="text-xs text-neutral-500 dark:text-neutral-400">
            <th class="py-1 pr-4 font-medium">#</th>
            <th class="py-1 pr-4 font-medium">Frequency</th>
            <th class="py-1 pr-4 font-medium">Level</th>
            <th class="py-1 font-medium">dBc</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, i) in rows" :key="i" class="border-t border-neutral-100 font-mono dark:border-neutral-800">
            <td class="py-1 pr-4 text-neutral-400 dark:text-neutral-600">{{ i + 1 }}</td>
            <td class="py-1 pr-4">{{ row.freqHz.toFixed(1) }} Hz</td>
            <td class="py-1 pr-4">{{ row.dbv.toFixed(2) }} dBV</td>
            <td class="py-1">{{ row.dbc.toFixed(1) }} dB</td>
          </tr>
        </tbody>
      </table>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Waiting for data…</div>
  </section>
</template>
