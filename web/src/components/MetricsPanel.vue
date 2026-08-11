<script setup lang="ts">
import { computed } from 'vue'
import type { MetersFrame } from '../streaming/protocol'
import type { GeneratorTone } from '../api/client'
import { channelTiles } from '../metrics'
import { useLoadOhms } from '../composables/useLoadOhms'

const props = defineProps<{
  channelLabel: 'L' | 'R'
  meters: MetersFrame | null
  generatorTones: GeneratorTone[]
}>()

// Shared with the other channel — see useLoadOhms.ts. Edited in the Generator pane, not here.
const { loadOhms } = useLoadOhms()

const tiles = computed(() => channelTiles(props.meters, props.generatorTones, loadOhms.value))
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <div class="mb-2 text-xs text-neutral-500 dark:text-neutral-400">{{ channelLabel }}: Metrics</div>

    <div v-if="tiles.length" class="grid grid-cols-2 gap-3 text-sm sm:grid-cols-3">
      <div v-for="tile in tiles" :key="tile.label">
        <div class="text-xs text-neutral-500 dark:text-neutral-400">{{ channelLabel }}: {{ tile.label }}</div>
        <div class="whitespace-pre-line font-mono">{{ tile.value }}</div>
      </div>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Waiting for data…</div>
  </section>
</template>
