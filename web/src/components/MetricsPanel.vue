<script setup lang="ts">
import { computed, ref } from 'vue'
import type { MetersFrame } from '../streaming/protocol'
import type { GeneratorTone } from '../api/client'
import { channelTiles } from '../metrics'

const props = defineProps<{
  channelLabel: 'L' | 'R'
  meters: MetersFrame | null
  generatorTones: GeneratorTone[]
}>()

const loadOhms = ref(8)

const tiles = computed(() => channelTiles(props.meters, props.generatorTones, loadOhms.value))
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <div class="mb-2 flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>{{ channelLabel }}: Metrics</span>
      <label class="flex items-center gap-1">
        Load
        <input
          type="number"
          min="0.1"
          step="0.1"
          v-model.number="loadOhms"
          class="w-14 rounded border border-neutral-300 px-1 py-0.5 dark:border-neutral-700 dark:bg-neutral-800"
        />
        Ω
      </label>
    </div>

    <div v-if="tiles.length" class="grid grid-cols-2 gap-3 text-sm sm:grid-cols-3">
      <div v-for="tile in tiles" :key="tile.label">
        <div class="text-xs text-neutral-500 dark:text-neutral-400">{{ channelLabel }}: {{ tile.label }}</div>
        <div class="whitespace-pre-line font-mono">{{ tile.value }}</div>
      </div>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Waiting for data…</div>
  </section>
</template>
