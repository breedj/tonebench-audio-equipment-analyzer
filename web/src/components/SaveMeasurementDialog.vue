<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import Modal from './Modal.vue'
import { useProjects } from '../composables/useProjects'
import { useMeasurements } from '../composables/useMeasurements'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: []; saved: [] }>()

const { projects, types, loadTypes, currentProjectId, currentTypeId } = useProjects()
const { save, error } = useMeasurements()

const form = reactive({
  projectId: null as number | null,
  measurementTypeId: null as number | null,
  name: '',
  notes: '',
})

const canSave = computed(() => form.measurementTypeId !== null && form.name.trim().length > 0)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    // Reset per open, not per mount — the dialog is a template singleton in App.vue. Defaults to
    // whatever the sidebar currently has open, since that's almost always what "Save" means here.
    form.name = ''
    form.notes = ''
    form.projectId = currentProjectId.value ?? projects.value[0]?.id ?? null
  },
)

watch(
  () => form.projectId,
  async (projectId) => {
    form.measurementTypeId = null
    if (projectId === null) return
    await loadTypes(projectId)
    if (projectId === currentProjectId.value && currentTypeId.value !== null) form.measurementTypeId = currentTypeId.value
  },
  { immediate: true },
)

async function onSave() {
  if (!canSave.value || form.measurementTypeId === null) return
  await save({ measurementTypeId: form.measurementTypeId, name: form.name.trim(), notes: form.notes.trim() || null })
  emit('saved')
  emit('close')
}
</script>

<template>
  <Modal :open="props.open" title="Save measurement" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <label class="flex flex-col gap-1">
        Project
        <select
          v-model.number="form.projectId"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        >
          <option v-for="p in projects" :key="p.id" :value="p.id">{{ p.name }}</option>
        </select>
      </label>

      <label class="flex flex-col gap-1">
        Measurement type
        <select
          v-model.number="form.measurementTypeId"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        >
          <option v-for="t in types" :key="t.id" :value="t.id">{{ t.name }}</option>
        </select>
        <span v-if="form.projectId !== null && types.length === 0" class="text-xs text-neutral-400 dark:text-neutral-600">
          This project has no measurement types yet — add one in the Projects panel first.
        </span>
      </label>

      <label class="flex flex-col gap-1">
        Name
        <input
          v-model="form.name"
          type="text"
          placeholder="e.g. before mod"
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
          :disabled="!canSave"
          @click="onSave"
        >
          Save
        </button>
      </div>
    </div>
  </Modal>
</template>
