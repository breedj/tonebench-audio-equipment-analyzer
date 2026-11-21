<script setup lang="ts">
/**
 * The tinted rounded well a picker tile's figure sits in.
 *
 * <p>It is not decoration: the tint is what ties a tile to the section heading above it, so a
 * reader who has found the right section can stay inside it by colour rather than by re-reading
 * the headings. It also gives every figure the same ground whatever the tile's own state is —
 * chosen tiles here carry a tinted background of their own, and a figure drawn straight onto that
 * would change weight as it was picked.</p>
 *
 * <p>The well sets <code>color</code> as well as the background, which is the whole of how the
 * figures are coloured: both icon components draw in <code>currentColor</code>, so they take the
 * section's accent without learning that sections exist.</p>
 */
import { sectionWellStyle } from '../panels/pickerSections'
import type { MeasurementGroupTone } from '../measurements/kinds'

const props = withDefaults(
  defineProps<{
    tone: MeasurementGroupTone
    /**
     * `md` is the design's 50×36 well, `sm` the 46×32 one its three-across grid uses, and `xs` the
     * 28×20 one a *control* gets — a row of the readings table, the button that opens a picker.
     *
     * <p>Small enough to sit in a 24 px table row and in a 32 px input without changing either's
     * height, which is the whole constraint: the point of colouring those figures is that they
     * match the tile the thing was chosen from, and it would be a poor trade to buy that with a
     * taller row.</p>
     *
     * <p><code>wide</code> is the 92×52 well the IMD footprints need: those figures are a spectrum
     * rather than a waveform, so they want an x axis worth reading across.</p>
     */
    size?: 'xs' | 'sm' | 'md' | 'wide'
  }>(),
  { size: 'md' },
)
</script>

<template>
  <span
    class="flex shrink-0 items-center justify-center rounded-lg"
    :class="{
      'h-9 w-[50px]': props.size === 'md',
      'h-8 w-[46px]': props.size === 'sm',
      'h-5 w-7 rounded-md': props.size === 'xs',
      'h-[52px] w-[92px]': props.size === 'wide',
    }"
    :style="sectionWellStyle(props.tone)"
    aria-hidden="true"
  >
    <slot />
  </span>
</template>
