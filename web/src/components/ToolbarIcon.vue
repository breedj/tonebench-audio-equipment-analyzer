<script setup lang="ts">
/**
 * The small line icons on the measurement toolbar's buttons (Save, Save as…, Save … result, Clear,
 * Screenshot, Report…), on the charts' Reset zoom and pinned-cursor badges, and on the FFT screen's
 * Metrics button. Inline SVG
 * paths — the outlines are Lucide's (ISC licence), drawn with `currentColor` so they follow the
 * button's own text colour in both themes and its disabled opacity.
 *
 * `aria-hidden`: every button that carries one also carries its label as text, so the icon adds
 * nothing to a screen reader.
 */
export type ToolbarIconName =
  | 'save'
  | 'save-as'
  | 'save-result'
  | 'clear'
  | 'screenshot'
  | 'report'
  | 'reset-zoom'
  | 'zoom-back'
  | 'set-axis'
  | 'metrics'
  | 'pin'

const PATHS: Record<ToolbarIconName, string[]> = {
  save: [
    'M15.2 3a2 2 0 0 1 1.4.6l3.8 3.8a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z',
    'M17 21v-7a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v7',
    'M7 3v4a1 1 0 0 0 1 1h7',
  ],
  'save-as': [
    'M10 2v3a1 1 0 0 0 1 1h5',
    'M18 18v-6a1 1 0 0 0-1-1h-6a1 1 0 0 0-1 1v6',
    'M18 22H4a2 2 0 0 1-2-2V6',
    'M8 18a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9.172a2 2 0 0 1 1.414.586l2.828 2.828A2 2 0 0 1 22 6.828V16a2 2 0 0 1-2.01 2z',
  ],
  'save-result': ['M2 3h20v5H2z', 'M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8', 'M10 12h4'],
  clear: [
    'm7 21-4.3-4.3c-1-1-1-2.5 0-3.4l9.6-9.6c1-1 2.5-1 3.4 0l5.6 5.6c1 1 1 2.5 0 3.4L13 21',
    'M22 21H7',
    'm5 11 9 9',
  ],
  screenshot: [
    'M14.5 4h-5L7 7H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3l-2.5-3z',
    'M12 16a3 3 0 1 0 0-6 3 3 0 0 0 0 6z',
  ],
  report: [
    'M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z',
    'M14 2v4a2 2 0 0 0 2 2h4',
    'M10 9H8',
    'M16 13H8',
    'M16 17H8',
  ],
  'reset-zoom': ['M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16z', 'm21 21-4.35-4.35', 'M8 11h6'],
  // An arrow curving back on itself: one step out of the zoom, not all the way out of it.
  'zoom-back': ['M3 7v6h6', 'M3.5 13a9 9 0 1 0 2.3-6.4L3 9.5'],
  // A frame with the two axes picked out: the window being written into the display panel's boxes.
  'set-axis': ['M4 4v16h16', 'M4 9h16', 'M9 4v16'],
  metrics: ['M11 12H3', 'M16 6H3', 'M16 18H3', 'M18 9v6', 'M21 12h-6'],
  pin: [
    'M12 17v5',
    'M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z',
  ],
}

/**
 * The accent the glyph is drawn in. From the 2026-10-12 mockups, where the measurement bar's
 * buttons keep black labels and colour only the icon — so the row reads as a row of words with a
 * key down its left, rather than as six coloured buttons competing with the chart.
 *
 * `none` is the default and inherits the button's own colour, which is what the disabled Save and
 * the chart toolbars' icons want: a coloured glyph on a 40 %-opacity button looks like a rendering
 * fault rather than a disabled control.
 */
export type ToolbarIconTone = 'none' | 'blue' | 'green' | 'red' | 'violet' | 'amber'

const TONES: Record<ToolbarIconTone, string> = {
  none: '',
  blue: 'text-blue-700 dark:text-blue-400',
  green: 'text-green-700 dark:text-green-400',
  red: 'text-red-700 dark:text-red-400',
  violet: 'text-violet-700 dark:text-violet-400',
  amber: 'text-amber-700 dark:text-amber-400',
}

const props = withDefaults(defineProps<{ name: ToolbarIconName; tone?: ToolbarIconTone }>(), {
  tone: 'none',
})
</script>

<template>
  <svg
    width="15"
    height="15"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="2"
    stroke-linecap="round"
    stroke-linejoin="round"
    class="shrink-0"
    :class="TONES[props.tone]"
    aria-hidden="true"
  >
    <path v-for="(d, i) in PATHS[props.name]" :key="i" :d="d" />
  </svg>
</template>
