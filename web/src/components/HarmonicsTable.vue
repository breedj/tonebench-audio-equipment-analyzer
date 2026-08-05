<script setup lang="ts">
import { computed } from 'vue'
import type { MetersFrame } from '../streaming/protocol'

const props = defineProps<{ meters: Record<number, MetersFrame>; channel: 0 | 1; channelLabel: 'L' | 'R' }>()

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
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <div class="mb-2 text-xs text-neutral-500 dark:text-neutral-400">{{ channelLabel }}: Harmonics</div>

    <div v-if="rows.length" class="overflow-x-auto">
      <table class="w-full text-left text-sm">
        <thead>
          <tr class="text-xs text-neutral-500 dark:text-neutral-400">
            <th class="py-1 pr-4 font-medium">Order</th>
            <th class="py-1 pr-4 font-medium">Frequency</th>
            <th class="py-1 pr-4 font-medium">Level</th>
            <th class="py-1 font-medium">dBc</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.label" class="border-t border-neutral-100 font-mono dark:border-neutral-800">
            <td class="py-1 pr-4">{{ row.label }}</td>
            <td class="py-1 pr-4">{{ row.freqHz.toFixed(1) }} Hz</td>
            <td class="py-1 pr-4">{{ row.dbv.toFixed(2) }} dBV</td>
            <td class="py-1">{{ row.label === 'Fundamental' ? '—' : `${row.dbc.toFixed(1)} dB` }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Waiting for data…</div>
  </section>
</template>
