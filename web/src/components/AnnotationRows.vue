<script setup lang="ts">
/**
 * The editable list of a chart's annotations: one row each, numbered the way the chart numbers them.
 *
 * <p>Its own component because two places render exactly this list — the FFT display rail's
 * Annotations panel, which also arms the chart for a click, and the Edit measurement dialog, where a
 * *saved result*'s own annotations are corrected after the fact (there is no way to put a stored
 * result back on the chart, so that list is the only way to reach them). Keeping it one component is
 * what stops the two drifting into two different ideas of what a row is.</p>
 *
 * <p>Levels are in <b>dBV</b>, which is what is stored, whatever unit the chart's axis happens to be
 * in — see annotationReading.</p>
 */
import {
  annotationColor,
  numberedAnnotations,
  removeAnnotation,
  updateAnnotation,
  type SpectrumAnnotation,
} from '../measurements/spectrumAnnotations'

const props = defineProps<{
  modelValue: SpectrumAnnotation[]
  /** Dims the controls while the list cannot be written (a result whose params could not be read). */
  disabled?: boolean
}>()

const emit = defineEmits<{ 'update:modelValue': [value: SpectrumAnnotation[]] }>()

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

/** A retyped number is ignored unless it is usable: the rail's own leniency, and a log axis has no place for a zero. */
function onFreq(id: number, current: number, event: Event) {
  const value = Number.parseFloat((event.target as HTMLInputElement).value)
  if (!Number.isFinite(value) || value <= 0) return restore(event, current, 2)
  emit('update:modelValue', updateAnnotation(props.modelValue, id, { freqHz: value }))
}

function onLevel(id: number, current: number | null, event: Event) {
  const value = Number.parseFloat((event.target as HTMLInputElement).value)
  if (!Number.isFinite(value)) return restore(event, current, 1)
  emit('update:modelValue', updateAnnotation(props.modelValue, id, { levelDbv: value }))
}

/**
 * What the two number boxes show. The stored figures come off a peak search, so they carry every
 * digit the bin had -- 1998.04687 Hz, -45.999938 dBV -- which is unreadable in a narrow box and
 * says nothing a bench cares about. Rounded only for display: retyping writes exactly what was
 * typed, and leaving a box alone leaves the stored value untouched.
 */
function shown(value: number | null, decimals: number): string {
  return value === null || !Number.isFinite(value) ? '' : value.toFixed(decimals)
}

/**
 * A box's other two edges. An inverting retype is <b>ignored</b> rather than normalised: the
 * mutators would happily sort it out, but swapping the two boxes under the user's cursor as they
 * type is worse than doing nothing. Same leniency as the frequency box above.
 */
function onFreqTo(id: number, from: number, current: number | null, event: Event) {
  const value = Number.parseFloat((event.target as HTMLInputElement).value)
  if (!Number.isFinite(value) || value <= from) return restore(event, current, 2)
  emit('update:modelValue', updateAnnotation(props.modelValue, id, { freqHz2: value }))
}

function onLevelBottom(id: number, top: number | null, current: number | null, event: Event) {
  const value = Number.parseFloat((event.target as HTMLInputElement).value)
  if (!Number.isFinite(value) || (top !== null && value >= top)) return restore(event, current, 1)
  emit('update:modelValue', updateAnnotation(props.modelValue, id, { levelDbv2: value }))
}

function onRemove(id: number) {
  emit('update:modelValue', removeAnnotation(props.modelValue, id))
}
</script>

<template>
  <div v-if="numberedAnnotations(props.modelValue).length > 0" class="flex flex-col gap-2">
    <div
      v-for="annotation in numberedAnnotations(props.modelValue)"
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
          :title="
            annotation.style === 'point'
              ? 'A labelled point of the trace'
              : annotation.style === 'box'
                ? 'A marked region of the plot'
                : 'A rule at this frequency'
          "
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
           Top/Bottom. "Frequency" alone would be wrong once there are two of them. -->
      <div class="grid grid-cols-2 gap-2.5 pl-7">
        <label class="tb-field">
          {{ annotation.style === 'box' ? 'From (Hz)' : 'Frequency (Hz)' }}
          <input
            :value="shown(annotation.freqHz, 2)"
            type="number"
            step="1"
            min="0"
            class="tb-input h-8 text-xs"
            :disabled="props.disabled"
            @change="onFreq(annotation.id, annotation.freqHz, $event)"
          />
        </label>
        <label v-if="annotation.style === 'box'" class="tb-field">
          To (Hz)
          <input
            :value="shown(annotation.freqHz2, 2)"
            type="number"
            step="1"
            min="0"
            class="tb-input h-8 text-xs"
            :disabled="props.disabled"
            @change="onFreqTo(annotation.id, annotation.freqHz, annotation.freqHz2, $event)"
          />
        </label>
        <label v-if="annotation.style !== 'frequency'" class="tb-field">
          <span>
            {{ annotation.style === 'box' ? 'Top (dBV)' : 'Level (dBV)' }}
            <span
              v-if="annotation.channel !== null"
              class="font-normal text-neutral-400 dark:text-neutral-500"
              :title="`Read off the ${annotation.channel === 0 ? 'left' : 'right'} channel.`"
            >
              · {{ annotation.channel === 0 ? 'L' : 'R' }}
            </span>
          </span>
          <input
            :value="shown(annotation.levelDbv, 1)"
            type="number"
            step="1"
            class="tb-input h-8 text-xs"
            :disabled="props.disabled"
            @change="onLevel(annotation.id, annotation.levelDbv, $event)"
          />
        </label>
        <label v-if="annotation.style === 'box'" class="tb-field">
          Bottom (dBV)
          <input
            :value="shown(annotation.levelDbv2, 1)"
            type="number"
            step="1"
            class="tb-input h-8 text-xs"
            :disabled="props.disabled"
            @change="onLevelBottom(annotation.id, annotation.levelDbv, annotation.levelDbv2, $event)"
          />
        </label>
      </div>
    </div>
  </div>
</template>
