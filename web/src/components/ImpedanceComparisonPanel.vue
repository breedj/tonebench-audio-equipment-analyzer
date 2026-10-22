<script setup lang="ts">
import { computed } from 'vue'
import type { ImpedanceTrace } from '../composables/useImpedance'
import { DUT_TYPES, type DutType } from '../impedance/component'
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
 * Every reading this screen has, for every curve on the chart at once — the measurement on screen
 * and each checked history item — one row per curve.
 *
 * <p><b>This is the only readout now.</b> Until 2026-10-22 the settings rail also carried
 * Measurements, Component and Equivalent circuit panels describing the live curve alone, and these
 * tables are those same three blocks built from the same `readings.ts` functions — so the three
 * were the one-column case of what is here, and were removed on the ask (Joost). The two controls
 * that lived on them came with them, since they are what the tables below are read against: the
 * **Device under test** dropdown, which decides whether there is a component fit at all, and the
 * **At frequency** box the equivalent-circuit table follows.</p>
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
  /** The live run's series resistor, and the level it drove with — for the live row only. */
  referenceOhms: number
  generatorAmpDbv: number | null
}>()

/**
 * What is on the fixture. `None` means no component table at all, which is why this control cannot
 * live inside that table: there would be no way back from it.
 */
const dutType = defineModel<DutType>('dutType', { required: true })

/** The one frequency every row of the equivalent-circuit table is read at. */
const equivalentHz = defineModel<number>('equivalentHz', { required: true })

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
  const component = componentComparison(subjects.value, dutType.value)
  return [
    measurementsComparison(subjects.value),
    ...(component ? [component] : []),
    equivalentComparison(subjects.value, equivalentHz.value),
  ]
})
</script>

<template>
  <!-- Rendered whatever is on the chart, unlike the tables inside it: these two controls are the
       measurement's own settings, and a panel that appears only once something has been measured
       would take them with it until the first run. -->
  <CollapsibleSection title="Readings" size="sm">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        <span
          title="What is on the fixture. Nothing in an impedance curve can tell a choke's self-resonance from a driver's motional resonance -- they are the same shape -- so the readout asks rather than guesses."
        >
          Device under test
        </span>
        <select v-model="dutType" class="tb-input">
          <option v-for="option in DUT_TYPES" :key="option.value" :value="option.value">{{ option.label }}</option>
        </select>
      </label>

      <label class="tb-field">
        <span title="The one frequency the equivalent-circuit table below is read at.">At frequency (Hz)</span>
        <input v-model.number="equivalentHz" type="number" min="1" step="any" class="tb-input" />
      </label>
    </div>

    <div v-if="tables.length > 0" class="mt-4 flex flex-col gap-4">
      <div v-for="table in tables" :key="table.title" class="tb-field">
        <span class="text-xs text-neutral-500 dark:text-neutral-400">{{ table.title }}</span>
        <div class="overflow-x-auto">
          <table class="w-full text-xs">
            <thead>
              <tr class="border-b border-neutral-200 text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">
                <th class="py-1 pr-3 text-left font-normal">Measurement</th>
                <!-- `index` as the key, not the label: a Measurements table has two "at" columns. -->
                <th
                  v-for="(column, index) in table.columns"
                  :key="index"
                  class="py-1 pl-3 text-right font-normal"
                  :title="column.title"
                >
                  {{ column.label }}
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
