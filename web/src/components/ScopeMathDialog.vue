<script setup lang="ts">
/**
 * What one math channel computes, and on which two inputs.
 *
 * <p><b>Seven tiles rather than eleven dropdown entries</b>, and that is what the Source A and
 * Source B controls buy: A − B and B − A are one operation with the operands swapped, and so are
 * −A and −B, dA/dt and dB/dt, ∫A and ∫B. Offering both of each made sense while A was always the
 * left input, because there was no other way to say it; with the sources chosen there is, and two
 * tiles differing only in which socket they name are two chances to pick the wrong one.</p>
 *
 * <p>The same shape as <code>ScopeMeasurementPickerDialog</code> — a tile grid with a figure and a
 * sentence each, staged and committed on Apply so Cancel really discards.</p>
 */
import { computed, ref, watch } from 'vue'
import Modal from './Modal.vue'
import ScopeToggleIcon from './ScopeToggleIcon.vue'
import SegmentedToggle from './SegmentedToggle.vue'
import {
  SCOPE_MATH_SOURCES,
  SCOPE_MATH_SOURCE_LABELS,
  SCOPE_MATH_TILES,
  DEFAULT_SCOPE_MATH_SPEC,
  mathSpecLabel,
  mathTileFor,
  type ScopeMathSlot,
  type ScopeMathSource,
  type ScopeMathSpec,
} from '../scope/math'

const props = defineProps<{
  open: boolean
  slot: ScopeMathSlot
  spec: ScopeMathSpec
  /** Which inputs this measurement is capturing, for the note under the sources. */
  capturedLeft: boolean
  capturedRight: boolean
}>()

const emit = defineEmits<{ close: []; apply: [spec: ScopeMathSpec] }>()

const operation = ref<ScopeMathSpec['operation']>(props.spec.operation)
const sourceA = ref<ScopeMathSource>(props.spec.sourceA)
const sourceB = ref<ScopeMathSource>(props.spec.sourceB)

// Re-seeded on every open, never watched continuously: a dialog that opened holding what was
// staged last time reads as having lost its place.
watch(
  () => props.open,
  (open) => {
    if (!open) return
    operation.value = props.spec.operation
    sourceA.value = props.spec.sourceA
    sourceB.value = props.spec.sourceB
  },
)

const chosen = computed(() => mathTileFor(operation.value)?.tile ?? null)

/** Whether the second operand means anything — the three one-operand tiles say no. */
const usesB = computed(() => chosen.value?.usesB ?? false)

const staged = computed<ScopeMathSpec>(() => ({
  operation: operation.value,
  sourceA: sourceA.value,
  sourceB: sourceB.value,
}))

const sourceOptions = computed(() =>
  SCOPE_MATH_SOURCES.map((source) => ({
    value: source,
    label: SCOPE_MATH_SOURCE_LABELS[source],
    // Never disabled for being absent from the capture: a math channel outlives one run, and the
    // note below says it instead. The picker dialog's own rule.
    title: source === 'ch0' ? 'The left input.' : 'The right input.',
  })),
)

/** Which chosen sources this measurement is not capturing, by their own labels. */
const absent = computed(() => {
  if (!chosen.value) return []
  const has = (source: ScopeMathSource) => (source === 'ch1' ? props.capturedRight : props.capturedLeft)
  const names = new Set<string>()
  if (!has(sourceA.value)) names.add(SCOPE_MATH_SOURCE_LABELS[sourceA.value])
  if (usesB.value && !has(sourceB.value)) names.add(SCOPE_MATH_SOURCE_LABELS[sourceB.value])
  return [...names]
})

function turnOff() {
  emit('apply', { ...DEFAULT_SCOPE_MATH_SPEC })
  emit('close')
}

function apply() {
  emit('apply', staged.value)
  emit('close')
}
</script>

<template>
  <Modal
    :open="props.open"
    :title="`Math ${props.slot}`"
    icon="formula"
    tone="violet"
    size="lg"
    @close="emit('close')"
  >
    <div class="flex flex-col gap-3 text-sm">
      <div class="grid grid-cols-2 gap-2.5 sm:grid-cols-3">
        <button
          v-for="tile in SCOPE_MATH_TILES"
          :key="tile.operation"
          type="button"
          class="flex items-center gap-3 rounded-lg border px-3 py-2.5 text-left text-[15px] transition-colors"
          :class="
            operation === tile.operation
              ? 'border-blue-500 bg-blue-500/10 text-blue-700 dark:border-blue-400 dark:text-blue-300'
              : 'border-neutral-200 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800'
          "
          :title="tile.title"
          :aria-pressed="operation === tile.operation"
          @click="operation = tile.operation"
        >
          <ScopeToggleIcon :name="tile.icon" :size="20" />
          <span class="min-w-0 truncate">{{ tile.label }}</span>
        </button>
      </div>

      <div class="grid grid-cols-2 gap-3">
        <div class="tb-field">
          <span class="tb-group">Source A</span>
          <SegmentedToggle v-model="sourceA" :options="sourceOptions" group-label="Source A" />
        </div>
        <div class="tb-field">
          <span class="tb-group">Source B</span>
          <SegmentedToggle
            v-model="sourceB"
            :options="sourceOptions"
            group-label="Source B"
            :class="usesB ? '' : 'opacity-40'"
          />
        </div>
      </div>

      <p v-if="chosen && !usesB" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        <strong>{{ chosen.label }}</strong> reads one input, so Source B does nothing here.
      </p>
      <p v-if="absent.length" class="text-[11px] leading-snug text-amber-600 dark:text-amber-400">
        Nothing to draw yet: this needs the {{ absent.join(' and the ') }} input, which this
        measurement is not capturing. Tick it under Acquisition ▸ Channels. The setting is kept
        either way.
      </p>
      <p v-else-if="chosen" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        Draws <strong>{{ mathSpecLabel(staged) }}</strong
        >, computed on the analyzer over the samples of the window on screen — never on the drawn
        columns, where the difference of two envelopes is not the envelope of a difference.
      </p>
    </div>

    <template #footer>
      <button type="button" class="btn-secondary" @click="turnOff">Turn off</button>
      <button type="button" class="btn-secondary" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" :disabled="!chosen" @click="apply">Apply</button>
    </template>
  </Modal>
</template>
