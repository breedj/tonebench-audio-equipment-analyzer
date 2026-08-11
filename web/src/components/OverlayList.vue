<script setup lang="ts">
import { useMeasurements } from '../composables/useMeasurements'

const { overlays, removeOverlay, setOverlayVisible } = useMeasurements()

function hasChannel(o: (typeof overlays.value)[number], ch: number): boolean {
  return o.detail.channelsData.some((c) => c.channel === ch)
}
</script>

<template>
  <section v-if="overlays.length > 0" class="flex flex-col gap-1.5 rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <h2 class="text-sm font-semibold text-neutral-900 dark:text-neutral-100">Overlays</h2>
    <div
      v-for="overlay in overlays"
      :key="overlay.id"
      class="flex items-center gap-2 text-sm"
      :class="{ 'opacity-40': !overlay.visible }"
    >
      <!-- Dashed-L / dotted-R swatch, matching how the chart actually draws this overlay's two channels. -->
      <svg width="28" height="14" viewBox="0 0 28 14" class="shrink-0">
        <line x1="1" y1="5" x2="27" y2="5" :stroke="overlay.color" stroke-width="1.5" stroke-dasharray="4,2" />
        <line x1="1" y1="10" x2="27" y2="10" :stroke="overlay.color" stroke-width="1.5" stroke-dasharray="1,3" />
      </svg>

      <span class="min-w-0 flex-1 truncate">
        {{ overlay.detail.name }}
        <span class="text-xs text-neutral-400 dark:text-neutral-500">
          <template v-if="hasChannel(overlay, 0)">L</template>
          <template v-if="hasChannel(overlay, 1)">R</template>
        </span>
      </span>

      <button
        type="button"
        class="text-neutral-500 hover:text-neutral-900 dark:hover:text-neutral-100"
        :title="overlay.visible ? 'Hide' : 'Show'"
        @click="setOverlayVisible(overlay.id, !overlay.visible)"
      >
        <svg v-if="overlay.visible" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7Z" />
          <circle cx="12" cy="12" r="3" />
        </svg>
        <svg v-else width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M17.94 17.94A10.94 10.94 0 0 1 12 19c-7 0-11-7-11-7a21.6 21.6 0 0 1 5.06-5.94M9.9 4.24A10.4 10.4 0 0 1 12 4c7 0 11 7 11 7a21.7 21.7 0 0 1-2.61 3.68M14.12 14.12a3 3 0 1 1-4.24-4.24" />
          <line x1="1" y1="1" x2="23" y2="23" />
        </svg>
      </button>

      <button
        type="button"
        class="text-neutral-400 hover:text-red-600"
        title="Remove"
        @click="removeOverlay(overlay.id)"
      >
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0-1 14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2L4 6h16Z" />
        </svg>
      </button>
    </div>
  </section>
</template>
