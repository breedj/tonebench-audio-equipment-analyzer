<script setup lang="ts">
/**
 * Which stimulus the generator plays, picked from a figure of each.
 *
 * <p>The same shape as <code>ScopeMeasurementPickerDialog</code>, and for the same reason: ten
 * names in a dropdown are a list a reader parses, where ten pictures are scanned — and a
 * <code>&lt;select&gt;</code> cannot carry a picture or the line of prose that says what each one
 * is <em>for</em>, which is the part that actually decides the choice.</p>
 *
 * <p><b>It stages and commits on Apply</b>, so Cancel really discards — unlike the dropdown it
 * replaces, where every arrow key reconfigured the generator on the way past.</p>
 */
import { ref, watch } from 'vue'
import Modal from './Modal.vue'
import ScopeToggleIcon from './ScopeToggleIcon.vue'
import { STIMULUS_KINDS } from '../generator/stimulusKinds'
import type { GeneratorKind } from '../api/client'

const props = defineProps<{ open: boolean; kind: GeneratorKind }>()
const emit = defineEmits<{ close: []; pick: [kind: GeneratorKind] }>()

const staged = ref<GeneratorKind>(props.kind)

// Re-seeded on every open, never watched continuously: a dialog that opened holding what was
// staged last time reads as having lost its place.
watch(
  () => props.open,
  (open) => {
    if (open) staged.value = props.kind
  },
)

function apply() {
  if (staged.value !== props.kind) emit('pick', staged.value)
  emit('close')
}
</script>

<template>
  <Modal :open="props.open" title="Stimulus" icon="sine" tone="orange" size="lg" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <div class="grid max-h-[32rem] grid-cols-1 gap-2.5 overflow-y-auto pr-1 sm:grid-cols-2">
        <button
          v-for="option in STIMULUS_KINDS"
          :key="option.value"
          type="button"
          class="flex items-start gap-3 rounded-lg border px-3 py-2.5 text-left transition-colors"
          :class="
            staged === option.value
              ? 'border-blue-500 bg-blue-500/10 text-blue-700 dark:border-blue-400 dark:text-blue-300'
              : 'border-neutral-200 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800'
          "
          :aria-pressed="staged === option.value"
          @click="staged = option.value"
        >
          <ScopeToggleIcon :name="option.icon" :size="22" class="mt-0.5" />
          <span class="min-w-0">
            <span class="block font-medium">{{ option.label }}</span>
            <span class="block text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
              {{ option.title }}
            </span>
          </span>
        </button>
      </div>

      <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        Whether a channel plays at all is the <strong>Play on</strong> pair above, not a stimulus of
        its own — so picking one here never starts the generator by itself.
      </p>
    </div>

    <template #footer>
      <button type="button" class="btn-secondary" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" @click="apply">Apply</button>
    </template>
  </Modal>
</template>
