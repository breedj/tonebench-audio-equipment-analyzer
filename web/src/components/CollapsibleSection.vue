<script setup lang="ts">
/**
 * Shared collapsible panel chrome: a bordered box with a title and a disclosure triangle in the
 * top-right corner. The triangle (and the title itself) toggle the body; `header-right` is for
 * controls that must stay clickable without also toggling (date pickers, a count badge, a numeric
 * input) — see MeasurementHistoryPanel/OtherPeaksTable/MeasurementChecklist for examples.
 */
import { ref } from 'vue'

const props = withDefaults(
  defineProps<{
    title: string
    size?: 'xs' | 'sm'
    padding?: 'p-3' | 'p-4'
    defaultOpen?: boolean
  }>(),
  { size: 'sm', padding: 'p-3', defaultOpen: true },
)

const open = ref(props.defaultOpen)
</script>

<template>
  <section class="panel-surface rounded-lg border border-neutral-200 dark:border-neutral-800" :class="padding">
    <div class="flex flex-wrap items-center justify-between gap-2" :class="{ 'mb-2': size === 'xs' && open, 'mb-3': size === 'sm' && open }">
      <button type="button" class="flex min-w-0 items-center gap-2 text-left" @click="open = !open">
        <h2
          class="font-semibold text-neutral-900 dark:text-neutral-100"
          :class="size === 'xs' ? 'text-xs' : 'text-sm'"
        >
          {{ title }}
        </h2>
      </button>
      <div class="flex items-center gap-2">
        <slot name="header-right" />
        <button
          type="button"
          class="shrink-0 rounded p-0.5 text-neutral-500 hover:text-neutral-700 dark:text-neutral-400 dark:hover:text-neutral-200"
          :aria-expanded="open"
          :title="open ? 'Collapse' : 'Expand'"
          @click="open = !open"
        >
          <svg
            class="h-2.5 w-2.5 transition-transform duration-150"
            :class="{ '-rotate-90': !open }"
            viewBox="0 0 10 6"
            fill="currentColor"
            aria-hidden="true"
          >
            <path d="M0 0h10L5 6z" />
          </svg>
        </button>
      </div>
    </div>
    <div v-show="open" class="flex flex-col gap-3">
      <slot />
    </div>
  </section>
</template>
