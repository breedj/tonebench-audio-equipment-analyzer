<script setup lang="ts">
import { computed } from 'vue'
import type { ImpedanceTrace } from '../composables/useImpedance'
import type { DutType } from '../impedance/component'
import {
  componentComparison,
  equivalentComparison,
  measurementsComparison,
  type ComparisonSubject,
  type ComparisonTable,
} from '../impedance/comparison'
import CollapsibleSection from './CollapsibleSection.vue'
import type { OverlayEntry } from '../composables/useMeasurements'

/**
 * The readout panel's three blocks for every curve on the chart at once — the measurement on screen
 * and each checked history item — one row per curve.
 *
 * <p>The side panel can only ever describe the live measurement, and a checked history curve is
 * drawn beside it with no numbers of its own. Nothing had to be stored to fix that: the peak, the
 * component fit and the equivalent circuit are all functions of a curve, and a stored result keeps
 * its curve, so a history item is read through exactly the functions the side panel uses. The
 * transposition lives in `impedance/comparison.ts` because nothing in a `.vue` is testable.</p>
 *
 * <p>A history result's <b>own</b> reference resistor and generator level are read from its
 * `params_json`, not taken from the screen: two runs of one measurement type can legitimately have
 * used different ones, and quoting today's resistor against last week's curve would put a wrong
 * drive current in a table that exists to be compared.</p>
 */
const props = defineProps<{
  /** The live measurement's curves — its Impedance trace is the first row when there is one. */
  traces: ImpedanceTrace[]
  /** The checked history items, already filtered to those carrying impedance curves. */
  overlays: OverlayEntry[]
  /** What the type's Component dropdown says is on the fixture; `None` hides the component table. */
  dutType: DutType
  /** The live run's series resistor, and the level it drove with — for the live row only. */
  referenceOhms: number
  generatorAmpDbv: number | null
  /** The frequency the Equivalent circuit block is reading, shared with the side panel. */
  equivalentHz: number
}>()

/** A result's own reference resistor and generator level, off its stored `params_json`. */
function runFacts(paramsJson: string): { referenceOhms: number; generatorAmpDbv: number | null } {
  try {
    const params = JSON.parse(paramsJson) as { referenceOhms?: unknown; generatorAmpDbv?: unknown }
    return {
      referenceOhms: typeof params.referenceOhms === 'number' && params.referenceOhms > 0 ? params.referenceOhms : 0,
      generatorAmpDbv: typeof params.generatorAmpDbv === 'number' ? params.generatorAmpDbv : null,
    }
  } catch {
    return { referenceOhms: 0, generatorAmpDbv: null }
  }
}

const subjects = computed<ComparisonSubject[]>(() => {
  const rows: ComparisonSubject[] = []

  const live = props.traces.find((t) => t.kind === 'Impedance')
  if (live && live.freqHz.length > 0) {
    rows.push({
      id: 'live',
      name: 'Current measurement',
      trace: live,
      referenceOhms: props.referenceOhms,
      generatorAmpDbv: props.generatorAmpDbv,
    })
  }

  for (const overlay of props.overlays) {
    const trace = (overlay.impedanceTraces ?? []).find((t) => t.kind === 'Impedance')
    if (!trace || trace.freqHz.length === 0) continue
    const facts = runFacts(overlay.detail.paramsJson)
    rows.push({
      id: `stored-${overlay.id}`,
      name: overlay.detail.name,
      trace,
      referenceOhms: facts.referenceOhms,
      generatorAmpDbv: facts.generatorAmpDbv,
    })
  }

  return rows
})

const tables = computed<ComparisonTable[]>(() => {
  if (subjects.value.length === 0) return []
  const component = componentComparison(subjects.value, props.dutType)
  return [
    measurementsComparison(subjects.value),
    ...(component ? [component] : []),
    equivalentComparison(subjects.value, props.equivalentHz),
  ]
})
</script>

<template>
  <CollapsibleSection v-if="tables.length > 0" title="Readings" size="sm">
    <div class="flex flex-col gap-4">
      <div v-for="table in tables" :key="table.title" class="flex flex-col gap-1">
        <span class="text-xs text-neutral-500 dark:text-neutral-400">{{ table.title }}</span>
        <div class="overflow-x-auto">
          <table class="w-full text-xs">
            <thead>
              <tr class="border-b border-neutral-200 text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">
                <th class="py-1 pr-3 text-left font-normal">Measurement</th>
                <th v-for="column in table.columns" :key="column" class="py-1 pl-3 text-right font-normal">
                  {{ column }}
                </th>
              </tr>
            </thead>
            <tbody class="font-mono text-neutral-900 dark:text-neutral-100">
              <tr v-for="row in table.rows" :key="row.id" class="border-b border-neutral-100 last:border-0 dark:border-neutral-900">
                <td class="max-w-[16rem] truncate py-1 pr-3 font-sans" :title="row.name">{{ row.name }}</td>
                <td v-for="(cell, i) in row.cells" :key="i" class="py-1 pl-3 text-right whitespace-nowrap">{{ cell }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <!-- A withheld fit says why, named with the run it belongs to — the ported-loudspeaker case
             puts a note on one row of a table whose other rows are fine. -->
        <p
          v-for="note in table.notes"
          :key="`${note.id}-${note.text.slice(0, 16)}`"
          class="text-[11px] text-amber-600 dark:text-amber-400"
        >
          {{ note.name }}: {{ note.text }}
        </p>
      </div>
    </div>
  </CollapsibleSection>
</template>
