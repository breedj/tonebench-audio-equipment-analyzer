<script setup lang="ts">
/**
 * One of the pills at the foot of a settings panel: how many points the run will take, or how long
 * it will take. Joost's 2026-10-12 request, from the Sweep screenshot — the two figures anyone
 * checks before pressing RUN, as badges rather than buried in the sentence that used to carry them.
 *
 * `kind` picks the glyph and nothing else: the pill's tint is `.tb-badge`'s, because these are two
 * halves of one estimate and colouring them apart would imply a distinction that is not there.
 */
const props = defineProps<{
  /** `points` is a count of measurements; `time` is a duration. */
  kind: 'points' | 'time'
  title?: string
}>()
</script>

<template>
  <span class="tb-badge" :title="props.title">
    <!-- A row of dots for a count of points, a clock face for a duration. 12 px, so the pill stays
         the height of the text beside it. -->
    <svg
      v-if="props.kind === 'points'"
      width="12"
      height="12"
      viewBox="0 0 24 24"
      fill="currentColor"
      aria-hidden="true"
    >
      <circle cx="5" cy="12" r="2" />
      <circle cx="12" cy="12" r="2" />
      <circle cx="19" cy="12" r="2" />
    </svg>
    <svg
      v-else
      width="12"
      height="12"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="2.2"
      stroke-linecap="round"
      stroke-linejoin="round"
      aria-hidden="true"
    >
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3 2" />
    </svg>
    <span class="font-mono"><slot /></span>
  </span>
</template>
