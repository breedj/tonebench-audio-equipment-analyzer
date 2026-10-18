<script setup lang="ts">
/**
 * The display rail's Annotations section: the style picker, Add/Cancel, the editable list of this
 * chart's notes, and Remove all.
 *
 * <p>One component for every measurement screen. It reads the shared {@link useAnnotations}
 * singleton rather than taking the list as a prop, which is what lets a display panel mount it with
 * nothing but the view it is for — exactly one measurement is on screen at a time, so one store is
 * the truth for all of them.</p>
 *
 * <p>It shows and clears <b>this view's</b> rows only. A measurement keeps one list across its
 * charts, and a user looking at a Nyquist plot should not be editing, or accidentally removing, a
 * note they put on the Bode plot; the count of the rest is said in a line instead.</p>
 *
 * <p><b>And it shows whichever set is on the charts</b>, which need not be the open measurement's —
 * a lit icon on a History row puts that saved result's own notes here instead, and then the rows
 * edited here are that result's and are written back to it. The caption says which, because the one
 * thing a user must not be able to do by accident is annotate a stored result while believing they
 * are annotating the measurement.</p>
 */
import { computed, ref } from 'vue'
import {
  DEFAULT_ANNOTATION_STYLES,
  MAX_ANNOTATIONS,
  annotationStyleOptions,
  annotationsElsewhere,
  numberedAnnotations,
  removeAnnotationsOfView,
  type AnnotationStyle,
} from '../measurements/chartAnnotations'
import { useAnnotations } from '../composables/useAnnotations'
import AnnotationRows from './AnnotationRows.vue'
import AnnotationToggle from './AnnotationToggle.vue'
import CollapsibleSection from './CollapsibleSection.vue'
import SegmentedToggle from './SegmentedToggle.vue'

const props = withDefaults(
  defineProps<{
    /** Which chart this panel is for — see ChartAnnotation.view. */
    view: string
    /** Which styles this chart offers. The rule style is for the charts whose y is unstable. */
    styles?: readonly AnnotationStyle[]
    /** What a click on this chart does, for the hint line. */
    pointHint?: string
    /**
     * The section heading. Defaults to "Annotations", which is right where a rail carries one of
     * these; the FFT rail carries three, one per chart it stacks, and they have to be told apart.
     */
    title?: string
  }>(),
  { styles: () => DEFAULT_ANNOTATION_STYLES, pointHint: 'Click the chart to place it.', title: 'Annotations' },
)

const { shown, live, source, sourceName, error, armed, arm, show } = useAnnotations()

const options = computed(() => annotationStyleOptions(props.styles))
const style = ref<AnnotationStyle>(props.styles[0] ?? 'point')

const mine = computed(() => numberedAnnotations(shown.value, props.view).length)
const elsewhere = computed(() => annotationsElsewhere(shown.value, props.view))
const full = computed(() => shown.value.length >= MAX_ANNOTATIONS)

/** Whether this measurement's own notes are the ones on the charts — the toggle's lit state. */
const liveShown = computed(() => source.value === 'live')
/** The name of the stored result whose notes are here instead, or null. */
const resultShown = computed(() => (typeof source.value === 'number' ? sourceName.value : null))

function onAdd() {
  // arm() lights this measurement's own notes when nothing is lit, so a press of Add can never
  // place a note onto a list the chart is not drawing -- see useAnnotations.arm.
  arm(armed.value === null ? style.value : null)
}

function removeAll() {
  shown.value = removeAnnotationsOfView(shown.value, props.view)
  arm(null)
}
</script>

<template>
  <!-- The notes drawn on the chart. Its own section rather than part of Display because it is about
       what the chart carries rather than how it is drawn, and because it is reached while reading
       the chart. The icon and tone are passed explicitly, which is what a new panel does -- the
       title table in panels/panelIcons.ts exists for the panels that predate it. -->
  <CollapsibleSection :title="props.title" size="xs" icon="notes" tone="violet" :default-open="false">
    <template #header-right>
      <!-- This measurement's own member of the group, so the rail can show and hide its notes from
           the panel that lists them. One per Annotations panel, which on the FFT rail means up to
           three showing the same state: the alternative is a single toggle two sections away from
           the list it governs. -->
      <AnnotationToggle
        :active="liveShown"
        :empty="live.length === 0"
        :title="
          liveShown
            ? 'This measurement\'s annotations are on the charts. Click to hide them.'
            : 'Show this measurement\'s own annotations on the charts.'
        "
        @click="show('live')"
      />
    </template>

    <!-- Which set is being edited, where that is not the obvious one. Said before the controls
         rather than after the list, because it changes what pressing Add means. -->
    <p v-if="resultShown !== null" class="text-[11px] leading-snug text-violet-700 dark:text-violet-300">
      The saved result <strong>{{ resultShown }}</strong
      >. Anything changed here is saved to that result, not to this measurement.
    </p>
    <p v-else-if="source === null" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      No annotations are shown. The icon above puts this measurement's own back on the charts.
    </p>

    <p v-if="error !== null" class="text-[11px] leading-snug text-red-600 dark:text-red-400">{{ error }}</p>

    <!-- The style picker takes whatever the button leaves, rather than the two splitting the row
         evenly: half a rail is not enough for a two-word label and the word was clipped (Joost,
         2026-10-17). The button is a fixed width rather than sized to its own label, or the picker
         beside it would jump sideways every time Add became Cancel. -->
    <div v-if="options.length > 1" class="grid grid-cols-[minmax(0,1fr)_auto] gap-2.5">
      <SegmentedToggle v-model="style" :options="options" group-label="Annotation style" />
      <button
        type="button"
        class="btn-panel-accent w-[3.75rem] px-0"
        :disabled="full && armed === null"
        :title="
          full && armed === null
            ? `${MAX_ANNOTATIONS} is the most one measurement carries.`
            : 'Arms the chart: the next click on it places an annotation.'
        "
        @click="onAdd"
      >
        {{ armed === null ? 'Add' : 'Cancel' }}
      </button>
    </div>
    <!-- One style offered: the picker would be a row of one, so the button takes the width. -->
    <button
      v-else
      type="button"
      class="btn-panel-accent w-full"
      :disabled="full && armed === null"
      :title="
        full && armed === null
          ? `${MAX_ANNOTATIONS} is the most one measurement carries.`
          : 'Arms the chart: the next click on it places an annotation.'
      "
      @click="onAdd"
    >
      {{ armed === null ? 'Add annotation' : 'Cancel' }}
    </button>

    <p v-if="armed === 'box'" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      Drag a rectangle on the chart to mark a region. Dragging zooms again once this is cancelled.
    </p>
    <p v-else-if="armed !== null" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      {{ props.pointHint }}
    </p>
    <p v-else-if="mine === 0 && source !== null" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      No annotations on this chart. Add one, then click it. They are drawn on screenshots and in the
      report, and a saved result keeps the ones it was saved with.
    </p>

    <AnnotationRows v-model="shown" :view="props.view" />

    <!-- The other charts' notes are neither shown nor removable here, so say they exist rather
         than leaving the count in the panel disagreeing with what a report prints. Deliberately not
         "this measurement's", since the set on screen may be a stored result's. -->
    <p v-if="elsewhere > 0" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      {{ elsewhere }} more on the other charts.
    </p>

    <button
      v-if="mine > 0"
      type="button"
      class="btn-secondary h-8 w-full text-xs"
      title="Removes every annotation from this chart."
      @click="removeAll"
    >
      Remove all
    </button>
  </CollapsibleSection>
</template>
