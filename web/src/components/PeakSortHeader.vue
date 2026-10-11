<script setup lang="ts">
import { computed } from 'vue'
import { peakSortIndicator, type PeakSort, type PeakSortColumn } from '../measurements/peakSort'

/**
 * One sortable column header for the Harmonics and Other peaks tables. Shared rather than written
 * twice: the two tables sit side by side on the same screen, and a header that looked or behaved
 * differently in one of them would read as a different control.
 *
 * The `<th>` itself stays in the caller's table (its padding is part of that table's column grid);
 * this is only the clickable label inside it.
 */
const props = defineProps<{ label: string; column: PeakSortColumn; sort: PeakSort | null }>()
const emit = defineEmits<{ sort: [column: PeakSortColumn] }>()

const indicator = computed(() => peakSortIndicator(props.sort, props.column))

/**
 * What the next click does, spelled out — the three-state cycle (this column's own direction, the
 * other one, then back to the table's natural order) is not guessable from an arrow.
 */
const hint = computed(() =>
  indicator.value === null
    ? `Sort by ${props.label.toLowerCase()}`
    : indicator.value === '▲'
      ? `Sorted by ${props.label.toLowerCase()}, lowest first — click to reverse`
      : `Sorted by ${props.label.toLowerCase()}, highest first — click to reverse`,
)
</script>

<template>
  <button
    type="button"
    class="inline-flex items-center gap-1 font-medium hover:text-neutral-800 dark:hover:text-neutral-200"
    :class="indicator ? 'text-neutral-700 dark:text-neutral-200' : ''"
    :title="hint"
    :aria-label="hint"
    @click="emit('sort', props.column)"
  >
    {{ label }}
    <!-- A fixed-width slot for the arrow, so the column does not jump sideways as the sort moves
         from one header to the next. -->
    <span class="w-2 text-[0.6rem] leading-none">{{ indicator ?? '' }}</span>
  </button>
</template>
