<script setup lang="ts">
import { computed } from 'vue'
import {
  TOGGLE_DOT_RADIUS,
  TOGGLE_ICON_SIZE,
  scopeToggleIcon,
  scopeToggleShapeIsMark,
  type ScopeToggleShape,
} from '../scope/displayIcons'

/**
 * One display toggle's figure.
 *
 * <p>The shapes are data (<code>scope/displayIcons.ts</code>); this only draws them. A shape is in
 * <code>currentColor</code> unless it names an <code>ink</code> of its own, so almost every figure
 * reads the same way lit and unlit, in either theme, and in a monochrome screenshot — the button's
 * own background is what says which it is. The exception is a figure that was supplied as a drawing
 * and carries that drawing's colours; <code>ScopeToggleShape</code>'s <code>ink</code> says why.</p>
 *
 * <p><b><code>mark</code> is the two-colour reading of the same shapes</b>, which the stimulus
 * picker's tiles and the button that opens it ask for, and the display rail does not — those are
 * lit/unlit state buttons, where colour already means "on". The signal keeps the current colour and
 * everything annotating it — a zero line, an envelope, a crossing, a width being dimensioned —
 * takes the mark colour. The split is <code>scopeToggleShapeIsMark</code>'s and is in the data
 * already, so nothing here decides what counts as signal. Without it the component draws precisely
 * what it always did.</p>
 */

const props = withDefaults(
  defineProps<{
    name: string
    size?: number
    /** A CSS colour for the annotation on the signal. Absent means the one-colour figure. */
    mark?: string
  }>(),
  { size: 16, mark: undefined },
)

const shapes = computed(() => scopeToggleIcon(props.name))

/**
 * What a shape is drawn in: its own ink where it names one, else the mark colour where this is a
 * two-colour drawing and the shape is an annotation, else <code>currentColor</code>.
 *
 * <p>The shape's own ink is first because it is the more specific statement of the two — see
 * <code>ScopeToggleShape</code>.</p>
 */
function ink(shape: ScopeToggleShape): string | undefined {
  if (shape.ink !== undefined) return shape.ink
  return props.mark !== undefined && scopeToggleShapeIsMark(shape) ? props.mark : undefined
}

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
        :stroke="ink(shape)"
        :stroke-width="shape.w"
        :stroke-dasharray="shape.dash ? DASH : undefined"
      />
      <circle
        v-else
        :cx="shape.x"
        :cy="shape.y"
        :r="shape.r ?? TOGGLE_DOT_RADIUS"
        :fill="ink(shape) ?? 'currentColor'"
        stroke="none"
      />
    </template>
  </svg>
</template>
