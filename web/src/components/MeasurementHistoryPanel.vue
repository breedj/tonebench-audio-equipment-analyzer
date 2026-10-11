<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { api, type MeasurementSummary } from '../api/client'
import { useProjects } from '../composables/useProjects'
import { useMeasurements } from '../composables/useMeasurements'
import { isFrequencyResponseKind, isImpulseResponseKind } from '../measurements/kinds'
import { groupHistory, isSimulation, showsGroupHeaders } from '../measurements/historyGroups'
import EditMeasurementDialog from './EditMeasurementDialog.vue'
import SimulationReanalyzeDialog from './SimulationReanalyzeDialog.vue'
import AcSimulationReanalyzeDialog from './AcSimulationReanalyzeDialog.vue'
import CollapsibleSection from './CollapsibleSection.vue'

const emit = defineEmits<{ error: [message: string]; report: [resultId: number] }>()

const { currentTypeId, types } = useProjects()
const { items, filter, error, load, remove, reloadOverlay } = useMeasurements()

// This panel is always scoped to the measurement the sidebar has open, so every row shares its
// kind and the CSV links can be decided once rather than per row. An impulse response is the one
// kind whose result holds three grids that cannot share a table, so it offers one link each -- see
// CsvExporter.ExportImpulse.
const isImpulseResponse = computed(() =>
  isImpulseResponseKind(types.value.find((t) => t.id === currentTypeId.value)?.kind),
)

// Measured runs and imported simulations are both results of this measurement (migration 014),
// split for display only. Same module the checklist uses, so the two lists cannot disagree about
// what belongs where.
const groups = computed(() => groupHistory(items.value))
const withHeaders = computed(() => showsGroupHeaders(groups.value))

const form = reactive({ fromDate: '', toDate: '' })

const editDialogOpen = ref(false)
const editingItem = ref<MeasurementSummary | null>(null)

function onEdit(item: MeasurementSummary) {
  editingItem.value = item
  editDialogOpen.value = true
}

// Re-analysing an imported simulation -- offered only on those rows, because it is what was
// stored with them that makes it possible and only a simulation has it.
//
// Which dialog opens is decided by the measurement's *kind*, not by the row: a .tran import is a
// spectrum rebuilt from stored samples, an .ac import is a curve rebuilt from stored rows, and the
// two offer different settings and POST to different routes. Keying on isSimulation alone -- which
// is all the row itself knows -- would open the transient form over an AC result, read an FFT size
// and a window that are not in its params_json, silently fall back to 8192/Hann, and then 409.
const reanalyzeId = ref<number | null>(null)
const reanalyzeOpen = ref(false)
const reanalyzeAcOpen = ref(false)

const isResponseMeasurement = computed(() =>
  isFrequencyResponseKind(types.value.find((t) => t.id === currentTypeId.value)?.kind),
)

function onReanalyze(item: MeasurementSummary) {
  reanalyzeId.value = item.resultId
  if (isResponseMeasurement.value) reanalyzeAcOpen.value = true
  else reanalyzeOpen.value = true
}

/**
 * The result's spectrum, its metrics and its params_json have all been rewritten, so the list's
 * headline figures are stale and so is anything the chart is drawing from it. Reloading the list
 * covers the first; `reloadOverlay` covers the second and is a no-op when the result is not
 * checked.
 */
async function onReanalyzed(resultId: number) {
  try {
    await load()
    await reloadOverlay(resultId)
  } catch (e) {
    emit('error', (e as Error).message)
  }
}

function applyFilter() {
  filter.value = {
    measurementTypeId: currentTypeId.value ?? undefined,
    from: form.fromDate ? new Date(`${form.fromDate}T00:00:00Z`).toISOString() : undefined,
    // Exclusive upper bound (ts >= @from AND ts < @to server-side) — add a day so the picked end
    // date's own results are still included, not cut off at its midnight.
    to: form.toDate ? new Date(new Date(`${form.toDate}T00:00:00Z`).getTime() + 86_400_000).toISOString() : undefined,
  }
  load()
}

// The sidebar owns which measurement is open — this panel is always scoped to it, not a
// standalone project/type picker of its own.
watch(currentTypeId, applyFilter, { immediate: true })

// Which row's Delete button is armed (turned into Confirm/Cancel), if any -- an inline
// click-twice confirmation in place of a browser confirm() dialog.
const pendingDeleteId = ref<number | null>(null)

// A stale armed row would otherwise linger across a filter change or reload.
watch(items, () => {
  pendingDeleteId.value = null
})

function onDeleteClick(item: MeasurementSummary) {
  pendingDeleteId.value = item.resultId
}

function onCancelDelete() {
  pendingDeleteId.value = null
}

async function onConfirmDelete(item: MeasurementSummary) {
  pendingDeleteId.value = null
  try {
    await remove(item.resultId)
  } catch (e) {
    emit('error', (e as Error).message)
  }
}
</script>

<template>
  <!-- The checkbox list from the design lives in MeasurementChecklist.vue, next to the chart. This
       is the rest of what History used to show (timestamps, THD headline, CSV export, delete, date
       filter) — not part of the new design, kept here for now until it's repositioned. -->
  <CollapsibleSection title="History details" padding="p-4">
    <template #header-right>
      <div class="flex items-end gap-3 text-sm">
        <label class="flex flex-col gap-1">
          From
          <input
            v-model="form.fromDate"
            type="date"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            @change="applyFilter"
          />
        </label>

        <label class="flex flex-col gap-1">
          To
          <input
            v-model="form.toDate"
            type="date"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            @change="applyFilter"
          />
        </label>
      </div>
    </template>

    <p v-if="error" class="text-xs text-red-600 dark:text-red-400">{{ error }}</p>

    <p v-if="currentTypeId === null" class="text-xs text-neutral-400 dark:text-neutral-600">
      Select a measurement to see its saved results.
    </p>
    <div v-else class="overflow-x-auto">
      <table class="w-full text-sm">
        <tbody v-for="group in groups" :key="group.id">
          <tr v-if="withHeaders">
            <td colspan="4" class="pt-3 pb-1 text-[10px] font-semibold tracking-wide text-neutral-400 uppercase dark:text-neutral-500">
              {{ group.label }}
            </td>
          </tr>
          <tr v-for="item in group.items" :key="item.resultId" class="border-t border-neutral-100 dark:border-neutral-800">
            <td class="py-1 pr-2">
              {{ item.name }}
              <span
                v-if="isSimulation(item)"
                class="ml-1 rounded bg-violet-100 px-1 text-[9px] font-semibold tracking-wide text-violet-700 uppercase dark:bg-violet-950 dark:text-violet-300"
                title="Imported from an LTspice simulation, not measured."
              >
                sim
              </span>
              <span v-if="item.notes" class="text-neutral-400"> — {{ item.notes }}</span>
            </td>
            <td class="py-1 pr-2 whitespace-nowrap text-neutral-400">{{ new Date(item.timestampUtc).toLocaleString() }}</td>
            <td class="py-1 pr-2 whitespace-nowrap text-neutral-400">
              <span v-for="(headline, channel) in item.channelHeadlines" :key="channel" class="mr-2">
                ch{{ channel }}: THD {{ headline.thdPercent.toFixed(3) }}%
              </span>
            </td>
            <td class="py-1 text-right">
              <div class="flex justify-end gap-1.5 text-xs whitespace-nowrap">
                <!-- Hidden while this row is awaiting a delete confirmation, so Confirm/Cancel
                     are the only things clickable on it. -->
                <template v-if="pendingDeleteId !== item.resultId">
                  <button
                    type="button"
                    class="rounded border px-2 py-1 btn-action"
                    @click="onEdit(item)"
                  >
                    Edit
                  </button>
                  <!--
                    Only a simulation carries what this works from, and the two kinds carry
                    different things -- a transient run's stored samples, an AC run's stored
                    curves -- so the tooltip has to follow the same `isResponseMeasurement` the
                    button's own dialog does. Naming an FFT size and a window over an AC sweep
                    describes a form it will not show.
                  -->
                  <button
                    v-if="isSimulation(item)"
                    type="button"
                    class="rounded border px-2 py-1 btn-action"
                    :title="
                      isResponseMeasurement
                        ? 'Change the analysed band, the bandwidth reference or the level offset and rebuild the curves of this simulation from the ones stored with it. The file is not needed.'
                        : 'Change the window, FFT size, level offset, start time or band and recompute the spectrum of this simulation from the samples stored with it. The file is not needed.'
                    "
                    @click="onReanalyze(item)"
                  >
                    Re-analyse
                  </button>
                  <a
                    :href="api.measurementCsvExportUrl(item.resultId)"
                    download
                    class="rounded border border-emerald-300 bg-emerald-50 px-2 py-1 text-emerald-700 hover:bg-emerald-100 dark:border-emerald-700 dark:bg-emerald-950/40 dark:text-emerald-300 dark:hover:bg-emerald-900/40"
                    :title="isImpulseResponse ? 'The impulse response itself, one row per sample, at full resolution.' : undefined"
                  >
                    CSV
                  </a>
                  <!-- An impulse response's other two grids. Separate links rather than one file:
                       its samples are against time, its gated magnitude and phase against
                       frequency, and its harmonic curves against fundamental frequency, each on
                       its own grid -- see CsvExporter.ExportImpulse. -->
                  <template v-if="isImpulseResponse">
                    <a
                      :href="api.measurementCsvExportUrl(item.resultId, 'curves')"
                      download
                      class="rounded border border-emerald-300 bg-emerald-50 px-2 py-1 text-emerald-700 hover:bg-emerald-100 dark:border-emerald-700 dark:bg-emerald-950/40 dark:text-emerald-300 dark:hover:bg-emerald-900/40"
                      title="The gated magnitude, phase and coherence against frequency."
                    >
                      Curves
                    </a>
                    <a
                      :href="api.measurementCsvExportUrl(item.resultId, 'harmonics')"
                      download
                      class="rounded border border-emerald-300 bg-emerald-50 px-2 py-1 text-emerald-700 hover:bg-emerald-100 dark:border-emerald-700 dark:bg-emerald-950/40 dark:text-emerald-300 dark:hover:bg-emerald-900/40"
                      title="The harmonic-distortion curves against fundamental frequency. Only measurements run with harmonic extraction on have them."
                    >
                      Harmonics
                    </a>
                  </template>
                  <button
                    type="button"
                    class="rounded border border-emerald-300 bg-emerald-50 px-2 py-1 text-emerald-700 hover:bg-emerald-100 dark:border-emerald-700 dark:bg-emerald-950/40 dark:text-emerald-300 dark:hover:bg-emerald-900/40"
                    title="A PDF report of this stored result."
                    @click="emit('report', item.resultId)"
                  >
                    Report
                  </button>
                </template>
                <template v-if="pendingDeleteId === item.resultId">
                  <button
                    type="button"
                    class="rounded border border-red-300 bg-red-50 px-2 py-1 text-red-700 hover:bg-red-100 dark:border-red-800 dark:bg-red-950 dark:text-red-400 dark:hover:bg-red-900"
                    @click="onConfirmDelete(item)"
                  >
                    Confirm
                  </button>
                  <button
                    type="button"
                    class="rounded border px-2 py-1 btn-action"
                    @click="onCancelDelete"
                  >
                    Cancel
                  </button>
                </template>
                <button
                  v-else
                  type="button"
                  class="rounded border border-red-300 bg-red-50 px-2 py-1 text-red-700 hover:bg-red-100 dark:border-red-800 dark:bg-red-950 dark:text-red-400 dark:hover:bg-red-900"
                  title="Moves this result to the trash, where it can be restored."
                  @click="onDeleteClick(item)"
                >
                  Delete
                </button>
              </div>
            </td>
          </tr>
        </tbody>
        <tbody v-if="items.length === 0">
          <tr>
            <td colspan="4" class="py-1 text-xs text-neutral-400 dark:text-neutral-600">No measurements saved yet.</td>
          </tr>
        </tbody>
      </table>
    </div>

    <EditMeasurementDialog :open="editDialogOpen" :item="editingItem" @close="editDialogOpen = false" />

    <SimulationReanalyzeDialog
      :open="reanalyzeOpen"
      :result-id="reanalyzeId"
      @close="reanalyzeOpen = false"
      @reanalyzed="onReanalyzed"
    />

    <AcSimulationReanalyzeDialog
      :open="reanalyzeAcOpen"
      :result-id="reanalyzeId"
      @close="reanalyzeAcOpen = false"
      @reanalyzed="onReanalyzed"
    />
  </CollapsibleSection>
</template>
