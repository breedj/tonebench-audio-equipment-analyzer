<script setup lang="ts">
/**
 * One IMD preset's FFT footprint: where its two tones sit on a log axis and where its products
 * land. The shapes are data (<code>imd/footprints.ts</code>); this only draws them.
 *
 * <p><b>Three colours, and they are the app's own.</b> f1 takes the left input's blue and f2 the
 * right input's orange — not because a preset plays them on those channels, but because this
 * screen already uses that pair for "the first one and the second one" and a third convention
 * would be one too many. The products take the cursor charts' red: they are the thing being
 * <em>measured</em> rather than a thing being played, which is the same distinction the Following
 * cursors' arrows are red for.</p>
 *
 * <p>The axis, the decade rules and the floor are drawn in the section's own accent through
 * <code>currentColor</code>, so a footprint sits in its well the way every other figure here
 * does.</p>
 */
import { computed } from 'vue'
import { FOOTPRINT_HEIGHT, FOOTPRINT_WIDTH, imdFootprint } from '../imd/footprints'

const props = defineProps<{ preset: string }>()

/** f1, f2 and the products. The first two are this app's own channel inks. */
const INK = { f1: '#3b82f6', f2: '#f97316', product: '#e11d48' } as const

const figure = computed(() => imdFootprint(props.preset))
</script>

<template>
  <svg
    v-if="figure"
    :viewBox="`0 0 ${FOOTPRINT_WIDTH} ${FOOTPRINT_HEIGHT}`"
    class="h-[42px] w-[84px] shrink-0"
    fill="none"
    stroke-linecap="round"
    aria-hidden="true"
  >
    <!-- The decades, the axis and the floor: the frame the bars are read against. -->
    <path :d="figure.grid" stroke="currentColor" stroke-width="0.6" stroke-opacity="0.35" />
    <path :d="`M3 36.5 H78`" stroke="currentColor" stroke-width="1" stroke-opacity="0.6" />
    <path
      :d="`M3 31 H78`"
      stroke="currentColor"
      stroke-width="0.8"
      stroke-opacity="0.45"
      stroke-dasharray="1.5 1.5"
    />
    <path :d="figure.products" :stroke="INK.product" stroke-width="1.6" />
    <path
      :d="figure.f1"
      :stroke="INK.f1"
      stroke-width="2.6"
      :stroke-dasharray="figure.dashed ? '2.2 2' : undefined"
    />
    <path
      :d="figure.f2"
      :stroke="INK.f2"
      stroke-width="2.6"
      :stroke-dasharray="figure.dashed ? '2.2 2' : undefined"
    />
  </svg>
</template>
