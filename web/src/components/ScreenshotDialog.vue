<script setup lang="ts">
import { ref } from 'vue'
import Modal from './Modal.vue'

/**
 * The Screenshot button's one question: colour, or black and white. The choice is kept while the
 * app is open (the dialog is one instance in App.vue, and its state is not reset per open), so a
 * series of screenshots for one document needs the answer once; it is not stored — it is a choice
 * about a file, not a preference about the app. What "black and white" does to the picture is
 * monochromeChartPng's.
 */
const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: []; confirm: [monochrome: boolean] }>()

const monochrome = ref(false)
</script>

<template>
  <Modal :open="props.open" title="Screenshot" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <p class="text-xs text-neutral-500 dark:text-neutral-400">
        The chart on screen as a 1600 × 900 PNG in the light colour scheme, at the zoom shown.
      </p>

      <fieldset class="flex flex-col gap-1">
        <legend class="mb-1 text-xs text-neutral-500 dark:text-neutral-400">Colours</legend>
        <label class="flex items-center gap-2">
          <input v-model="monochrome" type="radio" :value="false" />
          <span>Colour</span>
        </label>
        <label class="flex items-center gap-2">
          <input v-model="monochrome" type="radio" :value="true" />
          <span>Black and white</span>
          <span class="text-xs text-neutral-500 dark:text-neutral-400">for a monochrome printer — line styles are kept, hues become greys</span>
        </label>
      </fieldset>

      <div class="flex items-center justify-end gap-2 pt-2">
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
          @click="emit('confirm', monochrome)"
        >
          Download PNG
        </button>
      </div>
    </div>
  </Modal>
</template>
