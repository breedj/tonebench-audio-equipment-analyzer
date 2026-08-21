<script setup lang="ts">
import { reactive, watch } from 'vue'
import Modal from './Modal.vue'
import { useMeasurements } from '../composables/useMeasurements'
import type { MeasurementSummary } from '../api/client'

const props = defineProps<{ open: boolean; item: MeasurementSummary | null }>()
const emit = defineEmits<{ close: []; saved: [] }>()

const { update, error } = useMeasurements()

const form = reactive({
  name: '',
  notes: '',
})

watch(
  () => props.open,
  (open) => {
    if (!open || !props.item) return
    form.name = props.item.name
    form.notes = props.item.notes ?? ''
  },
)

async function onSave() {
  if (!props.item || !form.name.trim()) return
  await update(props.item.resultId, { name: form.name.trim(), notes: form.notes.trim() || null })
  emit('saved')
  emit('close')
}
</script>

<template>
  <Modal :open="props.open" title="Edit measurement" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <label class="flex flex-col gap-1">
        Name
        <input
          v-model="form.name"
          type="text"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>

      <label class="flex flex-col gap-1">
        Notes
        <textarea
          v-model="form.notes"
          rows="3"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>

      <p v-if="error" class="text-xs text-red-600 dark:text-red-400">{{ error }}</p>

      <div class="flex justify-end gap-2 pt-2">
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="emit('close')"
        >
          Cancel
        </button>
        <button
          type="button"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          :disabled="!form.name.trim()"
          @click="onSave"
        >
          Save
        </button>
      </div>
    </div>
  </Modal>
</template>
