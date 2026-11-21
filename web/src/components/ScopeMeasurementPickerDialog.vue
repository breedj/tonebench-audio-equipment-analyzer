<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import Modal from './Modal.vue'
import PickerFigureWell from './PickerFigureWell.vue'
import PickerSectionHeading from './PickerSectionHeading.vue'
import PickerTileCheck from './PickerTileCheck.vue'
import ScopeMeasurementIcon from './ScopeMeasurementIcon.vue'
import SegmentedToggle from './SegmentedToggle.vue'
import { sectionMarkColor } from '../panels/pickerSections'
import { useScopeMeasurements } from '../composables/useScopeMeasurements'
import {
  SCOPE_MEASUREMENTS,
  SCOPE_MEASUREMENT_GROUPS,
  SCOPE_SELECTION_SOURCES,
  scopeMeasurement,
  scopeMeasurementSections,
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
 * <p><b>And sections inside each tab</b> (Joost's design, 2026-11-20). Fifteen tiles on the
 * Vertical tab is a list again, which is what the figures exist to prevent; under five headings it
 * is five short ones, and each heading says what its members have in common — that <em>Top</em> and
 * <em>Base</em> are settled levels rather than extremes is the whole difference between them and
 * <em>Max</em> and <em>Min</em>, and the heading says it once instead of four tooltips saying it
 * each.</p>
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
const SOURCE_FALLBACK_LABELS: Record<string, string> = {
  ch0: 'Left',
  ch1: 'Right',
  math1: 'Math 1',
  math2: 'Math 2',
}

/**
 * Each source's own trace ink, so the control agrees with the chart.
 *
 * <p>Repeated from <code>OscilloscopeScreenChart</code> rather than imported, for the reason the
 * display rail's own pair gives: a dialog reaching into a chart to colour a dot is the wrong way
 * round, and these are the app's convention rather than that component's.</p>
 */
const SOURCE_INK: Record<string, string> = {
  ch0: '#3b82f6',
  ch1: '#f97316',
  math1: '#a855f7',
  math2: '#ec4899',
}

const sourceOptions = computed(() =>
  SCOPE_SELECTION_SOURCES.map((key) => {
    const live = props.sources.find((s) => s.key === key)
    const count = chosenOn(key)
    return {
      value: key,
      label: live?.label ?? SOURCE_FALLBACK_LABELS[key] ?? key,
      dot: SOURCE_INK[key],
      // How many readings this source already holds, across every tab: the one number that says
      // where the chosen ones went, and the reason a reader opens a source they are not on.
      badge: count,
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

/** The chosen tab's sections with their entries, which is what the body draws. */
const sections = computed(() => scopeMeasurementSections(tab.value))

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

/** How many readings one source holds, over every tab — the Source control's own badges. */
function chosenOn(key: string): number {
  let n = 0
  for (const def of SCOPE_MEASUREMENTS) {
    if (def.group === 'Delay') continue
    if (staged.has(scopeSelectionEntry(def.id, key))) n += 1
  }
  return n
}

/** What the footer reports: the chosen source's own tally, or the pair group's. */
const summary = computed(() => {
  if (pairTab.value) {
    const n = chosenIn('Delay')
    return `${n} selected on the pair`
  }
  const label = props.sources.find((s) => s.key === source.value)?.label ??
    SOURCE_FALLBACK_LABELS[source.value] ??
    source.value
  return `${chosenOn(source.value)} selected on ${label}`
})

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
      <!-- Pills rather than `tb-segment-on`'s lifted track (Joost's design, 2026-11-20). These are
           tabs and not a choice of setting: there is no track behind them, and that class's pink is
           the app's "this segment is on" and would sit beside a blue count saying the same thing in
           another colour. -->
      <div class="flex flex-wrap gap-1" role="tablist">
        <button
          v-for="group in SCOPE_MEASUREMENT_GROUPS"
          :key="group.value"
          type="button"
          role="tab"
          :aria-selected="tab === group.value"
          class="inline-flex h-[34px] items-center gap-1.5 rounded-full px-3 text-[13px] transition-colors"
          :class="
            tab === group.value
              ? 'bg-blue-500/10 font-semibold text-blue-700 dark:text-blue-300'
              : 'font-medium text-neutral-500 hover:text-neutral-900 dark:text-neutral-400 dark:hover:text-neutral-100'
          "
          @click="tab = group.value"
        >
          {{ group.label }}
          <span
            v-if="chosenIn(group.value) > 0"
            class="inline-block min-w-[18px] rounded-full px-1.5 text-[11px] font-semibold leading-[18px]"
            :style="{
              backgroundColor: 'var(--kind-blue-badge-bg)',
              color: 'var(--kind-blue-badge-fg)',
            }"
            >{{ chosenIn(group.value) }}</span
          >
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
      <div class="flex max-h-[34rem] flex-col gap-3.5 overflow-y-auto pr-1">
        <section v-for="entry in sections" :key="entry.section.id" class="flex flex-col gap-2">
          <PickerSectionHeading :section="entry.section" />
          <div class="grid grid-cols-2 gap-2 sm:grid-cols-3">
            <button
              v-for="def in entry.items"
              :key="def.id"
              type="button"
              class="flex h-[46px] items-center gap-2.5 rounded-[10px] border py-0 pl-1.5 pr-2.5 text-left text-[13.5px] transition-colors"
              :class="
                unavailable(def)
                  ? 'cursor-not-allowed border-neutral-200 opacity-40 dark:border-neutral-700'
                  : isOn(def)
                    ? 'border-blue-500 bg-blue-500/10 font-semibold text-blue-700 dark:border-blue-400 dark:text-blue-300'
                    : 'border-neutral-200 font-medium hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800'
              "
              :title="unavailable(def) ? `${def.title} — channels only, not the math trace.` : def.title"
              :aria-pressed="isOn(def)"
              :disabled="unavailable(def)"
              @click="toggle(def)"
            >
              <PickerFigureWell :tone="entry.section.tone" size="sm">
                <ScopeMeasurementIcon
                  :name="def.icon"
                  size="lg"
                  :mark="sectionMarkColor(entry.section.mark)"
                />
              </PickerFigureWell>
              <span class="min-w-0 flex-1 truncate">{{ def.label }}</span>
              <!-- Inline rather than hung off the corner, unlike the two single-choice dialogs: a
                   dozen tiles may be ticked here at once, and a dozen badges overhanging the gaps
                   between them would be noise. -->
              <PickerTileCheck v-if="isOn(def)" placement="inline" />
            </button>
          </div>
        </section>
      </div>

      <div
        class="flex items-start gap-2.5 rounded-[10px] border border-neutral-200 p-3 text-[12px] leading-relaxed text-neutral-600 dark:border-neutral-800 dark:text-neutral-400"
        :style="{ backgroundColor: 'var(--surface-control)' }"
      >
        <svg
          width="16"
          height="16"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          class="mt-px shrink-0 text-blue-600 dark:text-blue-400"
          aria-hidden="true"
        >
          <circle cx="12" cy="12" r="9" />
          <path d="M12 11v5M12 8h.01" />
        </svg>
        <span>
          Ticks apply to the
          <strong class="font-semibold text-neutral-900 dark:text-neutral-100">Source</strong> above.
          To watch one measurement on several traces: tick it, switch source, tick it again. Readings
          chosen before per-trace selection existed are shown on all traces.
        </span>
      </div>
    </div>

    <template #footer>
      <button
        type="button"
        class="btn-secondary inline-flex items-center gap-2"
        :disabled="staged.size === 0"
        @click="clearAll"
      >
        <svg
          width="15"
          height="15"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          class="text-red-600 dark:text-red-400"
          aria-hidden="true"
        >
          <path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3" />
        </svg>
        Clear all
      </button>
      <span class="flex-1 pr-1 text-right text-xs text-neutral-500 dark:text-neutral-400">{{ summary }}</span>
      <button type="button" class="btn-secondary" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" @click="onApply">Apply</button>
    </template>
  </Modal>
</template>
