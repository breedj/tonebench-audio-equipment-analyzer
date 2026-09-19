<script setup lang="ts">
// The one place that decides how a measurement screen's three regions — the settings rail, the
// chart column and the display rail — are arranged against the window width. Every screen in
// App.vue renders through this, so a breakpoint decision made here holds everywhere.
//
//   < 1024 px   one stacked column: rail, chart, display sections.
//   1024–1279   two columns: a 16rem rail beside the chart, and the display sections + History
//               checklist flow two-wide UNDER the chart. Three columns here left the chart ~170 px
//               with the sidebar pinned (246 + 32 + 304 + 272 of a 1024 px window), narrower than
//               the same window one pixel smaller, where everything stacked.
//   ≥ 1280 px   three columns: the same 16rem rail, chart, 16rem display rail (12rem on the FFT
//               screen, whose ChartDisplayPanel was designed for that width).
//
// It is a grid rather than a flex row because the aside has to be the third COLUMN at xl and a
// second ROW under the chart at lg without being rendered twice — a duplicated
// MeasurementChecklist would fetch twice and the display panels' v-models would fork.
//
// Each rail collapses to the strip on its chart-side edge, which carries the button that opens it
// again; the strip runs the full height of the row so the boundary reads as a boundary. The
// settings rail collapses wherever it is a column (lg up); the display rail only where it is one
// (xl up) — under the chart it is a row of sections, and hiding those would not give the chart
// anything. Both states are stored with the sidebar pin (useUiLayout) and survive a restart.
// Hiding is done with responsive `hidden` classes rather than v-if, so the slot contents are one
// DOM node in every state and a collapse is a layout change the charts' ResizeObservers see, not
// a remount.
import { useUiLayout } from '../composables/useUiLayout'
import MeasurementNotesPanel from './MeasurementNotesPanel.vue'

withDefaults(defineProps<{ asideWidth?: 'md' | 'sm' }>(), { asideWidth: 'md' })

const {
  settingsRailCollapsed,
  displayRailCollapsed,
  load,
  setSettingsRailCollapsed,
  setDisplayRailCollapsed,
} = useUiLayout()
void load()

const STRIP =
  'shrink-0 items-start justify-center rounded border border-neutral-200 pt-2 text-neutral-500 ' +
  'hover:bg-neutral-100 hover:text-neutral-900 dark:border-neutral-800 dark:text-neutral-400 ' +
  'dark:hover:bg-neutral-800 dark:hover:text-neutral-100'
</script>

<template>
  <!-- The rail columns are `auto`: their width is their content's — the rail at 16rem plus its
       strip, or the strip alone once collapsed — so a collapse needs no second template. The
       minmax(0,1fr) is the grid equivalent of `min-w-0 flex-1`: without the 0 a uPlot canvas
       reports its last width as the column's minimum and the column never shrinks again. -->
  <div class="grid grid-cols-1 gap-4 lg:grid-cols-[auto_minmax(0,1fr)] lg:items-start xl:grid-cols-[auto_minmax(0,1fr)_auto]">
    <!-- The rail spans both rows at lg, which is what makes auto-placement put the aside (the
         third child) in row 2 / column 2 — under the chart, not under the rail. At xl it is back
         to one row, so the aside lands in column 3. DOM order never changes. `self-stretch`
         overrides the grid's items-start so the strip runs the row's full height. -->
    <div class="flex min-w-0 items-stretch lg:row-span-2 lg:self-stretch xl:row-span-1">
      <div
        class="flex min-w-0 flex-1 flex-col gap-3 lg:w-64 lg:flex-none"
        :class="settingsRailCollapsed ? 'lg:hidden' : ''"
      >
        <slot name="rail" />
        <!-- Every measurement's Notes box, under its settings: here rather than in each screen's
             rail template, so it is in the same place on every screen and cannot be forgotten by a
             new one. -->
        <MeasurementNotesPanel />
      </div>
      <button
        type="button"
        class="hidden lg:flex"
        :class="[STRIP, settingsRailCollapsed ? '' : 'ml-2']"
        :title="settingsRailCollapsed ? 'Show the settings' : 'Collapse the settings to the left'"
        :aria-label="settingsRailCollapsed ? 'Show the settings' : 'Collapse the settings'"
        :aria-expanded="!settingsRailCollapsed"
        @click="setSettingsRailCollapsed(!settingsRailCollapsed)"
      >
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path :d="settingsRailCollapsed ? 'M9 5l7 7-7 7' : 'M15 5l-7 7 7 7'" />
        </svg>
      </button>
    </div>

    <div class="flex min-w-0 flex-col gap-3">
      <slot />
    </div>

    <!-- Under the chart the sections flow two-wide; `items-start` keeps a short section from
         stretching to its neighbour's height. From xl it is a flex column again, where
         `items-start` would shrink-wrap every section — hence the explicit `xl:items-stretch`. -->
    <div class="flex min-w-0 items-stretch xl:self-stretch">
      <button
        type="button"
        class="hidden xl:flex"
        :class="[STRIP, displayRailCollapsed ? '' : 'mr-2']"
        :title="displayRailCollapsed ? 'Show the display options' : 'Collapse the display options to the right'"
        :aria-label="displayRailCollapsed ? 'Show the display options' : 'Collapse the display options'"
        :aria-expanded="!displayRailCollapsed"
        @click="setDisplayRailCollapsed(!displayRailCollapsed)"
      >
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path :d="displayRailCollapsed ? 'M15 5l-7 7 7 7' : 'M9 5l7 7-7 7'" />
        </svg>
      </button>
      <div
        class="grid min-w-0 flex-1 grid-cols-1 gap-3 sm:grid-cols-2 lg:items-start xl:flex xl:flex-none xl:flex-col xl:items-stretch"
        :class="[asideWidth === 'sm' ? 'xl:w-48' : 'xl:w-64', displayRailCollapsed ? 'xl:hidden' : '']"
      >
        <slot name="aside" />
      </div>
    </div>
  </div>
</template>
