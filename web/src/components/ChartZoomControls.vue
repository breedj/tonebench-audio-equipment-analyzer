<script setup lang="ts">
import ToolbarIcon from './ToolbarIcon.vue'

/**
 * The three things a zoomed chart offers: step back one zoom, make the zoom the axis window, and
 * drop back to the display panel's own window.
 *
 * <p>One component because six charts carried the same Reset zoom button with the same classes and
 * three different spellings of its title, and because the two added on 2026-10-18 would otherwise
 * have been copied six times each. Each chart keeps its own notion of "zoomed" and its own history
 * — there are two of the former, for the reason `useZoomBounds` documents — and passes them in.</p>
 */
defineProps<{
  /** Whether the axes show anything other than the window the display panel asks for. */
  zoomed: boolean
  /** Whether there is a previous zoom to step back to (`useZoomHistory`). */
  canGoBack: boolean
  /** What this chart's Reset goes back to, in its own words — "the full frequency span", "the time and level window set in the display panel". */
  resetTitle: string
  /** What Set axis writes into, in its own words. Left out where the chart has no panel boxes to write. */
  applyTitle?: string
}>()

defineEmits<{ back: []; apply: []; reset: [] }>()
</script>

<template>
  <!-- `contents`, so the three buttons are laid out by the header row they sit in rather than by a
       wrapper of their own: every one of these headers is a flex row with a readout that has to
       keep the space between. -->
  <span v-if="zoomed" class="contents">
    <button
      v-if="canGoBack"
      type="button"
      class="inline-flex shrink-0 items-center gap-1 rounded border px-2 py-0.5 btn-action"
      title="Back to the previous zoom."
      @click="$emit('back')"
    >
      <ToolbarIcon name="zoom-back" />
      Back
    </button>
    <button
      v-if="applyTitle"
      type="button"
      class="inline-flex shrink-0 items-center gap-1 rounded border px-2 py-0.5 btn-action"
      :title="applyTitle"
      @click="$emit('apply')"
    >
      <ToolbarIcon name="set-axis" />
      Set axis
    </button>
    <button
      type="button"
      class="inline-flex shrink-0 items-center gap-1 rounded border px-2 py-0.5 btn-action"
      :title="resetTitle"
      @click="$emit('reset')"
    >
      <ToolbarIcon name="reset-zoom" />
      Reset zoom
    </button>
  </span>
</template>
