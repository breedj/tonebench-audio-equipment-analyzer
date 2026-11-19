<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import Modal from './Modal.vue'
import ScopeMeasurementIcon from './ScopeMeasurementIcon.vue'
import { useScopeMeasurements } from '../composables/useScopeMeasurements'
import {
  SCOPE_MEASUREMENTS,
  SCOPE_MEASUREMENT_GROUPS,
  scopeMeasurementsIn,
  type ScopeMeasurementGroup,
} from '../scope/measurementCatalogue'

/**
 * Which measurements the table under the chart shows.
 *
 * <p>Tabs and a grid of figures rather than the flat checkbox list the FFT screen's metric picker
 * uses, and the difference is the catalogue: thirty entries whose names differ by one character
 * (<em>+Width</em> against <em>−Width</em>, <em>Top</em> against <em>Base</em>) are a list to be
 * parsed, where a figure showing where on a trace the reading is taken is read at a glance. The
 * groups are the four a bench instrument sorts them into, which is also the order a reader looks
 * in: how tall, how long, everything else, and the two inputs against each other.</p>
 *
 * <p>Staged locally and committed on Apply, like every other dialog here, so Cancel really
 * discards.</p>
 */

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const { selected, setSelected } = useScopeMeasurements()

const staged = reactive(new Set<string>())
const tab = ref<ScopeMeasurementGroup>('Vertical')

watch(
  () => props.open,
  (open) => {
    if (!open) return
    staged.clear()
    for (const id of selected.value) staged.add(id)
    // Opened on the first tab each time rather than where it was left: the dialog is reached from a
    // button that says "measurements", not from a tab a reader was browsing, and a dialog that
    // opens somewhere other than its beginning reads as having lost its place.
    tab.value = 'Vertical'
  },
)

const entries = computed(() => scopeMeasurementsIn(tab.value))

/** How many of a tab's own entries are ticked, so the tabs say where the chosen ones are. */
function chosenIn(group: ScopeMeasurementGroup): number {
  let n = 0
  for (const def of SCOPE_MEASUREMENTS) if (def.group === group && staged.has(def.id)) n += 1
  return n
}

function toggle(id: string) {
  if (staged.has(id)) staged.delete(id)
  else staged.add(id)
}

function onApply() {
  // Through the catalogue's own order rather than the Set's insertion order, so the table reads the
  // same way whichever order the boxes were pressed in.
  setSelected(SCOPE_MEASUREMENTS.filter((def) => staged.has(def.id)).map((def) => def.id))
  emit('close')
}

function clearAll() {
  staged.clear()
}
</script>

<template>
  <!-- size="lg": the tiles are half again as big as they were, and three to a row at the default
       width would leave the labels truncating on the longer ones. -->
  <Modal :open="props.open" title="Add or remove measurements" icon="ruler" size="lg" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <div class="flex flex-wrap gap-1">
        <button
          v-for="group in SCOPE_MEASUREMENT_GROUPS"
          :key="group.value"
          type="button"
          class="tb-segment"
          :class="tab === group.value ? 'tb-segment-on' : ''"
          @click="tab = group.value"
        >
          {{ group.label }}
          <span v-if="chosenIn(group.value) > 0" class="ml-1 opacity-70">{{ chosenIn(group.value) }}</span>
        </button>
      </div>

      <!-- Every size here moves together, and the icon's is a prop rather than its own class --
           see ScopeMeasurementIcon. The figure is what the tile is scanned by: thirty-one entries
           named +Width, -Width, +Duty, -Duty, Top, Base and Amplitude differ by one character, so a
           list of them is read rather than scanned, and the drawing is what makes it a glance. -->
      <div class="grid max-h-[36rem] grid-cols-2 gap-2.5 overflow-y-auto pr-1 sm:grid-cols-3">
        <button
          v-for="def in entries"
          :key="def.id"
          type="button"
          class="flex items-center gap-3 rounded-lg border px-3 py-2.5 text-left text-[15px] transition-colors"
          :class="
            staged.has(def.id)
              ? 'border-blue-500 bg-blue-500/10 text-blue-700 dark:border-blue-400 dark:text-blue-300'
              : 'border-neutral-200 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800'
          "
          :title="def.title"
          :aria-pressed="staged.has(def.id)"
          @click="toggle(def.id)"
        >
          <ScopeMeasurementIcon :name="def.icon" size="lg" />
          <span class="min-w-0 truncate">{{ def.label }}</span>
        </button>
      </div>

      <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        Each one is shown for every input the chart is drawing, and for the math trace when one is —
        so a reading is taken off a channel by taking that channel off the screen. The
        <strong>Channel delay</strong> group reads both inputs together and has one row of its own.
      </p>

      <div class="flex items-center justify-between gap-2 border-t border-neutral-200 pt-3 dark:border-neutral-800">
        <button type="button" class="btn-secondary" :disabled="staged.size === 0" @click="clearAll">
          Clear all
        </button>
        <div class="flex gap-2">
          <button type="button" class="btn-secondary" @click="emit('close')">Cancel</button>
          <button type="button" class="btn-primary" @click="onApply">Apply</button>
        </div>
      </div>
    </div>
  </Modal>
</template>
