<script setup lang="ts">
// The expand / contract button in every result chart's corner: four arrows to the corners pins
// the chart's root element over the whole window, four arrows to the centre brings it back.
// Escape brings it back too. The chart's own height composable reads the expanded state, so the
// plot fills the window rather than keeping the height it had in the page. See useExpandedChart
// for why this is not the browser's Fullscreen API.
//
// 28 px around an 18 px glyph since 2026-09-30, up from 20/14: it is the only way back from an
// expanded chart besides Escape, and at the old size it was a hard thing to hit. Every chart header
// reserves `pr-8` for it, which is that width — it is positioned absolutely, so nothing else makes
// room for it — `pr-9` rather than the button's own 28 px, so the title beside it keeps a gap.
import { toRef } from 'vue'
import { useExpandedChart } from '../composables/useExpandedChart'

const props = defineProps<{ target: HTMLElement | null | undefined }>()
const { active, toggle } = useExpandedChart(toRef(props, 'target'))
</script>

<template>
  <button
    type="button"
    class="flex h-7 w-7 shrink-0 items-center justify-center rounded text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
    :title="active ? 'Back to the page (Esc)' : 'Expand this chart to the whole window'"
    :aria-label="active ? 'Contract chart' : 'Expand chart'"
    :aria-pressed="active"
    @click="toggle"
  >
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      <!-- Expand: each arrow leaves the centre for its corner. Contract: each arrow comes in from
           its corner and stops at the centre. -->
      <template v-if="active">
        <path d="M10 4v6H4" /><path d="M10 10 4 4" />
        <path d="M14 4v6h6" /><path d="m14 10 6-6" />
        <path d="M20 14h-6v6" /><path d="m14 14 6 6" />
        <path d="M4 14h6v6" /><path d="m10 14-6 6" />
      </template>
      <template v-else>
        <path d="M4 10V4h6" /><path d="m4 4 6 6" />
        <path d="M14 4h6v6" /><path d="m20 4-6 6" />
        <path d="M20 14v6h-6" /><path d="m20 20-6-6" />
        <path d="M10 20H4v-6" /><path d="m4 20 6-6" />
      </template>
    </svg>
  </button>
</template>
