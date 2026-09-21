<script setup lang="ts">
import { computed, inject, ref } from 'vue'
import { useProjects } from '../composables/useProjects'
import { DEVICE_STATUS_KEY, simulatorHint } from '../simulator/hints'

/**
 * One line above the chart, in simulated mode only, whenever the simulator's status-bar setup
 * makes the open measurement's result trivial — and what to change. See simulator/hints.ts for
 * the rules; this only wires them to the open measurement and the polled device status. Rendered
 * by MeasurementLayout so every screen carries it without each one saying so.
 */
const status = inject(DEVICE_STATUS_KEY, ref(null))
const { types, currentTypeId } = useProjects()

const hint = computed(() => {
  const kind = types.value.find((t) => t.id === currentTypeId.value)?.kind
  return simulatorHint(kind, status.value)
})
</script>

<template>
  <p
    v-if="hint"
    class="rounded border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-700 dark:bg-amber-950/40 dark:text-amber-300"
    role="status"
  >
    <span class="font-medium">Simulator:</span> {{ hint }}
  </p>
</template>
