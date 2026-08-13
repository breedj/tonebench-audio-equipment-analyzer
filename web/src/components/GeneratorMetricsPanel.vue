<script setup lang="ts">
import { computed } from 'vue'
import { generatorTile, GEN_METRIC_LABEL } from '../metrics'
import { useGeneratorStimulus } from '../composables/useGeneratorStimulus'
import { useMetricSelection } from '../composables/useMetricSelection'

// One box for both channels — Left and Right always play the same stimulus (see
// GeneratorPanel.vue), so a per-channel "L: Gen"/"R: Gen" pair would just be the same values
// twice. Reads the *staged* form (useGeneratorStimulus.ts), not DeviceStatus, so this keeps
// showing the configured stimulus after Stop instead of blanking out with the silenced generator.
const { kind, tones, multitone, noise } = useGeneratorStimulus()
const { selectedMetrics, removeMetric } = useMetricSelection()

const tile = computed(() => (selectedMetrics.value.includes(GEN_METRIC_LABEL) ? generatorTile(kind.value, tones, multitone, noise) : null))
</script>

<template>
  <div
    v-if="tile"
    class="min-w-[9rem] rounded-xl border border-l-4 border-neutral-200 border-l-violet-500 bg-white px-3 py-2 shadow-sm dark:border-neutral-800 dark:border-l-violet-400 dark:bg-neutral-900"
  >
    <div class="mb-1 flex items-center justify-between gap-3">
      <span class="text-xs text-neutral-500 dark:text-neutral-400">{{ tile.label }}</span>
      <button
        type="button"
        class="shrink-0 text-neutral-400 hover:text-red-500 dark:text-neutral-600 dark:hover:text-red-400"
        title="Remove this metric"
        aria-label="Remove Gen"
        @click="removeMetric(GEN_METRIC_LABEL)"
      >
        <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" aria-hidden="true">
          <path d="M18 6 6 18M6 6l12 12" />
        </svg>
      </button>
    </div>
    <div class="whitespace-pre-line font-mono text-lg">{{ tile.value }}</div>
  </div>
</template>
