<script setup lang="ts">
import { computed } from 'vue'
import type { MetersFrame } from '../streaming/protocol'
import type { GeneratorTone } from '../api/client'
import { AVAILABLE_METRIC_LABELS, channelTiles, type MetricTile } from '../metrics'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useMetricSelection } from '../composables/useMetricSelection'

const props = defineProps<{
  channelLabel: 'L' | 'R'
  meters: MetersFrame | null
  generatorTones: GeneratorTone[]
}>()

// Shared with the other channel — see useLoadOhms.ts. Edited in the Generator pane, not here.
const { loadOhms } = useLoadOhms()
const { selectedMetrics, removeMetric } = useMetricSelection()

const allTiles = computed(() => channelTiles(props.meters, props.generatorTones, loadOhms.value))

// Selected fixed metrics (in the order they were picked), followed by any live GenN/Gen… tiles —
// those aren't part of the selectable catalogue (see metrics.ts), so they always show when present
// rather than needing to be added.
const tiles = computed(() => {
  const byLabel = new Map(allTiles.value.map((t) => [t.label, t]))
  const selected = selectedMetrics.value
    .map((label) => byLabel.get(label))
    .filter((t): t is MetricTile => t !== undefined)
  const generatorTiles = allTiles.value.filter((t) => !(AVAILABLE_METRIC_LABELS as readonly string[]).includes(t.label))
  return [...selected, ...generatorTiles]
})

function isRemovable(label: string): boolean {
  return (AVAILABLE_METRIC_LABELS as readonly string[]).includes(label)
}
</script>

<template>
  <div v-if="tiles.length" class="flex flex-wrap gap-3">
    <div
      v-for="tile in tiles"
      :key="tile.label"
      class="min-w-[9rem] rounded-xl border border-neutral-200 bg-white px-3 py-2 shadow-sm dark:border-neutral-800 dark:bg-neutral-900"
    >
      <div class="mb-1 flex items-center justify-between gap-3">
        <span class="text-xs text-neutral-500 dark:text-neutral-400">{{ channelLabel }}: {{ tile.label }}</span>
        <button
          v-if="isRemovable(tile.label)"
          type="button"
          class="shrink-0 text-neutral-400 hover:text-red-500 dark:text-neutral-600 dark:hover:text-red-400"
          title="Remove this metric"
          :aria-label="`Remove ${tile.label}`"
          @click="removeMetric(tile.label)"
        >
          <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" aria-hidden="true">
            <path d="M18 6 6 18M6 6l12 12" />
          </svg>
        </button>
      </div>
      <div class="whitespace-pre-line font-mono text-lg">{{ tile.value }}</div>
    </div>
  </div>
  <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">
    {{ props.meters ? 'No metrics selected — add some with the metrics button.' : 'Waiting for data…' }}
  </div>
</template>
