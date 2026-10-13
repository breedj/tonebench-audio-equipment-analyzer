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

/** Collapsed to its first line until asked. Per mount, so it is not remembered across screens — a
 *  hint that stayed open would take three lines off a chart nobody had asked it about. */
const expanded = ref(false)

const hint = computed(() => {
  const kind = types.value.find((t) => t.id === currentTypeId.value)?.kind
  return simulatorHint(kind, status.value)
})
</script>

<template>
  <!-- One line, with the rest behind More — Joost's 2026-10-12 design. It used to wrap to three
       lines above the chart, which on a short window cost more of the plot than the hint was worth;
       the first clause is the whole of what a reader needs to decide whether to read the rest.
       `role="status"` stays on the container so the expansion is announced rather than the text
       being re-read from the top. -->
  <div
    v-if="hint"
    class="flex items-start gap-2 rounded-[10px] border border-amber-300 bg-amber-50 py-1.5 pl-3 pr-1.5 text-xs text-amber-900 dark:border-amber-700 dark:bg-amber-950/40 dark:text-amber-200"
    role="status"
  >
    <span class="flex h-5 shrink-0 items-center text-amber-600 dark:text-amber-400">
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <circle cx="12" cy="12" r="9" />
        <path d="M12 11v5M12 8h.01" />
      </svg>
    </span>
    <p class="m-0 min-w-0 flex-1 leading-5" :class="expanded ? '' : 'truncate'">
      <span class="font-semibold">Simulator:</span> {{ hint }}
    </p>
    <button
      type="button"
      class="h-5 shrink-0 rounded px-2 text-xs font-semibold text-amber-800 hover:bg-amber-100 dark:text-amber-300 dark:hover:bg-amber-900/40"
      :aria-expanded="expanded"
      @click="expanded = !expanded"
    >
      {{ expanded ? 'Less' : 'More' }}
    </button>
  </div>
</template>
