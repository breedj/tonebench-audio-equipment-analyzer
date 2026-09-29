<script setup lang="ts">
import { ref, watch } from 'vue'
import Modal from './Modal.vue'
import MeasurementKindIcon from './MeasurementKindIcon.vue'
import { useProjects } from '../composables/useProjects'
import { MEASUREMENT_KINDS } from '../measurements/kinds'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const { currentProjectId, createMeasurementType, selectType, error } = useProjects()

const selectedKind = ref<string | null>(null)
const name = ref('')

watch(
  () => props.open,
  (open) => {
    if (!open) return
    selectedKind.value = null
    name.value = ''
  },
)

function pickKind(kind: string) {
  selectedKind.value = kind
  name.value = MEASUREMENT_KINDS.find((k) => k.kind === kind)?.label ?? ''
}

async function onCreate() {
  if (currentProjectId.value === null || selectedKind.value === null || !name.value.trim()) return
  const id = await createMeasurementType(currentProjectId.value, { kind: selectedKind.value, name: name.value.trim(), notes: null })
  await selectType(id)
  emit('close')
}
</script>

<template>
  <!-- Wide, and the kinds in two columns: eleven of them in one column ran past the bottom of an
       ordinary window, so the last few were only found by scrolling a list that gave no sign it
       scrolled. Two columns of six fit without one. -->
  <Modal :open="props.open" title="New measurement" :wide="selectedKind === null" @close="emit('close')">
    <div v-if="selectedKind === null" class="flex flex-col gap-2 text-sm">
      <p v-if="currentProjectId === null" class="text-xs text-neutral-400 dark:text-neutral-600">
        Select or create a project first.
      </p>
      <div class="grid max-h-[80vh] grid-cols-1 gap-2 overflow-y-auto sm:grid-cols-2">
        <button
          v-for="k in MEASUREMENT_KINDS"
          :key="k.kind"
          type="button"
          class="flex items-start gap-3 rounded border border-neutral-200 p-3 text-left hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-800 dark:hover:bg-neutral-800"
          :disabled="currentProjectId === null"
          @click="pickKind(k.kind)"
        >
          <MeasurementKindIcon :kind="k.kind" class="mt-0.5 shrink-0 text-neutral-500 dark:text-neutral-400" />
          <span class="min-w-0">
            <span class="block font-medium text-neutral-900 dark:text-neutral-100">{{ k.label }}</span>
            <span class="block text-xs text-neutral-500 dark:text-neutral-400">{{ k.description }}</span>
          </span>
        </button>
      </div>
    </div>

    <div v-else class="flex flex-col gap-3 text-sm">
      <label class="flex flex-col gap-1">
        Name
        <input
          v-model="name"
          type="text"
          placeholder="e.g. FFT analysis"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        />
      </label>

      <p v-if="error" class="text-xs text-red-600 dark:text-red-400">{{ error }}</p>

      <div class="flex justify-end gap-2 pt-2">
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="selectedKind = null"
        >
          Back
        </button>
        <button
          type="button"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          :disabled="!name.trim()"
          @click="onCreate"
        >
          Create
        </button>
      </div>
    </div>
  </Modal>
</template>
