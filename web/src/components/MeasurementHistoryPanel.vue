<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { api, type MeasurementSummary } from '../api/client'
import { useProjects } from '../composables/useProjects'
import { useMeasurements } from '../composables/useMeasurements'
import EditMeasurementDialog from './EditMeasurementDialog.vue'
import CollapsibleSection from './CollapsibleSection.vue'

const emit = defineEmits<{ error: [message: string]; report: [resultId: number] }>()

const { currentTypeId } = useProjects()
const { items, filter, error, load, remove } = useMeasurements()

const form = reactive({ fromDate: '', toDate: '' })

const editDialogOpen = ref(false)
const editingItem = ref<MeasurementSummary | null>(null)

function onEdit(item: MeasurementSummary) {
  editingItem.value = item
  editDialogOpen.value = true
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
        <tbody>
          <tr v-for="item in items" :key="item.resultId" class="border-t border-neutral-100 dark:border-neutral-800">
            <td class="py-1 pr-2">
              {{ item.name }}
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
                <button
                  type="button"
                  class="rounded border border-blue-300 bg-blue-50 px-2 py-1 text-blue-700 hover:bg-blue-100 dark:border-blue-700 dark:bg-blue-950/40 dark:text-blue-300 dark:hover:bg-blue-900/40"
                  @click="onEdit(item)"
                >
                  Edit
                </button>
                <a
                  :href="api.measurementCsvExportUrl(item.resultId)"
                  download
                  class="rounded border border-emerald-300 bg-emerald-50 px-2 py-1 text-emerald-700 hover:bg-emerald-100 dark:border-emerald-700 dark:bg-emerald-950/40 dark:text-emerald-300 dark:hover:bg-emerald-900/40"
                >
                  CSV
                </a>
                <button
                  type="button"
                  class="rounded border border-emerald-300 bg-emerald-50 px-2 py-1 text-emerald-700 hover:bg-emerald-100 dark:border-emerald-700 dark:bg-emerald-950/40 dark:text-emerald-300 dark:hover:bg-emerald-900/40"
                  title="A PDF report of this stored result."
                  @click="emit('report', item.resultId)"
                >
                  Report
                </button>
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
                    class="rounded border border-blue-300 bg-blue-50 px-2 py-1 text-blue-700 hover:bg-blue-100 dark:border-blue-700 dark:bg-blue-950/40 dark:text-blue-300 dark:hover:bg-blue-900/40"
                    @click="onCancelDelete"
                  >
                    Cancel
                  </button>
                </template>
                <button
                  v-else
                  type="button"
                  class="rounded border border-red-300 bg-red-50 px-2 py-1 text-red-700 hover:bg-red-100 dark:border-red-800 dark:bg-red-950 dark:text-red-400 dark:hover:bg-red-900"
                  @click="onDeleteClick(item)"
                >
                  Delete
                </button>
              </div>
            </td>
          </tr>
          <tr v-if="items.length === 0">
            <td colspan="4" class="py-1 text-xs text-neutral-400 dark:text-neutral-600">No measurements saved yet.</td>
          </tr>
        </tbody>
      </table>
    </div>

    <EditMeasurementDialog :open="editDialogOpen" :item="editingItem" @close="editDialogOpen = false" />
  </CollapsibleSection>
</template>
