<script setup lang="ts">
/**
 * A formatted reading with its unit set in grey beside the figure — "52.55 Hz", "4.41 dB",
 * "22.780 ms". The 2026-10-12 mockup's treatment of every number in the Measurements panel, and
 * what lets a column of readings be scanned for its numbers rather than read.
 *
 * The split is `splitUnit`'s (response/format.ts), so it is tested once rather than guessed at per
 * call site; this component is only the two spans.
 */
import { computed } from 'vue'
import { splitUnit } from '../response/format'

const props = defineProps<{ text: string }>()

const parts = computed(() => splitUnit(props.text))
</script>

<template>
  <span class="whitespace-nowrap font-mono text-neutral-900 dark:text-neutral-100">
    {{ parts.value }}
    <span v-if="parts.unit" class="text-neutral-500 dark:text-neutral-400">{{ parts.unit }}</span>
  </span>
</template>
