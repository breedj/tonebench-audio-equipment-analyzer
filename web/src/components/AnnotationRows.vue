<script setup lang="ts">
/**
 * The editable list of a chart's annotations: one row each, numbered the way the chart numbers them.
 *
 * <p>Its own component because two places render exactly this list — a display rail's Annotations
 * panel, which also arms the chart for a click, and the Edit measurement dialog, where a *saved
 * result*'s own annotations are corrected after the fact (there is no way to put a stored result
 * back on a chart, so that list is the only way to reach them). Keeping it one component is what
 * stops the two drifting into two different ideas of what a row is.</p>
 *
 * <p>Every figure is shown in the quantity it was <b>stored</b> in — hertz, milliseconds, watts,
 * ohms — whatever unit the chart's axis happens to be in, and the field labels come from that same
 * quantity. The rail shows one view's rows; the dialog groups the whole measurement's by view, since
 * a stored result may carry notes from five different charts.</p>
 */
import { computed } from 'vue'
import {
  annotationColor,
  annotationViewsPresent,
  numberedAnnotations,
  removeAnnotation,
  updateAnnotation,
  type ChartAnnotation,
} from '../measurements/chartAnnotations'
import { annotationQuantity } from '../charts/annotationQuantities'
import { annotationViewLabel } from '../charts/annotationViews'

const props = defineProps<{
  modelValue: ChartAnnotation[]
  /** One chart's rows. Omit to group every view's, which is what the Edit dialog does. */
  view?: string
  /** Dims the controls while the list cannot be written (a result whose params could not be read). */
  disabled?: boolean
}>()

const emit = defineEmits<{ 'update:modelValue': [value: ChartAnnotation[]] }>()

/** One group per view, or the single group the `view` prop names. */
const groups = computed(() => {
  const views = props.view === undefined ? annotationViewsPresent(props.modelValue) : [props.view]
  return views
    .map((view) => ({ view, label: annotationViewLabel(view), rows: numberedAnnotations(props.modelValue, view) }))
    .filter((group) => group.rows.length > 0)
})

/** Whether to print each group's name — only worth it when a reader can see more than one. */
const showGroupLabels = computed(() => props.view === undefined && groups.value.length > 1)

function onText(id: number, event: Event) {
  emit('update:modelValue', updateAnnotation(props.modelValue, id, { text: (event.target as HTMLInputElement).value }))
}

/**
 * Puts the stored figure back in the box after a retype was refused.
 *
 * <p>Needed because these inputs are bound with `:value` rather than `v-model`: when a change is
 * rejected the model does not move, so Vue never re-renders the input and the refused number simply
 * stays on screen, where it reads as having been accepted. The row would then be showing something
 * the chart is not drawing.</p>
 */
function restore(event: Event, value: number | null, decimals: number) {
  ;(event.target as HTMLInputElement).value = shown(value, decimals)
}

/**
 * A retyped number is ignored unless it is usable: the rail's own leniency, plus the stored
 * quantity's own rule — a ratio quantity (a frequency, an impedance, a percentage) has no place for
 * a zero, while a level, a time and a real part are all legitimately negative.
 */
function onX(a: ChartAnnotation, event: Event) {
  const q = annotationQuantity(a.xQuantity)
  const value = Number.parseFloat((event.target as HTMLInputElement).value)
  if (!Number.isFinite(value) || (q.positiveOnly && value <= 0)) return restore(event, a.x, q.decimals)
  emit('update:modelValue', updateAnnotation(props.modelValue, a.id, { x: value }))
}

function onY(a: ChartAnnotation, event: Event) {
  const q = annotationQuantity(a.yQuantity)
  const value = Number.parseFloat((event.target as HTMLInputElement).value)
  if (!Number.isFinite(value) || (q.positiveOnly && value <= 0)) return restore(event, a.y, q.decimals)
  emit('update:modelValue', updateAnnotation(props.modelValue, a.id, { y: value }))
}

/**
 * A box's other two edges. An inverting retype is <b>ignored</b> rather than normalised: the
 * mutators would happily sort it out, but swapping the two boxes under the user's cursor as they
 * type is worse than doing nothing. Same leniency as the boxes above.
 */
function onX2(a: ChartAnnotation, event: Event) {
  const q = annotationQuantity(a.xQuantity)
  const value = Number.parseFloat((event.target as HTMLInputElement).value)
  if (!Number.isFinite(value) || value <= a.x || (q.positiveOnly && value <= 0)) {
    return restore(event, a.x2, q.decimals)
  }
  emit('update:modelValue', updateAnnotation(props.modelValue, a.id, { x2: value }))
}

function onY2(a: ChartAnnotation, event: Event) {
  const q = annotationQuantity(a.yQuantity)
  const value = Number.parseFloat((event.target as HTMLInputElement).value)
  if (!Number.isFinite(value) || (a.y !== null && value >= a.y) || (q.positiveOnly && value <= 0)) {
    return restore(event, a.y2, q.decimals)
  }
  emit('update:modelValue', updateAnnotation(props.modelValue, a.id, { y2: value }))
}

/**
 * What the number boxes show. The stored figures come off a peak search, so they carry every digit
 * the bin had -- 1998.04687 Hz, -45.999938 dBV -- which is unreadable in a narrow box and says
 * nothing a bench cares about. Rounded only for display: retyping writes exactly what was typed, and
 * leaving a box alone leaves the stored value untouched.
 */
function shown(value: number | null, decimals: number): string {
  return value === null || !Number.isFinite(value) ? '' : value.toFixed(decimals)
}

/** A field's caption: the stored quantity's own name and unit, and which edge of a box it is. */
function caption(a: ChartAnnotation, field: 'x' | 'x2' | 'y' | 'y2'): string {
  const q = annotationQuantity(field === 'x' || field === 'x2' ? a.xQuantity : a.yQuantity)
  const unit = q.unit ? ` (${q.unit})` : ''
  if (a.style !== 'box') return `${q.label}${unit}`
  const edge = { x: 'From', x2: 'To', y: 'Top', y2: 'Bottom' }[field]
  return `${edge}${unit}`
}

function styleTitle(a: ChartAnnotation): string {
  if (a.style === 'point') return 'A labelled point of the trace'
  if (a.style === 'box') return 'A marked region of the plot'
  return 'A rule at this value'
}

function onRemove(id: number) {
  emit('update:modelValue', removeAnnotation(props.modelValue, id))
}
</script>

<template>
  <div v-if="groups.length > 0" class="flex flex-col gap-2">
    <div v-for="group in groups" :key="group.view" class="flex flex-col gap-2">
      <p
        v-if="showGroupLabels"
        class="text-[11px] font-medium uppercase tracking-wide text-neutral-500 dark:text-neutral-400"
      >
        {{ group.label }}
      </p>
      <div
        v-for="annotation in group.rows"
        :key="annotation.id"
        class="flex flex-col gap-1.5 border-t border-neutral-200 pt-2 first:border-t-0 first:pt-0 dark:border-neutral-800"
      >
        <div class="flex items-center gap-2">
          <!-- The ordinal in its own colour, the same one the chart draws on that label's badge, so a
               row and a label on the picture can be tied together without reading either number. -->
          <span
            class="inline-flex size-5 shrink-0 items-center justify-center rounded-full border text-[10px] font-medium tabular-nums"
            :style="{
              color: annotationColor(annotation.number),
              borderColor: annotationColor(annotation.number),
              backgroundColor: `${annotationColor(annotation.number)}26`,
            }"
            :title="styleTitle(annotation)"
          >
            {{ annotation.number }}
          </span>
          <input
            :value="annotation.text"
            type="text"
            placeholder="What is this?"
            class="tb-input h-8 min-w-0 flex-1 text-xs"
            :disabled="props.disabled"
            @change="onText(annotation.id, $event)"
          />
          <button
            type="button"
            class="shrink-0 rounded px-1.5 py-1 text-xs text-neutral-500 hover:text-red-600 dark:text-neutral-400 dark:hover:text-red-400"
            title="Remove this annotation."
            :disabled="props.disabled"
            @click="onRemove(annotation.id)"
          >
            ✕
          </button>
        </div>
        <!-- A box's four numbers flow into the same two columns as a point's two: From/To over
             Top/Bottom. The caption alone would be wrong once there are two of them. -->
        <div class="grid grid-cols-2 gap-2.5 pl-7">
          <label class="tb-field">
            {{ caption(annotation, 'x') }}
            <input
              :value="shown(annotation.x, annotationQuantity(annotation.xQuantity).decimals)"
              type="number"
              step="any"
              class="tb-input h-8 text-xs"
              :disabled="props.disabled"
              @change="onX(annotation, $event)"
            />
          </label>
          <label v-if="annotation.style === 'box'" class="tb-field">
            {{ caption(annotation, 'x2') }}
            <input
              :value="shown(annotation.x2, annotationQuantity(annotation.xQuantity).decimals)"
              type="number"
              step="any"
              class="tb-input h-8 text-xs"
              :disabled="props.disabled"
              @change="onX2(annotation, $event)"
            />
          </label>
          <label v-if="annotation.style !== 'frequency'" class="tb-field">
            <span>
              {{ caption(annotation, 'y') }}
              <span
                v-if="annotation.channel !== null"
                class="font-normal text-neutral-400 dark:text-neutral-500"
                :title="`Read off the ${annotation.channel === 0 ? 'left' : 'right'} channel.`"
              >
                · {{ annotation.channel === 0 ? 'L' : 'R' }}
              </span>
            </span>
            <input
              :value="shown(annotation.y, annotationQuantity(annotation.yQuantity).decimals)"
              type="number"
              step="any"
              class="tb-input h-8 text-xs"
              :disabled="props.disabled"
              @change="onY(annotation, $event)"
            />
          </label>
          <label v-if="annotation.style === 'box'" class="tb-field">
            {{ caption(annotation, 'y2') }}
            <input
              :value="shown(annotation.y2, annotationQuantity(annotation.yQuantity).decimals)"
              type="number"
              step="any"
              class="tb-input h-8 text-xs"
              :disabled="props.disabled"
              @change="onY2(annotation, $event)"
            />
          </label>
        </div>
      </div>
    </div>
  </div>
</template>
