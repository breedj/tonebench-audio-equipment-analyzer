<script setup lang="ts">
/**
 * The round tinted glyph that opens a panel header, a sidebar row or a picker card: a circle in one
 * of the ten kind accents (`--kind-<tone>-chip-bg` / `-fg` in style.css) with a line icon inside it.
 *
 * The tone is a CSS variable pair rather than a Tailwind class pair for the reason style.css gives:
 * the values are the design's own, they belong to no Tailwind scale, and a colour spelled in a
 * template would need a `dark:` twin beside it in every caller.
 *
 * `aria-hidden` throughout — the title beside it is the label, and a decorative circle adds nothing
 * to a screen reader.
 */
import { computed } from 'vue'
import { PANEL_GLYPHS, type PanelIconName } from '../panels/panelIcons'
import type { MeasurementKindTone } from '../measurements/kinds'

const props = withDefaults(
  defineProps<{
    icon: PanelIconName
    tone: MeasurementKindTone
    /** The circle's diameter. 28 on a panel header, 30 in the sidebar, 24 on a compact panel. */
    size?: number
  }>(),
  { size: 28 },
)

const glyph = computed(() => PANEL_GLYPHS[props.icon])
/** The glyph is drawn at 4/7 of the circle, which is the mockup's 16-in-28. */
const glyphSize = computed(() => Math.round((props.size * 4) / 7))
</script>

<template>
  <span
    class="flex shrink-0 items-center justify-center rounded-full"
    :style="{
      width: `${props.size}px`,
      height: `${props.size}px`,
      backgroundColor: `var(--kind-${props.tone}-chip-bg)`,
      color: `var(--kind-${props.tone}-chip-fg)`,
    }"
    aria-hidden="true"
  >
    <svg
      :width="glyphSize"
      :height="glyphSize"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
      stroke-linecap="round"
      stroke-linejoin="round"
    >
      <path v-for="(d, i) in glyph.paths" :key="i" :d="d" />
      <circle v-for="(c, i) in glyph.circles ?? []" :key="`c${i}`" :cx="c.cx" :cy="c.cy" :r="c.r" />
    </svg>
  </span>
</template>
