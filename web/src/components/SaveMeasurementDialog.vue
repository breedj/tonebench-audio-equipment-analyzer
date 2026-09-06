<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import Modal from './Modal.vue'
import { useProjects } from '../composables/useProjects'
import { useMeasurements } from '../composables/useMeasurements'
import { isFrequencyResponseKind, isImpulseResponseKind, isSweepKind } from '../measurements/kinds'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: []; saved: [] }>()

// Always saves into whichever project/measurement is currently open in the sidebar — this dialog
// only opens from the "Save measurement result" button, which itself only shows once a type is
// selected (see App.vue), so currentTypeId is never null here. Same currentType/currentProject
// lookup App.vue itself does — useProjects doesn't keep a derived "current" object of its own.
const { projects, types, currentTypeId } = useProjects()
const { save, saveSweep, saveResponse, saveImpulse, error } = useMeasurements()

const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)
const currentProject = computed(() => projects.value.find((p) => p.id === currentType.value?.projectId) ?? null)

const form = reactive({
  name: '',
  notes: '',
})

const canSave = computed(() => currentType.value !== null && form.name.trim().length > 0)

/** Local-time "yyyy-MM-dd HH:mm" — the default Name, so a save someone doesn't bother renaming
 * still sorts and reads sensibly in the history list rather than showing up blank. */
function timestampName(): string {
  const d = new Date()
  const pad = (n: number) => n.toString().padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    // Reset per open, not per mount — the dialog is a template singleton in App.vue.
    form.name = timestampName()
    form.notes = ''
  },
)

async function onSave() {
  if (!canSave.value || currentType.value === null) return
  const req = { measurementTypeId: currentType.value.id, name: form.name.trim(), notes: form.notes.trim() || null }
  // Which endpoint depends on the kind: a sweep's result is N captures built from the runner's
  // points, a response's is its runner's curves, an FFT's is the pipeline's single retained
  // spectrum. Same dialog either way — the name/notes it collects are all the difference the user
  // cares about.
  if (isImpulseResponseKind(currentType.value.kind)) await saveImpulse(req)
  else if (isFrequencyResponseKind(currentType.value.kind)) await saveResponse(req, currentType.value.kind)
  else if (isSweepKind(currentType.value.kind)) await saveSweep(req)
  else await save(req)
  emit('saved')
  emit('close')
}
</script>

<template>
  <Modal :open="props.open" title="Save measurement" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <p class="text-xs text-neutral-500 dark:text-neutral-400">
        Saving into <span class="font-medium text-neutral-700 dark:text-neutral-300">{{ currentProject?.name }} / {{ currentType?.name }}</span>
      </p>

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
