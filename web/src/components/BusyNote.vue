<script setup lang="ts">
/**
 * A spinner and a line of text, for the stretches where the app is waiting on the server with
 * nothing to show yet.
 *
 * <p>Deliberately says <em>what</em> it is waiting for rather than spinning anonymously: an import
 * of a 51-run stepped simulation takes about twenty seconds, and "Analysing 51 runs…" is the
 * difference between waiting and wondering whether the click registered. There is no progress
 * figure because the server reports none — a bar that advanced on a guess would be worse than the
 * spinner, since it would be claiming to know.</p>
 */
defineProps<{ label: string }>()
</script>

<template>
  <div class="flex items-center gap-2 text-sm text-neutral-600 dark:text-neutral-400" role="status" aria-live="polite">
    <span
      class="inline-block size-4 shrink-0 animate-spin rounded-full border-2 border-neutral-300 border-t-transparent dark:border-neutral-600 dark:border-t-transparent"
      aria-hidden="true"
    ></span>
    <span>{{ label }}</span>
  </div>
</template>
