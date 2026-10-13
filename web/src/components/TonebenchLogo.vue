<script setup lang="ts">
import { computed } from 'vue'

// THE APP'S MARK: four rounded bars — a level meter, or four tones at four levels. Joost's own
// drawing, supplied as docs/design/tonebench-icon.svg on 2026-10-12, replacing the harmonic comb
// that had stood since 2026-09-30. It is simpler at 16 px than the comb was, which is the only size
// a taskbar or a favicon ever gets, and it is what the 2026-10-12 mockups put in the header.
//
// **The bars carry their own colours — this mark is not `currentColor`.** That is the one thing
// that changed with it: the comb's structure followed the theme, and four hues that spell the mark
// cannot. So both variants are literal, and they differ only in which pair of weights reads against
// the ground they sit on:
//
//   glyph  the bars alone, in the 500 weights of the supplied SVG. Use it against a panel that has
//          a background of its own — the app header, the welcome page, an empty state.
//   tile   the same bars on the dark rounded tile, in the 400 weights, which is what reads against
//          it. This is literally packaging/icons/tonebench.svg — the shipped app icon — so it is
//          what an about box or a splash screen should show.
//
// The bars are drawn ONCE at the supplied 24-unit coordinates and the tile variant places them with
// a single transform, so the two cannot drift. Their bounds are x 3..21 and y 5..21 (the nominal
// rects; the corners are radii, not strokes, so nothing sticks out), fitted to x 36..220 — the same
// 184-wide window the comb used — and centred vertically: scale 184/18 = 10.2222, hence the
// translate. packaging/icons/tonebench.svg repeats those numbers because sharp cannot rasterise a
// `.vue`; that duplication is the one thing here that can rot, so its comment says so too. Change
// the geometry in both files or in neither, then run `npm run icons` in web/.

const props = withDefaults(defineProps<{ size?: number; variant?: 'tile' | 'glyph' }>(), {
  size: 64,
  variant: 'tile',
})

const tiled = computed(() => props.variant === 'tile')

/** Blue, violet, pink, orange — the 500 weights bare, the 400s against the tile's dark ground. */
const bars = computed(() =>
  tiled.value
    ? ['#60a5fa', '#a78bfa', '#f472b6', '#fb923c']
    : ['#3b82f6', '#8b5cf6', '#ec4899', '#f97316'],
)

/** x, y, height — the width and the 1.5 radius are the same for all four. */
const GEOMETRY = [
  { x: 3, y: 12, height: 9 },
  { x: 8, y: 5, height: 16 },
  { x: 13, y: 9, height: 12 },
  { x: 18, y: 15, height: 6 },
] as const

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

    <g :transform="tiled ? 'translate(5.333 -4.91) scale(10.2222)' : 'scale(10.6667)'">
      <rect
        v-for="(bar, i) in GEOMETRY"
        :key="i"
        :x="bar.x"
        :y="bar.y"
        width="3"
        :height="bar.height"
        rx="1.5"
        :fill="bars[i]"
      />
    </g>
  </svg>
</template>
