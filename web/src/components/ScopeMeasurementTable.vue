<script setup lang="ts">
import CollapsibleSection from './CollapsibleSection.vue'
import PickerFigureWell from './PickerFigureWell.vue'
import ScopeMeasurementIcon from './ScopeMeasurementIcon.vue'
import { sectionMarkColor, type PickerSection } from '../panels/pickerSections'
import ToolbarIcon from './ToolbarIcon.vue'
import { useScopeMeasurements } from '../composables/useScopeMeasurements'
import {
  scopeMeasurement,
  scopeMeasurementSection,
  SCOPE_MEASUREMENT_ABSENT,
} from '../scope/measurementCatalogue'
import { SCOPE_STAT_COLUMNS } from '../scope/measurementStats'
import type { ScopeMeasurementRow } from '../scope/measurementRows'

/**
 * The chosen measurements, under the chart.
 *
 * <p><b>Rows are measurements and columns are statistics</b>, which is the transpose of the way a
 * bench instrument prints the same table — and the right way round here. A scope's own readout has
 * a fixed width and four or five slots; this sits under a chart in a column that is free to grow
 * downwards and never sideways, so measurements along the top would scroll out of reach the moment
 * a reader ticked a seventh. Going down instead also lets the two channels' figure for one reading
 * sit next to each other, which is the commonest comparison there is on a stereo capture.</p>
 *
 * <p>Every figure here was measured on the analyzer, on the samples of the window on screen at full
 * resolution, and the statistics beside it are of the same rows of the same records — see
 * <code>scope/measurementStats.ts</code>, including why <b>Count is records the screen read</b>
 * rather than records the run committed.</p>
 */

defineProps<{
  rows: readonly ScopeMeasurementRow[]
  /** True while the cursors are gating, so the heading says which stretch the figures are of. */
  gated?: boolean
}>()

/**
 * The controls live in this card's own header now rather than in the Measurements panel.
 *
 * <p>The rail's argument was that a bench instrument puts the readings on the screen and the
 * controls that decide them in a menu. It is the wrong way round here: the reader notices a
 * reading is missing, or that a figure is wandering, while looking at <em>this table</em> — so the
 * add, the statistics and the reset belong where that is noticed.</p>
 */
const { selected, statistics, setStatistics, stat, statCount, resetStats, remove } = useScopeMeasurements()

/** A statistic as the cell prints it — in the row's own units, at its own magnitude. */
function statText(row: ScopeMeasurementRow, key: (typeof SCOPE_STAT_COLUMNS)[number]['key']): string {
  const s = stat(row.key)
  if (!s) return SCOPE_MEASUREMENT_ABSENT
  return row.format(s[key])
}

function statCountOf(row: ScopeMeasurementRow): string {
  return (stat(row.key)?.count ?? 0).toLocaleString()
}

function iconOf(row: ScopeMeasurementRow): string {
  return scopeMeasurement(row.id)?.icon ?? ''
}

/**
 * The section a row's reading was chosen from, which is where its figure's colours come from.
 *
 * <p>So a row is drawn exactly as the tile it was picked off: same figure, same tint, same mark
 * colour. That is worth more here than in the dialog, because the table is where a reader looks
 * afterwards — the colour says which section a reading came from, and two readings of the same
 * kind sit in the same hue without the table growing a column to say so.</p>
 */
function sectionOf(row: ScopeMeasurementRow): PickerSection | null {
  return scopeMeasurementSection(row.id)
}

/**
 * Opens the picker from the table it fills.
 *
 * <p>The only way in, since the rail's own button went with the readings it sat beside: this is
 * where a reader is looking when they notice a reading is missing. It goes in the header's own
 * slot, which stays clickable without toggling the section.</p>
 */
const emit = defineEmits<{ pickMeasurements: [] }>()
</script>

<template>
  <CollapsibleSection title="Selected measurements">
    <template #header-right>
      <div class="flex items-center gap-1.5">
        <!-- The count beside the controls that change it, where the rail used to carry it. -->
        <span v-if="statistics && statCount > 0" class="text-[11px] tabular-nums text-neutral-500 dark:text-neutral-400">
          {{ statCount.toLocaleString() }}
        </span>
        <button
          type="button"
          class="flex h-7 w-7 items-center justify-center rounded-lg border transition btn-toggle-off"
          title="Add or remove measurements…"
          aria-label="Add or remove measurements"
          @click="emit('pickMeasurements')"
        >
          <ToolbarIcon name="plus" />
        </button>
        <!-- A lit toggle rather than a tick box, the display rail's own idiom: unlit is the track's
             recess and lit is the accent, so the state survives a monochrome screenshot. -->
        <button
          type="button"
          class="flex h-7 w-7 items-center justify-center rounded-lg border transition"
          :class="statistics ? 'btn-action' : 'btn-toggle-off'"
          :aria-pressed="statistics"
          aria-label="Statistics"
          title="Statistics — each row gains its mean, its extremes, how far it has wandered, and how many records have been counted."
          @click="setStatistics(!statistics)"
        >
          <ToolbarIcon name="statistics" />
        </button>
        <!-- Clear is the *statistics*, not the selection: the rows come off one at a time by their
             own cross, and this is the one of the two that cannot be undone. -->
        <button
          type="button"
          class="flex h-7 w-7 items-center justify-center rounded-lg border transition btn-toggle-off disabled:cursor-not-allowed disabled:opacity-40"
          :disabled="statCount === 0"
          aria-label="Clear statistics"
          title="Clear the accumulated statistics and start counting again from the next record. The readings themselves are unaffected."
          @click="resetStats()"
        >
          <ToolbarIcon name="reset" />
        </button>
      </div>
    </template>

    <p
      v-if="gated && rows.length > 0"
      class="mb-2 rounded bg-teal-500/10 px-1.5 py-1 text-[11px] leading-snug text-teal-700 dark:text-teal-300"
    >
      Between the cursors — re-measured on the samples in that stretch, not read off the window's
      own figures.
    </p>

    <div v-if="rows.length > 0" class="overflow-x-auto">
      <table class="w-full text-left text-[12px] tabular-nums">
        <thead class="text-[11px] uppercase text-neutral-500 dark:text-neutral-400">
          <tr class="border-b border-neutral-200 dark:border-neutral-800">
            <th class="w-6 pb-1"></th>
            <th class="pb-1 pr-2 font-medium">Measurement</th>
            <th class="pb-1 pr-3 text-right font-medium">Value</th>
            <template v-if="statistics">
              <th
                v-for="column in SCOPE_STAT_COLUMNS"
                :key="column.key"
                class="pb-1 pr-3 text-right font-medium"
                :title="column.title"
              >
                {{ column.label }}
              </th>
              <th
                class="pb-1 text-right font-medium"
                title="How many records have been counted. Records the screen read, not records the run committed — a run commits faster than a browser polls, and the ones in between are never fetched."
              >
                Count
              </th>
            </template>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="row in rows"
            :key="row.key"
            class="group border-b border-neutral-100 last:border-0 dark:border-neutral-800/60"
            :data-measurement="row.key"
          >
            <td class="py-1 pr-1.5 text-neutral-400 dark:text-neutral-500">
              <PickerFigureWell v-if="sectionOf(row)" :tone="sectionOf(row)!.tone" size="xs">
                <ScopeMeasurementIcon
                  :name="iconOf(row)"
                  :mark="sectionMarkColor(sectionOf(row)!.mark)"
                />
              </PickerFigureWell>
              <ScopeMeasurementIcon v-else :name="iconOf(row)" />
            </td>
            <td class="py-1 pr-2" :title="row.title">
              <span class="text-neutral-500 dark:text-neutral-400">{{ row.sourceLabel }}</span>
              <span class="mx-1 text-neutral-300 dark:text-neutral-600">·</span>
              <span class="font-medium">{{ row.label }}</span>
              <button
                type="button"
                class="ml-1.5 text-neutral-300 opacity-0 transition-opacity hover:text-red-500 group-hover:opacity-100 dark:text-neutral-600"
                title="Take this reading off the table. Only this source — the same measurement on the other input stays."
                @click="remove(row.key)"
              >
                ✕
              </button>
            </td>
            <td class="py-1 pr-3 text-right font-mono text-neutral-900 dark:text-neutral-100">
              {{ row.text }}
            </td>
            <template v-if="statistics">
              <td
                v-for="column in SCOPE_STAT_COLUMNS"
                :key="column.key"
                class="py-1 pr-3 text-right font-mono text-neutral-600 dark:text-neutral-300"
              >
                {{ statText(row, column.key) }}
              </td>
              <td class="py-1 text-right font-mono text-neutral-500 dark:text-neutral-400">
                {{ statCountOf(row) }}
              </td>
            </template>
          </tr>
        </tbody>
      </table>
    </div>

    <p v-else-if="selected.length === 0" class="text-xs text-neutral-400 dark:text-neutral-600">
      Nothing chosen. Press <strong>+</strong> above to put a reading here.
    </p>
    <p v-else class="text-xs text-neutral-400 dark:text-neutral-600">Nothing captured yet.</p>

    <p
      v-if="statistics && rows.length > 0"
      class="mt-2 text-[11px] leading-snug text-neutral-500 dark:text-neutral-400"
    >
      Counted over the records this screen has read since the last reset. Anything that changes what
      a reading is — a capture setting, the window being measured, the cursors, the math
      channel — starts the count again on its own, because a mean over two configurations is a mean
      of two different measurements.
    </p>
  </CollapsibleSection>
</template>
