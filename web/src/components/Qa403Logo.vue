<script setup lang="ts">
import { computed } from 'vue'

// THE APP'S MARK: a fundamental with its peak marker and five harmonics falling into the noise
// floor — the app's signature measurement. Chosen from five proposals on 2026-09-30 because it is
// the one that still reads at 16 px, which is the only size a taskbar or a favicon ever gets.
//
// One geometry, two variants, and the difference between them is not cosmetic:
//
//   tile   the comb on its own dark ground, in the brighter #38bdf8/#4ade80 pair. This is what
//          packaging/icons/qa403.svg draws, i.e. literally the app icon, which is what an about
//          box is meant to show. Use it wherever the mark stands alone — the about dialog, and a
//          splash screen when one is built.
//   glyph  the bare comb, structure in `currentColor` and accents in #0ea5e9/#22c55e, so it
//          follows the theme. Use it inline against a panel that already has a background of its
//          own — a header, a heading, an empty state.
//
// The comb is drawn ONCE, at its own coordinates, and the tile variant places it with a single
// transform. Its real bounds are x 18..238 and y 18.5..207 — strokes and round caps included, not
// the nominal rects — fitted to x 36..220 and centred vertically, hence scale 184/220 = 0.8364.
// packaging/icons/qa403.svg carries the same numbers because sharp cannot rasterise a .vue; that
// duplication is the one thing here that can rot, so its comment says so too. Change the geometry
// in both files or in neither, then run `npm run icons` in web/.

const props = withDefaults(defineProps<{ size?: number; variant?: 'tile' | 'glyph' }>(), {
  size: 64,
  variant: 'tile',
})

const tiled = computed(() => props.variant === 'tile')
/** Baseline and noise floor. White over the tile's own dark ground; the theme's ink otherwise. */
const structure = computed(() => (tiled.value ? '#ffffff' : 'currentColor'))
const bar = computed(() => (tiled.value ? '#38bdf8' : '#0ea5e9'))
const marker = computed(() => (tiled.value ? '#4ade80' : '#22c55e'))

// A gradient id has to be unique per document, not per component: two logos on one page with the
// same id both resolve to whichever was defined first. Harmless while they are identical, but it
// is the kind of thing that only shows up once a second instance exists.
const gradientId = `qa403-logo-tile-${Math.random().toString(36).slice(2, 9)}`
</script>

<template>
  <svg
    :width="props.size"
    :height="props.size"
    viewBox="0 0 256 256"
    role="img"
    aria-label="QA403 Audio Analyzer"
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

    <g :transform="tiled ? 'translate(20.94 33.73) scale(0.8364)' : undefined">
      <!-- noise floor -->
      <path
        d="M20 192 H236"
        fill="none"
        :stroke="structure"
        stroke-width="4"
        stroke-linecap="round"
        stroke-dasharray="10 12"
        opacity="0.3"
      />

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
