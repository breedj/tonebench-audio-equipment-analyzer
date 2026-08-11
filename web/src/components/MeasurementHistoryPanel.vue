<script setup lang="ts">
import { reactive, watch } from 'vue'
import { api } from '../api/client'
import { useProjects } from '../composables/useProjects'
import { useMeasurements } from '../composables/useMeasurements'

const emit = defineEmits<{ error: [message: string] }>()

const { currentTypeId } = useProjects()
const { items, filter, error, load, remove } = useMeasurements()

const form = reactive({ fromDate: '', toDate: '' })

function applyFilter() {
  filter.value = {
    typeId: currentTypeId.value ?? undefined,
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

async function onDelete(id: number) {
  try {
    await remove(id)
  } catch (e) {
    emit('error', (e as Error).message)
  }
}
</script>

<template>
  <!-- The checkbox list from the design lives in MeasurementChecklist.vue, next to the chart. This
       is the rest of what History used to show (timestamps, THD headline, CSV export, delete, date
       filter) — not part of the new design, kept here for now until it's repositioned. -->
  <section class="rounded-lg border border-neutral-200 p-4 dark:border-neutral-800">
    <div class="mb-3 flex flex-wrap items-end justify-between gap-3">
      <h2 class="text-sm font-semibold text-neutral-900 dark:text-neutral-100">History details</h2>

      <div class="flex items-end gap-3 text-sm">
        <label class="flex flex-col gap-1">
          From
          <input
            v-model="form.fromDate"
            type="date"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="applyFilter"
          />
        </label>

        <label class="flex flex-col gap-1">
          To
          <input
            v-model="form.toDate"
            type="date"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="applyFilter"
          />
        </label>
      </div>
    </div>

    <p v-if="error" class="mb-2 text-xs text-red-600 dark:text-red-400">{{ error }}</p>

    <p v-if="currentTypeId === null" class="text-xs text-neutral-400 dark:text-neutral-600">
      Select a measurement to see its saved results.
    </p>
    <table v-else class="w-full text-sm">
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
            <div class="flex justify-end gap-2 text-xs whitespace-nowrap">
              <a
                :href="api.measurementCsvExportUrl(item.resultId)"
                download
                class="text-neutral-500 hover:text-neutral-900 dark:hover:text-neutral-100"
              >
                CSV
              </a>
              <button type="button" class="text-neutral-400 hover:text-red-600" @click="onDelete(item.resultId)">delete</button>
            </div>
          </td>
        </tr>
        <tr v-if="items.length === 0">
          <td colspan="4" class="py-1 text-xs text-neutral-400 dark:text-neutral-600">No measurements saved yet.</td>
        </tr>
      </tbody>
    </table>
  </section>
</template>
