<script setup lang="ts">
import { reactive, watch } from 'vue'
import Modal from './Modal.vue'
import { AVAILABLE_METRIC_LABELS, GEN_METRIC_LABEL } from '../metrics'
import { useMetricSelection } from '../composables/useMetricSelection'

// Gen first, separated from the per-channel catalogue below — it's the one entry here that isn't
// an L:/R: tile (see GEN_METRIC_LABEL), but it's staged/persisted through the same selection list.
const PICKER_LABELS = [GEN_METRIC_LABEL, ...AVAILABLE_METRIC_LABELS]

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const { selectedMetrics, setSelectedMetrics } = useMetricSelection()

// Staged locally so Cancel discards without touching the shared selection — same "edit a local
// copy, commit on Apply" shape as SpectrumConfigPanel's form vs. useSpectrumConfig's live config.
const staged = reactive(new Set<string>())

watch(
  () => props.open,
  (open) => {
    if (!open) return
    staged.clear()
    for (const label of selectedMetrics.value) staged.add(label)
  },
)

function toggle(label: string, checked: boolean) {
  if (checked) staged.add(label)
  else staged.delete(label)
}

function onApply() {
  setSelectedMetrics(PICKER_LABELS.filter((label) => staged.has(label)))
  emit('close')
}
</script>

<template>
  <Modal :open="props.open" title="Add or remove metrics" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <div class="grid max-h-80 grid-cols-2 gap-x-4 gap-y-1.5 overflow-y-auto sm:grid-cols-3">
        <label v-for="label in PICKER_LABELS" :key="label" class="flex items-center gap-1.5">
          <input
            type="checkbox"
            class="h-4 w-4 rounded border-neutral-400 dark:border-neutral-600"
            :checked="staged.has(label)"
            @change="toggle(label, ($event.target as HTMLInputElement).checked)"
          />
          {{ label }}
        </label>
      </div>

      <div class="flex justify-end gap-2 border-t border-neutral-200 pt-3 dark:border-neutral-800">
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="emit('close')"
        >
          Cancel
        </button>
        <button
          type="button"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          @click="onApply"
        >
          Apply
        </button>
      </div>
    </div>
  </Modal>
</template>
