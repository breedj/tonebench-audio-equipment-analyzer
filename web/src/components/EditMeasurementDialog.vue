<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import Modal from './Modal.vue'
import MeasurementKindChip from './MeasurementKindChip.vue'
import { useMeasurements } from '../composables/useMeasurements'
import { useProjects } from '../composables/useProjects'
import type { MeasurementSummary } from '../api/client'

const props = defineProps<{ open: boolean; item: MeasurementSummary | null }>()
const emit = defineEmits<{ close: []; saved: [] }>()

const { update, error } = useMeasurements()

/**
 * The kind of the measurement this result belongs to, for the chip under the title. A summary
 * carries its measurement's *name* rather than its kind, and a name is unique within a project
 * (there is an index on it), so the open project's own list is where to look it up. No match — a
 * result from a project that has since been closed — simply shows the name without a chip.
 */
const { types } = useProjects()
const kind = computed(() => types.value.find((t) => t.name === props.item?.measurementTypeName)?.kind ?? null)

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
  <!-- The 2026-10-13 dialog chrome: a tinted glyph and a 17 px title over the measurement this
       result belongs to, one card on the ground tone, and the actions on a strip of their own. -->
  <Modal
    :open="props.open"
    title="Edit measurement"
    icon="pencil"
    tone="cyan"
    ground
    flush
    @close="emit('close')"
  >
    <template v-if="props.item" #subtitle>
      <span class="flex items-center gap-1.5 text-[12px] text-neutral-500 dark:text-neutral-400">
        <MeasurementKindChip v-if="kind" :kind="kind" :size="16" />
        {{ props.item.measurementTypeName }}
      </span>
    </template>

    <div class="p-4">
      <div class="dialog-card flex flex-col gap-3.5 p-3.5">
        <label class="flex flex-col gap-1.5 text-[12px] font-semibold text-neutral-700 dark:text-neutral-200">
          Name
          <input v-model="form.name" type="text" class="tb-input h-9 font-normal" />
        </label>

        <label class="flex flex-col gap-1.5 text-[12px] font-semibold text-neutral-700 dark:text-neutral-200">
          <span>Notes <span class="font-normal text-neutral-500 dark:text-neutral-400">— optional</span></span>
          <textarea
            v-model="form.notes"
            rows="4"
            placeholder="The DUT, the wiring, what to look for."
            class="tb-input h-auto resize-y py-2 font-normal leading-snug"
          />
        </label>

        <p v-if="error" class="text-[12px] text-red-600 dark:text-red-400">{{ error }}</p>
      </div>
    </div>

    <template #footer>
      <span class="flex-1"></span>
      <button type="button" class="btn-secondary" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" :disabled="!form.name.trim()" @click="onSave">Save</button>
    </template>
  </Modal>
</template>
