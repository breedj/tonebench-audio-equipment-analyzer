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

const props = defineProps<{ name: string }>()

const shapes = computed(() => scopeToggleIcon(props.name))
</script>

<template>
  <svg
    :viewBox="`0 0 ${TOGGLE_ICON_SIZE} ${TOGGLE_ICON_SIZE}`"
    width="16"
    height="16"
    fill="none"
    stroke="currentColor"
    stroke-width="1.8"
    stroke-linecap="round"
    stroke-linejoin="round"
    class="shrink-0"
    aria-hidden="true"
  >
    <template v-for="(shape, i) in shapes" :key="i">
      <path v-if="shape.t === 'path'" :d="shape.d" :stroke-width="shape.w" />
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
