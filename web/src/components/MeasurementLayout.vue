<script setup lang="ts">
// The one place that decides how a measurement screen's three regions — the settings rail, the
// chart column and the display rail — are arranged against the window width. Every screen in
// App.vue renders through this, so a breakpoint decision made here holds everywhere.
//
//   < 1024 px   one stacked column: rail, chart, display sections.
//   ≥ 1024 px   three columns: a 16rem rail (266 px with `roomyRail`), the chart, and a 16rem
//               display rail. The FFT screen's display rail used to be 12rem, which its
//               ChartDisplayPanel was designed for; every screen is 16rem since 2026-09-19 so the
//               display rails line up when switching screens.
//
// Three columns used to start at 1280, with the display sections flowing two-wide *under* the
// chart from 1024 to 1279 — because three columns at 1024 left the chart about 170 px once the
// sidebar was pinned (246 + 32 + 304 + 272). What makes three columns work at 1024 now is that
// by then both rails have folded to their strips and the sidebar to its icons: the widths in
// useMediaQuery.ts fold them on the way down, in an order chosen so the chart keeps its width
// instead of a panel dropping below it (Joost, 2026-10-08). Same arithmetic, ~880 px of chart.
//
// It is still a grid rather than a flex row because the aside has to be the third COLUMN above
// 1024 and a second ROW under the chart below it, without being rendered twice — a duplicated
// MeasurementChecklist would fetch twice and the display panels' v-models would fork.
//
// Each rail collapses to the strip on its chart-side edge, which carries the button that opens it
// again; the strip runs the full height of the row so the boundary reads as a boundary. Both
// collapse wherever they are columns (lg up) — stacked below that, the display rail is a row of
// sections and hiding those would not give the chart anything. The stored half of that state
// lives with the sidebar pin (useUiLayout) and survives a restart; the width-driven half is
// railCollapse.ts's, and deliberately does not. Hiding is done with responsive `hidden` classes
// rather than v-if, so the slot contents are one DOM node in every state and a collapse is a
// layout change the charts' ResizeObservers see, not a remount.
import { computed, ref, watch } from 'vue'
import { useUiLayout } from '../composables/useUiLayout'
import {
  DISPLAY_RAIL_MIN_WIDTH_PX,
  SETTINGS_RAIL_MIN_WIDTH_PX,
  useMediaQuery,
} from '../composables/useMediaQuery'
import { railAtNewWidth, railCollapsed, railToggled, type RailState } from '../layout/railCollapse'
import SimulatorHint from './SimulatorHint.vue'

/**
 * `roomyRail` gives the settings column 266 px instead of 16rem, and only the FFT screen asks for
 * it: a generator tone row is a frequency field, "Hz", a level field, the level-unit dropdown and
 * the Remove-tone button, and at 16rem exactly the last of those ran off the end. Ten pixels, not a
 * size up, so the rail still reads as lined up with every other screen's.
 *
 * There used to be a `wideRail` at 18.75rem, for the two frequency-response screens: their generator
 * block carries whole sentences rather than labels — the level mode's "at 5-10 Hz (bandwidth
 * reference)" line and the stepped runner's point/time estimate. It is gone (Joost, 2026-09-29),
 * because a rail and a RUN/SINGLE/STOP trio three quarters of an inch wider than the impulse
 * response screen's next door was the more visible of the two problems; those sentences wrap.
 */
const props = withDefaults(defineProps<{ roomyRail?: boolean }>(), { roomyRail: false })

const {
  settingsRailCollapsed,
  displayRailCollapsed,
  load,
  setSettingsRailCollapsed,
  setDisplayRailCollapsed,
} = useUiLayout()
void load()

// Each rail folds on its own width, and can still be opened by hand below it for this session —
// see railCollapse.ts for why the by-hand half is not written to the preference.
const settingsWideEnough = useMediaQuery(`(min-width: ${SETTINGS_RAIL_MIN_WIDTH_PX}px)`)
const displayWideEnough = useMediaQuery(`(min-width: ${DISPLAY_RAIL_MIN_WIDTH_PX}px)`)

const settingsOverrideOpen = ref(false)
const displayOverrideOpen = ref(false)
watch(settingsWideEnough, () => {
  settingsOverrideOpen.value = railAtNewWidth({ stored: settingsRailCollapsed.value, overrideOpen: settingsOverrideOpen.value }).overrideOpen
})
watch(displayWideEnough, () => {
  displayOverrideOpen.value = railAtNewWidth({ stored: displayRailCollapsed.value, overrideOpen: displayOverrideOpen.value }).overrideOpen
})

const settingsState = computed<RailState>(() => ({ stored: settingsRailCollapsed.value, overrideOpen: settingsOverrideOpen.value }))
const displayState = computed<RailState>(() => ({ stored: displayRailCollapsed.value, overrideOpen: displayOverrideOpen.value }))

const settingsFolded = computed(() => railCollapsed(settingsState.value, settingsWideEnough.value))
const displayFolded = computed(() => railCollapsed(displayState.value, displayWideEnough.value))

function toggleSettings() {
  const next = railToggled(settingsState.value, settingsWideEnough.value)
  settingsOverrideOpen.value = next.overrideOpen
  if (next.stored !== settingsRailCollapsed.value) void setSettingsRailCollapsed(next.stored)
}

function toggleDisplay() {
  const next = railToggled(displayState.value, displayWideEnough.value)
  displayOverrideOpen.value = next.overrideOpen
  if (next.stored !== displayRailCollapsed.value) void setDisplayRailCollapsed(next.stored)
}

// `panel-surface` is what keeps the two strips reading as the edge of the rail they fold rather than
// as a gap in the page — they carry the same ground as every panel (style.css). The hover background
// still wins over it: a `:hover` utility outranks a plain class.
const STRIP =
  'panel-surface shrink-0 items-start justify-center rounded border border-neutral-200 pt-2 text-neutral-500 ' +
  'hover:bg-neutral-100 hover:text-neutral-900 dark:border-neutral-800 dark:text-neutral-400 ' +
  'dark:hover:bg-neutral-800 dark:hover:text-neutral-100'
</script>

<template>
  <!-- The rail columns are `auto`: their width is their content's — the rail at 16rem plus its
       strip, or the strip alone once collapsed — so a collapse needs no second template. The
       minmax(0,1fr) is the grid equivalent of `min-w-0 flex-1`: without the 0 a uPlot canvas
       reports its last width as the column's minimum and the column never shrinks again. -->
  <div class="grid grid-cols-1 gap-4 lg:grid-cols-[auto_minmax(0,1fr)_auto] lg:items-start">
    <!-- Stacked, the three children fall in DOM order. From lg each is its own column and the
         aside (the third child) lands in column 3 — which is what the rail's `lg:row-span-2` used
         to prevent, pushing it under the chart instead. DOM order never changes either way.
         `self-stretch` overrides the grid's items-start so the strip runs the row's full height. -->
    <div class="flex min-w-0 items-stretch lg:self-stretch">
      <div
        class="flex min-w-0 flex-1 flex-col gap-3 lg:flex-none"
        :class="[
          props.roomyRail ? 'lg:w-[266px]' : 'lg:w-64',
          settingsFolded ? 'lg:hidden' : '',
        ]"
      >
        <slot name="rail" />
      </div>
      <button
        type="button"
        class="hidden lg:flex"
        :class="[STRIP, settingsFolded ? '' : 'ml-2']"
        :title="settingsFolded ? 'Show the settings' : 'Collapse the settings to the left'"
        :aria-label="settingsFolded ? 'Show the settings' : 'Collapse the settings'"
        :aria-expanded="!settingsFolded"
        @click="toggleSettings"
      >
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path :d="settingsFolded ? 'M9 5l7 7-7 7' : 'M15 5l-7 7 7 7'" />
        </svg>
      </button>
    </div>

    <div class="flex min-w-0 flex-col gap-3">
      <!-- Simulated mode only: what the status-bar knobs have to say for this measurement to show
           anything, when they do not. Here rather than in each screen so no screen can forget it. -->
      <SimulatorHint />
      <slot />
    </div>

    <!-- Stacked under the chart the sections flow two-wide; `items-start` keeps a short section
         from stretching to its neighbour's height. From lg it is a flex column, where
         `items-start` would shrink-wrap every section — hence the explicit `lg:items-stretch`. -->
    <div class="flex min-w-0 items-stretch lg:self-stretch">
      <button
        type="button"
        class="hidden lg:flex"
        :class="[STRIP, displayFolded ? '' : 'mr-2']"
        :title="displayFolded ? 'Show the display options' : 'Collapse the display options to the right'"
        :aria-label="displayFolded ? 'Show the display options' : 'Collapse the display options'"
        :aria-expanded="!displayFolded"
        @click="toggleDisplay"
      >
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path :d="displayFolded ? 'M15 5l-7 7 7 7' : 'M9 5l7 7-7 7'" />
        </svg>
      </button>
      <div
        class="grid min-w-0 flex-1 grid-cols-1 gap-3 sm:grid-cols-2 lg:flex lg:w-64 lg:flex-none lg:flex-col lg:items-stretch"
        :class="displayFolded ? 'lg:hidden' : ''"
      >
        <slot name="aside" />
      </div>
    </div>
  </div>
</template>
