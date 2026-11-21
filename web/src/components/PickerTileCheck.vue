<script setup lang="ts">
/**
 * The tick that says a picker tile is the chosen one.
 *
 * <p>A filled circle rather than a border alone, because the three pickers all tint a chosen tile
 * and a tint is the one signal that does not survive a monochrome screenshot or a colour-blind
 * reader — the same reasoning the display rail's lit buttons carry.</p>
 *
 * <p><code>corner</code> hangs it off the tile's top-right, clear of the text, which is what the
 * two single-choice dialogs want: one tile is chosen and the badge should be unmissable.
 * <code>inline</code> sits it at the end of the row, for the measurement picker, where a dozen
 * tiles may be ticked at once and a dozen badges hanging over the gaps would be noise.</p>
 */
withDefaults(defineProps<{ placement?: 'corner' | 'inline' }>(), { placement: 'corner' })
</script>

<template>
  <span
    class="flex items-center justify-center rounded-full bg-blue-600 text-white dark:bg-blue-500"
    :class="
      placement === 'corner'
        ? 'absolute -right-[7px] -top-[7px] size-5 border-2 border-[var(--surface-panel)]'
        : 'size-[18px] shrink-0'
    "
    aria-hidden="true"
  >
    <svg
      :width="placement === 'corner' ? 12 : 11"
      :height="placement === 'corner' ? 12 : 11"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="3"
      stroke-linecap="round"
      stroke-linejoin="round"
    >
      <path d="M5 12.5l4.5 4.5L19 7.5" />
    </svg>
  </span>
</template>
