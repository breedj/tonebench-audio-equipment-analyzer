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
import ScopeToggleIcon from './ScopeToggleIcon.vue'
import type { SegmentedOption } from '../panels/segmented'

const props = defineProps<{
  options: readonly SegmentedOption<T>[]
  /** Columns in the track's grid. Defaults to one per option, i.e. a single row. */
  columns?: number
  /** Names the group for a screen reader. Spelled `groupLabel` and not `ariaLabel`, which collides
   *  with the native `aria-label` attribute when written in kebab case at a call site. */
  groupLabel: string
}>()

const model = defineModel<T>({ required: true })

const columnCount = computed(() => props.columns ?? props.options.length)

/**
 * An option's own geometry and, where it carries a dot, its own colour when it is the chosen one.
 *
 * <p>The lit label takes the dot's hue rather than the track's pink, because on the one control
 * that is about <em>traces</em> a blue dot beside a pink word reads as two different things being
 * said. Inline, so it beats `.tb-segment-on`'s class.</p>
 */
function buttonStyle(option: SegmentedOption<T>): Record<string, string> | undefined {
  const style: Record<string, string> = {}
  if (option.span && option.span > 1) style.gridColumn = `span ${option.span} / span ${option.span}`
  if (option.dot && model.value === option.value) style.color = option.dot
  return Object.keys(style).length > 0 ? style : undefined
}
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
      :style="buttonStyle(option)"
      :aria-pressed="model === option.value"
      :disabled="option.disabled"
      :title="option.title"
      @click="model = option.value"
    >
      <!-- A figure or a dot, never both: each says which option this is, and two of them say it
           twice. The figure inherits, so `buttonStyle`'s colour reaches it when this is the one
           that is on. -->
      <ScopeToggleIcon v-if="option.icon" :name="option.icon" :size="16" />
      <span
        v-else-if="option.dot"
        class="size-2 shrink-0 rounded-full"
        :style="{ backgroundColor: option.dot }"
        aria-hidden="true"
      ></span>
      {{ option.label }}
      <!-- The count takes the option's own dot colour where it has one, so the badge says which
           trace it is counting without a second legend. -->
      <span
        v-if="option.badge"
        class="ml-0.5 inline-block min-w-[18px] rounded-full px-1.5 text-[11px] font-semibold leading-[18px] text-white"
        :style="{ backgroundColor: option.dot ?? 'var(--kind-blue-chip-fg)' }"
        >{{ option.badge }}</span
      >
    </button>
  </div>
</template>
