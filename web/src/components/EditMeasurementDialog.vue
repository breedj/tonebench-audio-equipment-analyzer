<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import Modal from './Modal.vue'
import AnnotationRows from './AnnotationRows.vue'
import MeasurementKindChip from './MeasurementKindChip.vue'
import { useMeasurements } from '../composables/useMeasurements'
import { useProjects } from '../composables/useProjects'
import { FFT_SPECTRUM_KIND } from '../measurements/kinds'
import { parseAnnotations, type ChartAnnotation } from '../measurements/chartAnnotations'
import { storedAnnotationFrame } from '../charts/annotationViews'
import { api, type MeasurementSummary } from '../api/client'

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

/**
 * This result's own annotations, read out of its stored `params_json`.
 *
 * <p>The list here is the only way to reach them once a result is saved: annotations are placed by
 * clicking the <i>live</i> chart, and nothing in this app puts a stored FFT result back on it. The
 * summary this dialog is handed carries a name and notes only, so the fetch is unavoidable.</p>
 *
 * <p><b>Null means they could not be read</b>, which is what keeps a failed fetch from wiping them:
 * onSave then sends nothing for the field, and the server leaves the stored list alone.</p>
 */
const annotations = ref<ChartAnnotation[] | null>(null)
const annotationsUnreadable = ref(false)
const isFft = computed(() => kind.value === FFT_SPECTRUM_KIND)

async function loadAnnotations(resultId: number) {
  annotations.value = null
  annotationsUnreadable.value = false
  try {
    const detail = await api.getMeasurement(resultId)
    annotations.value = parseAnnotations(
      (JSON.parse(detail.paramsJson) as { chartAnnotations?: unknown }).chartAnnotations,
      storedAnnotationFrame(detail.kind),
    )
  } catch {
    annotationsUnreadable.value = true
  }
}

watch(
  () => props.open,
  (open) => {
    if (!open || !props.item) return
    form.name = props.item.name
    form.notes = props.item.notes ?? ''
    annotations.value = null
    annotationsUnreadable.value = false
    if (isFft.value) void loadAnnotations(props.item.resultId)
  },
)

async function onSave() {
  if (!props.item || !form.name.trim()) return
  // The field is left off entirely unless the list was read: omitted means "leave them alone", and
  // an empty array would mean "cleared" — see UpdateMeasurementRequest.
  await update(props.item.resultId, {
    name: form.name.trim(),
    notes: form.notes.trim() || null,
    ...(annotations.value === null ? {} : { chartAnnotations: annotations.value }),
  })
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

      <!-- shrink-0, like every direct child of a scrolling dialog body: .dialog-card hides its
           overflow, so a card the flex column shrank would clip its own content and read as a
           failed load rather than as a missing scrollbar. -->
      <div v-if="isFft" class="dialog-card mt-3 shrink-0 flex flex-col gap-3 p-3.5">
        <div class="flex flex-col gap-1">
          <span class="text-[12px] font-semibold text-neutral-700 dark:text-neutral-200">Annotations</span>
          <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
            Drawn on this result's chart and listed in its report. Add new ones by clicking the live
            chart.
          </p>
        </div>
        <p v-if="annotationsUnreadable" class="text-[11px] text-neutral-500 dark:text-neutral-400">
          Could not read this result's annotations. Saving will leave them as they are.
        </p>
        <p
          v-else-if="annotations !== null && annotations.length === 0"
          class="text-[11px] text-neutral-500 dark:text-neutral-400"
        >
          This result has none.
        </p>
        <AnnotationRows v-if="annotations !== null" v-model="annotations" />
      </div>
    </div>

    <template #footer>
      <span class="flex-1"></span>
      <button type="button" class="btn-secondary" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" :disabled="!form.name.trim()" @click="onSave">Save</button>
    </template>
  </Modal>
</template>
