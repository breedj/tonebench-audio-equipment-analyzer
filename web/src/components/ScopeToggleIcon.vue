<script setup lang="ts">
import { computed } from 'vue'
import { TOGGLE_DOT_RADIUS, TOGGLE_ICON_SIZE, scopeToggleIcon } from '../scope/displayIcons'

/**
 * One display toggle's figure.
 *
 * <p>The shapes are data (<code>scope/displayIcons.ts</code>); this only draws them. Everything is
 * in <code>currentColor</code>, so a figure reads the same way lit and unlit, in either theme, and
 * in a monochrome screenshot — the button's own background is what says which it is.</p>
 */

const props = withDefaults(defineProps<{ name: string; size?: number }>(), { size: 16 })

const shapes = computed(() => scopeToggleIcon(props.name))

/**
 * The dash a <code>dash</code> shape is stroked with, matching <code>ScopeMeasurementIcon</code>'s.
 *
 * <p>Repeated as a figure rather than shared, because the two sets are drawn in boxes of different
 * sizes and what has to match is the <em>look</em> at the size each is used at, not the number.</p>
 */
const DASH = '2 1.5'
</script>

<template>
  <svg
    :viewBox="`0 0 ${TOGGLE_ICON_SIZE} ${TOGGLE_ICON_SIZE}`"
    :width="size"
    :height="size"
    fill="none"
    stroke="currentColor"
    stroke-width="1.8"
    stroke-linecap="round"
    stroke-linejoin="round"
    class="shrink-0"
    aria-hidden="true"
  >
    <template v-for="(shape, i) in shapes" :key="i">
      <path
        v-if="shape.t === 'path'"
        :d="shape.d"
        :stroke-width="shape.w"
        :stroke-dasharray="shape.dash ? DASH : undefined"
      />
      <circle
        v-else
        :cx="shape.x"
        :cy="shape.y"
        :r="shape.r ?? TOGGLE_DOT_RADIUS"
        fill="currentColor"
        stroke="none"
      />
    </template>
  </svg>
</template>
