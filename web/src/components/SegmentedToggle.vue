<script setup lang="ts" generic="T">
/**
 * The segmented toggle from Joost's 2026-10-12 mockups: a recessed track with the selected option
 * lifted out of it on its own white (dark: raised) ground. Projection, View, Units, Radius, Surface,
 * Direction — every "which one of these is on" control in a display rail, which before this were
 * eleven hand-rolled button rows that had each drifted a little from the others.
 *
 * `columns` and an option's own `span` are the caller's, not the component's: how many columns five
 * projections need over two rows is a question about those five labels, and the Projection group
 * answers it with three spanning two columns over two spanning three. Everything else — the track,
 * the heights, the selected lift — is fixed here so it cannot drift again.
 *
 * Generic over the value type so `v-model` keeps a union (`'Bode' | 'Nyquist' | …`) rather than
 * widening to string at every call site.
 */
import { computed } from 'vue'
import type { SegmentedOption } from '../panels/segmented'

const props = defineProps<{
  options: SegmentedOption<T>[]
  /** Columns in the track's grid. Defaults to one per option, i.e. a single row. */
  columns?: number
  /** Names the group for a screen reader. Spelled `groupLabel` and not `ariaLabel`, which collides
   *  with the native `aria-label` attribute when written in kebab case at a call site. */
  groupLabel: string
}>()

const model = defineModel<T>({ required: true })

const columnCount = computed(() => props.columns ?? props.options.length)
</script>

<template>
  <div
    class="tb-segment-track grid"
    :style="{ gridTemplateColumns: `repeat(${columnCount}, minmax(0, 1fr))` }"
    role="group"
    :aria-label="groupLabel"
  >
    <button
      v-for="option in options"
      :key="String(option.value)"
      type="button"
      class="tb-segment"
      :class="model === option.value ? 'tb-segment-on' : ''"
      :style="option.span && option.span > 1 ? { gridColumn: `span ${option.span} / span ${option.span}` } : undefined"
      :aria-pressed="model === option.value"
      :disabled="option.disabled"
      :title="option.title"
      @click="model = option.value"
    >
      {{ option.label }}
    </button>
  </div>
</template>
