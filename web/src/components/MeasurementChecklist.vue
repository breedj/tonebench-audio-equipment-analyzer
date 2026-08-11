<script setup lang="ts">
import { useProjects } from '../composables/useProjects'
import { useMeasurements } from '../composables/useMeasurements'

const emit = defineEmits<{ error: [message: string] }>()

const { currentTypeId } = useProjects()
const { items, overlays, addOverlay, setOverlayVisible } = useMeasurements()

// Checkbox state mirrors the overlay's visibility (checked = plotted on the chart), matching the
// design's "check it to see it" model. Unchecking hides rather than removes the overlay, so its
// color/series slot stays stable if it's re-checked. The full history detail (timestamps, THD
// headline, CSV export, delete, date filter) lives in MeasurementHistoryPanel.vue further down the
// page, not here — this is deliberately just the compact checklist from the design.
function isChecked(resultId: number): boolean {
  return overlays.value.find((o) => o.id === resultId)?.visible ?? false
}

async function onToggle(resultId: number, event: Event) {
  const checked = (event.target as HTMLInputElement).checked
  try {
    if (checked) {
      await addOverlay(resultId)
    } else {
      setOverlayVisible(resultId, false)
    }
  } catch (e) {
    emit('error', (e as Error).message)
  }
}
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <h2 class="mb-2 text-xs font-semibold text-neutral-900 dark:text-neutral-100">History</h2>
    <p v-if="currentTypeId === null" class="text-xs text-neutral-400 dark:text-neutral-600">No measurement selected.</p>
    <ul v-else class="flex flex-col gap-1.5 text-sm">
      <li v-for="item in items" :key="item.resultId" class="flex items-center gap-2">
        <input
          type="checkbox"
          :checked="isChecked(item.resultId)"
          class="h-4 w-4 rounded border-neutral-400 dark:border-neutral-600"
          @change="onToggle(item.resultId, $event)"
        />
        <span class="truncate">{{ item.name }}</span>
      </li>
      <li v-if="items.length === 0" class="text-xs text-neutral-400 dark:text-neutral-600">No measurements saved yet.</li>
    </ul>
  </section>
</template>
