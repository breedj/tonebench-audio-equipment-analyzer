<script setup lang="ts">
import { computed } from 'vue'
import type { DeviceStatus } from '../api/client'
import type { MetersFrame } from '../streaming/protocol'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { systemTiles } from '../metrics'

const props = defineProps<{
  status: DeviceStatus | null
  /** Either channel's meters frame — binSizeHz is identical for both (same FFT config). */
  meters: MetersFrame | null
}>()

const { config } = useSpectrumConfig()

const tiles = computed(() =>
  config.value
    ? systemTiles({
        fftSize: config.value.fftSize,
        window: config.value.window,
        averaging: config.value.averaging,
        averageCount: config.value.averageCount,
        averagesTaken: props.meters?.averagesTaken,
        inputRangeDbv: props.status?.inputRangeDbv,
        binSizeHz: props.meters?.binSizeHz,
      })
    : [],
)
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <div class="mb-2 text-xs text-neutral-500 dark:text-neutral-400">System</div>
    <div v-if="tiles.length" class="grid grid-cols-2 gap-3 text-sm sm:grid-cols-5">
      <div v-for="tile in tiles" :key="tile.label">
        <div class="text-xs text-neutral-500 dark:text-neutral-400">{{ tile.label }}</div>
        <div class="font-mono">{{ tile.value }}</div>
      </div>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Loading…</div>
  </section>
</template>
