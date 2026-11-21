<script setup lang="ts">
/**
 * One math tile's figure: the two operands, faint, and the trace the operation makes of them.
 *
 * <p><b>The colours are the chart's own</b>, not a palette for this dialog: A takes the left
 * input's blue, B the right input's orange and the result the colour its own slot is drawn in —
 * so the picture of a math channel is in the same ink as the math channel. The design's reference
 * drew A and B as a generic blue/red pair; matching the plot instead is what lets a reader connect
 * the tile to the trace that will appear.</p>
 *
 * <p>The operands are drawn thin and at part opacity and the result at full weight, because the
 * result is what the tile is offering — the operands are there to say what it was made from.</p>
 */
import { computed } from 'vue'
import { MATH_FIGURE_HEIGHT, MATH_FIGURE_WIDTH, scopeMathFigure } from '../scope/mathFigures'
import type { ScopeMathSource } from '../scope/math'

const props = defineProps<{
  /** The tile's own operation, which is what picks the figure. */
  operation: string
  /** The two sockets as the dialog has them staged, so the operands are in the right inks. */
  sourceA: ScopeMathSource
  sourceB: ScopeMathSource
  /** The slot's own trace colour, for the result. */
  resultColor: string
}>()

/**
 * The chart's channel inks, repeated rather than imported: a dialog reaching into
 * `OscilloscopeScreenChart` to colour a 50-pixel figure is the wrong way round, and these two
 * values are the app's convention rather than that component's.
 */
const CHANNEL_INK = { ch0: '#3b82f6', ch1: '#f97316' } as const

const figure = computed(() => scopeMathFigure(props.operation))
</script>

<template>
  <svg
    v-if="figure"
    :viewBox="`0 0 ${MATH_FIGURE_WIDTH} ${MATH_FIGURE_HEIGHT}`"
    class="h-[25px] w-[42px] shrink-0"
    fill="none"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <!-- The zero line, in the well's own accent: what the three traces are drawn about. -->
    <path
      :d="`M2,${MATH_FIGURE_HEIGHT / 2} H${MATH_FIGURE_WIDTH - 2}`"
      stroke="currentColor"
      stroke-width="1"
      stroke-opacity="0.45"
      stroke-dasharray="1.6 1.8"
    />
    <path :d="figure.a" :stroke="CHANNEL_INK[props.sourceA]" stroke-width="1.2" stroke-opacity="0.8" />
    <path
      v-if="figure.b"
      :d="figure.b"
      :stroke="CHANNEL_INK[props.sourceB]"
      stroke-width="1.2"
      stroke-opacity="0.8"
    />
    <path :d="figure.result" :stroke="props.resultColor" stroke-width="2" />
  </svg>
</template>
