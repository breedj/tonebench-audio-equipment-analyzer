<script setup lang="ts">
import { computed } from 'vue'
import Modal from './Modal.vue'
import { useConfirm } from '../composables/useConfirm'

// The one yes/no dialog, mounted once in App.vue and driven by useConfirm().confirm(...) from
// wherever a question needs asking — the replacement for window.confirm.

const { state, answer } = useConfirm()
const open = computed(() => state.value !== null)
</script>

<template>
  <Modal :open="open" :title="state?.title ?? ''" @close="answer(false)">
    <div class="flex flex-col gap-3 text-sm">
      <p class="whitespace-pre-line text-neutral-700 dark:text-neutral-300">{{ state?.message }}</p>

      <div class="flex justify-end gap-2 pt-2">
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="answer(false)"
        >
          {{ state?.cancelLabel ?? 'Cancel' }}
        </button>
        <button
          type="button"
          class="rounded px-3 py-1 text-white"
          :class="state?.danger
            ? 'bg-red-600 hover:bg-red-500'
            : 'bg-neutral-900 hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white'"
          autofocus
          @click="answer(true)"
        >
          {{ state?.confirmLabel ?? 'OK' }}
        </button>
      </div>
    </div>
  </Modal>
</template>
