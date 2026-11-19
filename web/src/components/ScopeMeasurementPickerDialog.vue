<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import Modal from './Modal.vue'
import ScopeMeasurementIcon from './ScopeMeasurementIcon.vue'
import SegmentedToggle from './SegmentedToggle.vue'
import { useScopeMeasurements } from '../composables/useScopeMeasurements'
import {
  SCOPE_MEASUREMENTS,
  SCOPE_MEASUREMENT_GROUPS,
  SCOPE_SELECTION_SOURCES,
  scopeMeasurement,
  scopeMeasurementsIn,
  scopeSelectionEntry,
  scopeSelectionId,
  scopeSelectionSources,
  type ScopeMeasurementDef,
  type ScopeMeasurementGroup,
} from '../scope/measurementCatalogue'
import type { ScopeMeasurementSource } from '../scope/measurementRows'

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

const props = defineProps<{
  open: boolean
  /** The sources this capture is actually offering, for the labels and the "not being read" note. */
  sources: readonly ScopeMeasurementSource[]
}>()
const emit = defineEmits<{ close: [] }>()

const { selected, setSelected } = useScopeMeasurements()

/**
 * The staged selection, always in explicit <code>id:source</code> terms.
 *
 * <p>A bare id is expanded on the way in, which is why this dialog needs no bare case: it means
 * every source, so the three explicit entries say the same thing and the dialog can work in one
 * vocabulary. Expanding against the <b>fixed</b> three rather than against what this record
 * carries is what makes that lossless — a left-only capture must not be able to forget that a
 * reading was also wanted on the right.</p>
 *
 * <p>Channel delay entries stay bare: they read the two inputs together and have one row of their
 * own, so a source on one would name nothing.</p>
 */
const staged = reactive(new Set<string>())
const tab = ref<ScopeMeasurementGroup>('Vertical')

/** Which source the tiles are being ticked for. */
const source = ref<string>('ch0')

/** What a source is called when this capture is not offering it, so the control reads the same. */
const SOURCE_FALLBACK_LABELS: Record<string, string> = { ch0: 'Left', ch1: 'Right', math: 'Math' }

const sourceOptions = computed(() =>
  SCOPE_SELECTION_SOURCES.map((key) => {
    const live = props.sources.find((s) => s.key === key)
    return {
      value: key,
      label: live?.label ?? SOURCE_FALLBACK_LABELS[key] ?? key,
      // Never disabled, deliberately. A selection outlives any one capture, so wanting a reading
      // on an input this run is not reading is a perfectly ordinary thing to ask for -- it simply
      // appears when that input does. Disabling it would make that unsayable; the note below says
      // it instead.
      title: live
        ? `Readings taken off ${live.label}.`
        : 'Not being read by this measurement — anything ticked here appears when it is.',
      disabled: tab.value === 'Delay',
    }
  }),
)

/** Whether the chosen source is one this capture is actually producing. */
const sourceLive = computed(() => props.sources.some((s) => s.key === source.value))

/** The pair group has no source: it reads both inputs together and has one row of its own. */
const pairTab = computed(() => tab.value === 'Delay')

/** Area is channels-only, which is a picker rule now rather than only a row-builder one. */
function unavailable(def: ScopeMeasurementDef): boolean {
  return !pairTab.value && def.quantity === 'area' && source.value === 'math'
}

/** The staged entry a tile stands for, under whatever source is chosen. */
function entryFor(def: ScopeMeasurementDef): string {
  return def.group === 'Delay' ? def.id : scopeSelectionEntry(def.id, source.value)
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    staged.clear()
    for (const entry of selected.value) {
      const id = scopeSelectionId(entry)
      const def = scopeMeasurement(id)
      if (!def) continue
      if (def.group === 'Delay') {
        staged.add(id)
        continue
      }
      for (const key of scopeSelectionSources(entry)) staged.add(scopeSelectionEntry(id, key))
    }
    // The first source this capture is offering, so the dialog opens on something that has rows.
    source.value = props.sources[0]?.key ?? 'ch0'
    // Opened on the first tab each time rather than where it was left: the dialog is reached from a
    // button that says "measurements", not from a tab a reader was browsing, and a dialog that
    // opens somewhere other than its beginning reads as having lost its place.
    tab.value = 'Vertical'
  },
)

const entries = computed(() => scopeMeasurementsIn(tab.value))

/**
 * How many of a tab's own entries are ticked <em>for the chosen source</em>, so the tabs say where
 * the chosen ones are — and change as the source does, which is the point of the control.
 */
function chosenIn(group: ScopeMeasurementGroup): number {
  let n = 0
  for (const def of SCOPE_MEASUREMENTS) {
    if (def.group !== group) continue
    if (staged.has(group === 'Delay' ? def.id : scopeSelectionEntry(def.id, source.value))) n += 1
  }
  return n
}

function isOn(def: ScopeMeasurementDef): boolean {
  return staged.has(entryFor(def))
}

function toggle(def: ScopeMeasurementDef) {
  const entry = entryFor(def)
  if (staged.has(entry)) staged.delete(entry)
  else staged.add(entry)
}

function onApply() {
  // setSelected parses through the catalogue's own order, so the table reads the same way whichever
  // order the tiles were pressed in and whichever source they were pressed under.
  setSelected([...staged])
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

      <!-- Which trace the tiles below are ticked for. Tick Pk-Pk here, switch, tick it again, and
           you get a row each -- which is the whole of what this control is for: before it, a
           reading was taken off a channel by taking that channel off the screen. -->
      <div class="tb-field">
        <span class="tb-field-label">Source</span>
        <SegmentedToggle v-model="source" :options="sourceOptions" group-label="Source" />
      </div>
      <p v-if="pairTab" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        <strong>Channel delay</strong> reads both inputs together, so these have one row of their
        own and no source to choose.
      </p>
      <p v-else-if="!sourceLive" class="text-[11px] leading-snug text-amber-600 dark:text-amber-400">
        This measurement is not reading that trace at the moment, so nothing ticked here has a row
        yet — it appears as soon as it is.
      </p>

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
            unavailable(def)
              ? 'cursor-not-allowed border-neutral-200 opacity-40 dark:border-neutral-700'
              : isOn(def)
                ? 'border-blue-500 bg-blue-500/10 text-blue-700 dark:border-blue-400 dark:text-blue-300'
                : 'border-neutral-200 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800'
          "
          :title="unavailable(def) ? `${def.title} — channels only, not the math trace.` : def.title"
          :aria-pressed="isOn(def)"
          :disabled="unavailable(def)"
          @click="toggle(def)"
        >
          <ScopeMeasurementIcon :name="def.icon" size="lg" />
          <span class="min-w-0 truncate">{{ def.label }}</span>
        </button>
      </div>

      <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        Each tile is ticked for the <strong>Source</strong> above it, so one measurement can be
        watched on one trace or on several — tick it, switch source, tick it again. Readings chosen
        before this control existed are shown on every trace, which is what they always meant.
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
