<script setup lang="ts">
import { computed } from 'vue'

// THE APP'S MARK: a fundamental with its peak marker and five falling harmonics — the app's
// signature measurement. Chosen from five proposals on 2026-09-30 because it is the one that still
// reads at 16 px, which is the only size a taskbar or a favicon ever gets.
//
// The dashed noise floor just above the baseline was removed on 2026-10-05: at 16 px a 4 px dash on
// a 10/12 pattern is under half a pixel of coverage, so it rendered as a grey smear across the
// harmonics rather than as a line, and at 256 px it read as a second baseline.
//
// One geometry, two variants, and the difference between them is not cosmetic:
//
//   tile   the comb on its own dark ground, in the brighter #38bdf8/#4ade80 pair. This is what
//          packaging/icons/tonebench.svg draws, i.e. literally the app icon, which is what an about
//          box is meant to show. Use it wherever the mark stands alone — the about dialog, and a
//          splash screen when one is built.
//   glyph  the bare comb, structure in `currentColor` and accents in #0ea5e9/#22c55e, so it
//          follows the theme. Use it inline against a panel that already has a background of its
//          own — a header, a heading, an empty state.
//
// The comb is drawn ONCE, at its own coordinates, and the tile variant places it with a single
// transform. Its real bounds are x 20..236 and y 18.5..207 — strokes and round caps included, not
// the nominal rects, so x is the baseline's and y runs from the top of the peak marker's stroke to
// the bottom of the baseline — fitted to x 36..220 and centred vertically, hence scale
// 184/216 = 0.8519. Both numbers moved when the noise floor went, because its round caps at
// x 18..238 were what set the width.
// packaging/icons/tonebench.svg carries the same numbers because sharp cannot rasterise a .vue; that
// duplication is the one thing here that can rot, so its comment says so too. Change the geometry
// in both files or in neither, then run `npm run icons` in web/.

const props = withDefaults(defineProps<{ size?: number; variant?: 'tile' | 'glyph' }>(), {
  size: 64,
  variant: 'tile',
})

const tiled = computed(() => props.variant === 'tile')
/** The baseline. White over the tile's own dark ground; the theme's ink otherwise. */
const structure = computed(() => (tiled.value ? '#ffffff' : 'currentColor'))
const bar = computed(() => (tiled.value ? '#38bdf8' : '#0ea5e9'))
const marker = computed(() => (tiled.value ? '#4ade80' : '#22c55e'))

// A gradient id has to be unique per document, not per component: two logos on one page with the
// same id both resolve to whichever was defined first. Harmless while they are identical, but it
// is the kind of thing that only shows up once a second instance exists.
const gradientId = `tonebench-logo-tile-${Math.random().toString(36).slice(2, 9)}`
</script>

<template>
  <svg
    :width="props.size"
    :height="props.size"
    viewBox="0 0 256 256"
    role="img"
    aria-label="Tonebench - Audio Equipment Analyzer"
  >
    <template v-if="tiled">
      <defs>
        <linearGradient :id="gradientId" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stop-color="#1f2937" />
          <stop offset="1" stop-color="#0b1220" />
        </linearGradient>
      </defs>
      <rect x="8" y="8" width="240" height="240" rx="52" :fill="`url(#${gradientId})`" />
    </template>

    <g :transform="tiled ? 'translate(18.96 31.95) scale(0.8519)' : undefined">
      <!-- baseline -->
      <rect x="20" y="202" width="216" height="5" rx="2.5" :fill="structure" opacity="0.22" />

      <!-- fundamental, then five falling harmonics -->
      <rect x="27" y="54" width="22" height="148" rx="6" :fill="bar" />
      <g :fill="bar" opacity="0.55">
        <rect x="63" y="114" width="22" height="88" rx="6" />
        <rect x="99" y="142" width="22" height="60" rx="6" />
        <rect x="135" y="160" width="22" height="42" rx="6" />
        <rect x="171" y="172" width="22" height="30" rx="6" />
        <rect x="207" y="180" width="22" height="22" rx="6" />
      </g>

      <!-- peak marker on the fundamental -->
      <circle cx="38" cy="34" r="12" fill="none" :stroke="marker" stroke-width="7" />
    </g>
  </svg>
</template>
