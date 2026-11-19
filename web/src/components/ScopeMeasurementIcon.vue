<script setup lang="ts">
import { computed } from 'vue'
import { ICON_HEIGHT, ICON_WIDTH, scopeMeasurementIcon } from '../scope/measurementIcons'

/**
 * One measurement's figure — a waveform with the quantity marked on it.
 *
 * <p>The shapes are data (<code>scope/measurementIcons.ts</code>); this only draws them. The
 * backdrop trace is faint and the mark is in the current colour, so a figure reads the same way
 * against a panel, inside a chosen card and in either theme without a palette of its own.</p>
 */

const props = defineProps<{ name: string }>()

const shapes = computed(() => scopeMeasurementIcon(props.name))

/** Half the arm of a dimension line's end tick, in the icon's own units. */
const TICK = 2
</script>

<template>
  <svg
    :viewBox="`0 0 ${ICON_WIDTH} ${ICON_HEIGHT}`"
    class="h-4 w-6 shrink-0 overflow-visible"
    fill="none"
    stroke="currentColor"
    stroke-width="1"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <template v-for="(shape, i) in shapes" :key="i">
      <path
        v-if="shape.t === 'path'"
        :d="shape.d"
        :stroke-opacity="shape.faint ? 0.4 : 1"
        :stroke-dasharray="shape.dash ? '2 1.5' : undefined"
      />
      <line
        v-else-if="shape.t === 'line'"
        :x1="shape.x1"
        :y1="shape.y1"
        :x2="shape.x2"
        :y2="shape.y2"
        :stroke-opacity="shape.faint ? 0.4 : 1"
        :stroke-dasharray="shape.dash ? '2 1.5' : undefined"
      />
      <rect
        v-else-if="shape.t === 'fill'"
        :x="shape.x"
        :y="shape.y"
        :width="shape.w"
        :height="shape.h"
        fill="currentColor"
        fill-opacity="0.22"
        stroke="none"
      />
      <!-- A dimension line: the span itself and a tick at each end, which is what makes it read as a
           measurement rather than as one more trace. -->
      <template v-else-if="shape.t === 'spanH'">
        <line :x1="shape.x1" :y1="shape.y" :x2="shape.x2" :y2="shape.y" />
        <line :x1="shape.x1" :y1="shape.y - TICK" :x2="shape.x1" :y2="shape.y + TICK" />
        <line :x1="shape.x2" :y1="shape.y - TICK" :x2="shape.x2" :y2="shape.y + TICK" />
      </template>
      <template v-else-if="shape.t === 'spanV'">
        <line :x1="shape.x" :y1="shape.y1" :x2="shape.x" :y2="shape.y2" />
        <line :x1="shape.x - TICK" :y1="shape.y1" :x2="shape.x + TICK" :y2="shape.y1" />
        <line :x1="shape.x - TICK" :y1="shape.y2" :x2="shape.x + TICK" :y2="shape.y2" />
      </template>
    </template>
  </svg>
</template>
