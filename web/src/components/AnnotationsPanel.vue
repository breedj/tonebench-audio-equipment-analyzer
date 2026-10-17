<script setup lang="ts">
/**
 * The display rail's Annotations section: the style picker, Add/Cancel, the editable list of this
 * chart's notes, and Remove all.
 *
 * <p>One component for every measurement screen. It reads the shared {@link useAnnotations}
 * singleton rather than taking the list as a prop, which is what lets a display panel mount it with
 * nothing but the view it is for — exactly one measurement is on screen at a time, so one list is
 * the truth for all of them.</p>
 *
 * <p>It shows and clears <b>this view's</b> rows only. A measurement keeps one list across its
 * charts, and a user looking at a Nyquist plot should not be editing, or accidentally removing, a
 * note they put on the Bode plot; the count of the rest is said in a line instead.</p>
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

const { annotations, armed, arm } = useAnnotations()

const options = computed(() => annotationStyleOptions(props.styles))
const style = ref<AnnotationStyle>(props.styles[0] ?? 'point')

const mine = computed(() => numberedAnnotations(annotations.value, props.view).length)
const elsewhere = computed(() => annotationsElsewhere(annotations.value, props.view))
const full = computed(() => annotations.value.length >= MAX_ANNOTATIONS)

function onAdd() {
  arm(armed.value === null ? style.value : null)
}

function removeAll() {
  annotations.value = removeAnnotationsOfView(annotations.value, props.view)
  arm(null)
}
</script>

<template>
  <!-- The notes drawn on the chart. Its own section rather than part of Display because it is about
       what the chart carries rather than how it is drawn, and because it is reached while reading
       the chart. The icon and tone are passed explicitly, which is what a new panel does -- the
       title table in panels/panelIcons.ts exists for the panels that predate it. -->
  <CollapsibleSection :title="props.title" size="xs" icon="notes" tone="violet" :default-open="false">
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
    <p v-else-if="mine === 0" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      No annotations on this chart. Add one, then click it. They are drawn on screenshots and in the
      report, and a saved result keeps the ones it was saved with.
    </p>

    <AnnotationRows v-model="annotations" :view="props.view" />

    <!-- A measurement's other charts' notes are neither shown nor removable here, so say they exist
         rather than leaving the count in the panel disagreeing with what a report prints. -->
    <p v-if="elsewhere > 0" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      {{ elsewhere }} more on this measurement's other charts.
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
