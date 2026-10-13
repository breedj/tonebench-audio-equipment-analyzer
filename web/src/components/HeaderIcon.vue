<script setup lang="ts">
/**
 * The four icons at the right of the app header — the manual, About, Report an issue and
 * Configuration. Joost's own drawings, supplied as docs/design/icon-*.svg on 2026-10-12.
 *
 * **Each carries its own hue and its own translucent fill, and that is the point of them.** The old
 * set was four grey outlines that only differed in shape, so finding the one you wanted meant
 * reading all four; a book, a circle, a bug and a gear in amber, blue, red and violet are told apart
 * at a glance. The fill is the stroke colour at 18 %, so each is a tinted shape rather than a line
 * drawing, and it is the one thing here that does *not* follow the theme: these hues are the mark's
 * own, and a dark-theme twin for each would be four more values to keep in step for no gain — all
 * four are already light enough to carry on the near-black header.
 *
 * In one component rather than four inline SVGs for the reason ToolbarIcon is: the geometry is data,
 * and the button around it is the caller's.
 */
export type HeaderIconName = 'manual' | 'about' | 'bug' | 'config'

interface Drawing {
  /** The stroke colour; the filled shape repeats it at 18 %. */
  stroke: string
  /** The shape that takes the translucent fill. */
  filled: { kind: 'path'; d: string } | { kind: 'circle'; cx: number; cy: number; r: number } | { kind: 'rect'; x: number; y: number; width: number; height: number; rx: number }
  /** Everything drawn as a line on top of it. */
  outline: string
}

const DRAWINGS: Record<HeaderIconName, Drawing> = {
  manual: {
    stroke: '#d97706',
    filled: { kind: 'path', d: 'M5 4h11a3 3 0 0 1 3 3v13H8a3 3 0 0 1-3-3z' },
    outline: 'M5 17a3 3 0 0 1 3-3h11',
  },
  about: {
    stroke: '#2563eb',
    filled: { kind: 'circle', cx: 12, cy: 12, r: 9 },
    outline: 'M12 11v5M12 8h.01',
  },
  bug: {
    stroke: '#dc2626',
    filled: { kind: 'rect', x: 7, y: 7, width: 10, height: 13, rx: 5 },
    outline: 'M9 7a3 3 0 0 1 6 0M3 13h4M17 13h4M4 7l3 2M20 7l-3 2M4 19l3-2M20 19l-3-2',
  },
  // The cog is the app's own, not the supplied drawing's: eight straight spokes around a circle
  // read as a sun at 20 px rather than as a gear (Joost, 2026-10-12). It keeps the new set's violet,
  // which is the half of that drawing that was the point.
  config: {
    stroke: '#7c3aed',
    filled: { kind: 'circle', cx: 12, cy: 12, r: 3 },
    outline:
      'M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z',
  },
}

const props = withDefaults(defineProps<{ name: HeaderIconName; size?: number }>(), { size: 20 })
</script>

<template>
  <svg
    :width="props.size"
    :height="props.size"
    viewBox="0 0 24 24"
    fill="none"
    :stroke="DRAWINGS[props.name].stroke"
    stroke-width="1.8"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <path
      v-if="DRAWINGS[props.name].filled.kind === 'path'"
      :d="(DRAWINGS[props.name].filled as { d: string }).d"
      :fill="DRAWINGS[props.name].stroke"
      fill-opacity="0.18"
    />
    <circle
      v-else-if="DRAWINGS[props.name].filled.kind === 'circle'"
      :cx="(DRAWINGS[props.name].filled as { cx: number }).cx"
      :cy="(DRAWINGS[props.name].filled as { cy: number }).cy"
      :r="(DRAWINGS[props.name].filled as { r: number }).r"
      :fill="DRAWINGS[props.name].stroke"
      fill-opacity="0.18"
    />
    <rect
      v-else
      :x="(DRAWINGS[props.name].filled as { x: number }).x"
      :y="(DRAWINGS[props.name].filled as { y: number }).y"
      :width="(DRAWINGS[props.name].filled as { width: number }).width"
      :height="(DRAWINGS[props.name].filled as { height: number }).height"
      :rx="(DRAWINGS[props.name].filled as { rx: number }).rx"
      :fill="DRAWINGS[props.name].stroke"
      fill-opacity="0.18"
    />
    <path :d="DRAWINGS[props.name].outline" />
  </svg>
</template>
