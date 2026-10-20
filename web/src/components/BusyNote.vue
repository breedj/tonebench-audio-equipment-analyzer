<script setup lang="ts">
/**
 * A spinner and a line of text, for the stretches where the app is waiting on the server with
 * nothing to show yet.
 *
 * <p>Deliberately says <em>what</em> it is waiting for rather than spinning anonymously: an import
 * of a 51-run stepped simulation takes about twenty seconds, and "Analysing 51 runs…" is the
 * difference between waiting and wondering whether the click registered.</p>
 *
 * <p>With `done` and `total` it also says <em>how far</em>, as "13 of 61" and a bar. That needs the
 * server to report it — a bar advancing on a guess would be worse than none, since it would be
 * claiming to know — and it does, through `SimulationImportProgress`, because a 61-run import of a
 * 220 MB file takes over a minute and a spinner that long reads as a stall (Joost, 2026-10-20).</p>
 */
const props = defineProps<{
  label: string
  /** Steps finished, where the task reports them. */
  done?: number | null
  /** Steps in total; with `done`, this is what turns the spinner into a progress bar. */
  total?: number | null
}>()

import { computed } from 'vue'

const counted = computed(
  () => typeof props.done === 'number' && typeof props.total === 'number' && props.total > 0,
)

/** Clamped, because a count that ran past its total would draw a bar wider than its track. */
const fraction = computed(() =>
  counted.value ? Math.min(1, Math.max(0, (props.done ?? 0) / (props.total ?? 1))) : 0,
)
</script>

<template>
  <div class="text-sm text-neutral-600 dark:text-neutral-400" role="status" aria-live="polite">
    <div class="flex items-center gap-2">
      <span
        class="inline-block size-4 shrink-0 animate-spin rounded-full border-2 border-neutral-300 border-t-transparent dark:border-neutral-600 dark:border-t-transparent"
        aria-hidden="true"
      ></span>
      <span>{{ label }}</span>
    </div>

    <!--
      The bar is the same track the measurement status bar uses, so progress reads the same
      wherever it appears. Under the line rather than beside it: the label is a sentence and a bar
      squeezed next to it would be too short to read anything off.
    -->
    <div v-if="counted" class="tb-progress mt-2" role="presentation">
      <div
        class="h-full rounded-full bg-neutral-500 transition-[width] duration-300 dark:bg-neutral-400"
        :style="{ width: `${(fraction * 100).toFixed(1)}%` }"
      ></div>
    </div>
  </div>
</template>
