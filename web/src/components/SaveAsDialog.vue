<script setup lang="ts">
import { ref, watch } from 'vue'
import Modal from './Modal.vue'

// Generic "type a new name, confirm" prompt — shared by the measurement-type and project Save
// As... flows, which differ only in title/label/what happens on confirm (handled by the caller).
const props = defineProps<{ open: boolean; title: string; label: string }>()
const emit = defineEmits<{ close: []; confirm: [name: string] }>()

const name = ref('')

watch(
  () => props.open,
  (open) => {
    if (open) name.value = ''
  },
)

function onConfirm() {
  const trimmed = name.value.trim()
  if (!trimmed) return
  emit('confirm', trimmed)
}
</script>

<template>
  <Modal :open="props.open" :title="props.title" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <label class="flex flex-col gap-1">
        {{ props.label }}
        <input
          v-model="name"
          type="text"
          autofocus
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @keyup.enter="onConfirm"
        />
      </label>

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
          :disabled="!name.trim()"
          @click="onConfirm"
        >
          Save
        </button>
      </div>
    </div>
  </Modal>
</template>
