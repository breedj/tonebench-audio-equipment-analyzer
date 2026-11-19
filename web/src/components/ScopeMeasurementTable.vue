<script setup lang="ts">
import CollapsibleSection from './CollapsibleSection.vue'
import ScopeMeasurementIcon from './ScopeMeasurementIcon.vue'
import { useScopeMeasurements } from '../composables/useScopeMeasurements'
import { scopeMeasurement, SCOPE_MEASUREMENT_ABSENT } from '../scope/measurementCatalogue'
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

const { selected, statistics, stat, remove } = useScopeMeasurements()

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
</script>

<template>
  <CollapsibleSection title="Selected measurements">
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
            <td class="py-1 text-neutral-400 dark:text-neutral-500">
              <ScopeMeasurementIcon :name="iconOf(row)" />
            </td>
            <td class="py-1 pr-2" :title="row.title">
              <span class="text-neutral-500 dark:text-neutral-400">{{ row.sourceLabel }}</span>
              <span class="mx-1 text-neutral-300 dark:text-neutral-600">·</span>
              <span class="font-medium">{{ row.label }}</span>
              <button
                type="button"
                class="ml-1.5 text-neutral-300 opacity-0 transition-opacity hover:text-red-500 group-hover:opacity-100 dark:text-neutral-600"
                title="Take this measurement off the table."
                @click="remove(row.id)"
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
      Nothing chosen. Press <strong>Choose measurements…</strong> under Measurements in the display
      rail to put a reading here.
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
