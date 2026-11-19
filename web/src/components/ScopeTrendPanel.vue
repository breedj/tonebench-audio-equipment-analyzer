<script setup lang="ts">
import { computed, watch } from 'vue'
import CollapsibleSection from './CollapsibleSection.vue'
import ScopeTrendChart from './ScopeTrendChart.vue'
import SegmentedToggle from './SegmentedToggle.vue'
import { useScopeMeasurements } from '../composables/useScopeMeasurements'
import type { ScopeMeasurementRow } from '../scope/measurementRows'
import { SCOPE_SERIES_CAPACITY } from '../scope/measurementStats'

/**
 * One chosen reading plotted over the records it came from — the trend, or the distribution of its
 * values.
 *
 * <p><b>What this answers that the table beside it cannot.</b> Mean, Min, Max, Pk-Pk and Stdev are
 * <em>order-free</em> and <em>shape-free</em>: a reading that drifted steadily across a run, one
 * that sat still and jumped once, and one that alternates between two values every record all give
 * the same five figures — and so do a bell and two clusters a long way apart whose mean no record
 * ever read. Each of those is a different fault, and each is one glance at a plot.</p>
 *
 * <p><b>One reading at a time, chosen from the table's own rows.</b> Every row at once is the
 * table, which is already there; what a plot is for is looking at one figure closely. The rows come
 * from the table rather than from the catalogue so the dropdown offers exactly what is being
 * measured — a reading for a channel that is not drawn has no row and so cannot be chosen.</p>
 *
 * <p><b>Rendered whenever the screen is</b>, like the measurement table, rather than appearing with
 * the first record: a card that comes and goes changes the length of the column every time a run
 * starts.</p>
 */

const props = defineProps<{
  rows: readonly ScopeMeasurementRow[]
}>()

/** Which row is plotted, by its row key — stored with the measurement like any display preference. */
const rowKey = defineModel<string>('rowKey', { required: true })
const view = defineModel<'Trend' | 'Distribution'>('view', { required: true })

const { series, seriesStart, stat, statsVersion } = useScopeMeasurements()

const VIEWS = [
  {
    value: 'Trend' as const,
    label: 'Trend',
    title: 'Each record’s reading against the record it came from — drift, a step, an intermittent fault.',
  },
  {
    value: 'Distribution' as const,
    label: 'Distribution',
    title:
      'How often each value came up. A bell is noise; two clusters are something switching; a long tail on one side is a reading that is usually fine and occasionally much worse.',
  },
]

/** The row the plot is of, or the first one on offer where the stored key names a row that has gone. */
const row = computed<ScopeMeasurementRow | null>(() => {
  if (props.rows.length === 0) return null
  return props.rows.find((r) => r.key === rowKey.value) ?? props.rows[0]
})

/**
 * Keeps the stored key on a row that exists.
 *
 * <p>Written rather than merely fallen back to, so the dropdown's own value matches the plot: a
 * select bound to a value its options do not contain renders blank, which reads as a control that
 * failed rather than as one whose row was unticked. It marks the measurement dirty like any other
 * display edit, which is right — the stored key really has changed.</p>
 */
watch(
  () => [props.rows, rowKey.value] as const,
  () => {
    const current = row.value
    if (current && current.key !== rowKey.value) rowKey.value = current.key
  },
  { immediate: true },
)

const plotted = computed(() => {
  // Read so the plot is redrawn as records arrive; the accumulators themselves are not reactive.
  void statsVersion.value
  const key = row.value?.key
  if (!key) return { series: new Float64Array(0), firstRecord: 0 }
  return { series: series(key), firstRecord: seriesStart(key) }
})

/**
 * Says so when the statistics have counted more records than the plot can show.
 *
 * <p>The one way a bounded series misleads rather than merely omits: a Count of nine thousand
 * beside a plot of a thousand invites the reading that the plot is of the run.</p>
 */
const boundNote = computed(() => {
  void statsVersion.value
  const key = row.value?.key
  const counted = key ? (stat(key)?.count ?? 0) : 0
  if (counted <= SCOPE_SERIES_CAPACITY) return null
  return `The statistics count all ${counted.toLocaleString()} records read; the plot keeps the newest ${SCOPE_SERIES_CAPACITY.toLocaleString()}.`
})
</script>

<template>
  <CollapsibleSection title="Reading over time" icon="bars" tone="indigo">
    <div v-if="rows.length === 0" class="text-xs text-neutral-500 dark:text-neutral-400">
      Tick a measurement under the chart and its readings are plotted here.
    </div>

    <template v-else>
      <div class="mb-2 flex flex-wrap items-end gap-3">
        <label class="tb-field min-w-[12rem] flex-1">
          <span class="tb-field-label">Reading</span>
          <select v-model="rowKey" class="tb-input">
            <option v-for="option in rows" :key="option.key" :value="option.key">
              {{ option.label }} · {{ option.sourceLabel }}
            </option>
          </select>
        </label>
        <div class="tb-field w-[16rem]">
          <span class="tb-field-label">Plot</span>
          <SegmentedToggle v-model="view" :options="VIEWS" group-label="Plot" />
        </div>
      </div>

      <ScopeTrendChart
        :series="plotted.series"
        :first-record="plotted.firstRecord"
        :view="view"
        :format="row?.format ?? ((v: number) => String(v))"
        :label="row ? `${row.label} · ${row.sourceLabel}` : ''"
      />

      <p v-if="boundNote" class="mt-1 text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        {{ boundNote }}
      </p>
    </template>
  </CollapsibleSection>
</template>
