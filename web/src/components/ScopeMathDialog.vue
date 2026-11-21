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
 * <p><b>Two sections</b> (Joost's design, 2026-11-20): the four that combine two traces, and the
 * three that read one. The split is the whole explanation of why Source B sometimes does nothing —
 * said once in a heading instead of under every tile — and it is derived from the tile's own
 * <code>usesB</code>, so a tile cannot be filed in the wrong half.</p>
 *
 * <p>The same shape as <code>ScopeMeasurementPickerDialog</code> — tiles in headed sections, staged
 * and committed on Apply so Cancel really discards.</p>
 */
import { computed, ref, watch } from 'vue'
import Modal from './Modal.vue'
import PickerFigureWell from './PickerFigureWell.vue'
import PickerSectionHeading from './PickerSectionHeading.vue'
import PickerTileCheck from './PickerTileCheck.vue'
import ScopeMathFigure from './ScopeMathFigure.vue'
import SegmentedToggle from './SegmentedToggle.vue'
import {
  SCOPE_MATH_SOURCES,
  SCOPE_MATH_SOURCE_LABELS,
  DEFAULT_SCOPE_MATH_SPEC,
  mathSpecLabel,
  mathTileFor,
  scopeMathSections,
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

const sections = scopeMathSections()

const chosen = computed(() => mathTileFor(operation.value)?.tile ?? null)

/** Whether the second operand means anything — the three one-operand tiles say no. */
const usesB = computed(() => chosen.value?.usesB ?? false)

const staged = computed<ScopeMathSpec>(() => ({
  operation: operation.value,
  sourceA: sourceA.value,
  sourceB: sourceB.value,
}))

/**
 * The two slots' trace colours, repeated from the chart for the figures and the result pill.
 *
 * <p>A dialog reaching into `OscilloscopeScreenChart` for them would be the wrong way round; what
 * matters is that the picture of a math channel is drawn in the ink that channel will appear in.</p>
 */
const MATH_INK = ['#a855f7', '#ec4899'] as const
const CHANNEL_INK = { ch0: '#3b82f6', ch1: '#f97316' } as const

const resultColor = computed(() => MATH_INK[props.slot - 1] ?? MATH_INK[0])

const sourceOptions = computed(() =>
  SCOPE_MATH_SOURCES.map((source) => ({
    value: source,
    label: SCOPE_MATH_SOURCE_LABELS[source],
    dot: CHANNEL_INK[source],
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

/**
 * The expression the footer writes, in pieces, so each operand takes its own trace's ink.
 *
 * <p>Split out of the <b>tile's</b> own label rather than out of the finished string, which is the
 * same reason <code>mathSpecLabel</code> substitutes rather than matching: the A in
 * <code>dA/dt</code> sits against a word character, so there is no boundary to find it by, and a
 * search for "L" in the finished label would colour the one in <code>dL/dt</code> and also the
 * first letter of anything else that happened to be an L.</p>
 */
const resultParts = computed(() => {
  const tile = chosen.value
  if (!tile) return [{ text: mathSpecLabel(staged.value), color: null as string | null }]
  return tile.label
    .split(/([AB])/)
    .filter((piece) => piece.length > 0)
    .map((piece) => {
      if (piece === 'A') {
        return { text: SCOPE_MATH_SOURCE_LABELS[sourceA.value], color: CHANNEL_INK[sourceA.value] }
      }
      if (piece === 'B') {
        return { text: SCOPE_MATH_SOURCE_LABELS[sourceB.value], color: CHANNEL_INK[sourceB.value] }
      }
      return { text: piece, color: null as string | null }
    })
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
    <div class="flex flex-col gap-3.5 text-sm">
      <section v-for="entry in sections" :key="entry.section.id" class="flex flex-col gap-2">
        <PickerSectionHeading :section="entry.section" />
        <!-- Four across where the room is there, which is what puts the whole binary group on one
             row: these labels are three characters of mono and a figure. -->
        <div class="grid grid-cols-2 gap-2 sm:grid-cols-4">
          <button
            v-for="tile in entry.items"
            :key="tile.operation"
            type="button"
            class="relative flex h-[52px] items-center gap-2.5 rounded-[10px] border py-0 pl-1.5 pr-2.5 text-left transition-colors"
            :class="
              operation === tile.operation
                ? 'border-blue-500 bg-blue-500/10 ring-[3px] ring-blue-500/20 dark:border-blue-400'
                : 'border-neutral-200 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800'
            "
            :title="tile.title"
            :aria-pressed="operation === tile.operation"
            @click="operation = tile.operation"
          >
            <PickerFigureWell :tone="entry.section.tone">
              <ScopeMathFigure
                :operation="tile.operation"
                :source-a="sourceA"
                :source-b="sourceB"
                :result-color="resultColor"
              />
            </PickerFigureWell>
            <span
              class="min-w-0 truncate font-mono text-sm"
              :class="
                operation === tile.operation
                  ? 'font-semibold text-blue-700 dark:text-blue-300'
                  : 'font-medium'
              "
              >{{ tile.label }}</span
            >
            <PickerTileCheck v-if="operation === tile.operation" />
          </button>
        </div>
      </section>

      <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <div v-for="which in ['A', 'B'] as const" :key="which" class="tb-field" :class="which === 'B' && !usesB ? 'opacity-45' : ''">
          <span class="flex items-center gap-2 text-xs font-semibold text-neutral-600 dark:text-neutral-300">
            <span
              class="flex size-5 items-center justify-center rounded-md font-mono text-xs font-semibold"
              :style="{
                backgroundColor: `var(--kind-${which === 'A' ? 'blue' : 'orange'}-chip-bg)`,
                color: `var(--kind-${which === 'A' ? 'blue' : 'orange'}-chip-fg)`,
              }"
              aria-hidden="true"
              >{{ which }}</span
            >
            Source {{ which }}
            <span
              v-if="which === 'B' && chosen && !usesB"
              class="font-normal text-neutral-500 dark:text-neutral-400"
            >
              — not used by this operation
            </span>
          </span>
          <SegmentedToggle
            v-if="which === 'A'"
            v-model="sourceA"
            :options="sourceOptions"
            group-label="Source A"
          />
          <SegmentedToggle v-else v-model="sourceB" :options="sourceOptions" group-label="Source B" />
        </div>
      </div>

      <p v-if="absent.length" class="text-[11px] leading-snug text-amber-600 dark:text-amber-400">
        Nothing to draw yet: this needs the {{ absent.join(' and the ') }} input, which this
        measurement is not capturing. Tick it under Acquisition ▸ Channels. The setting is kept
        either way.
      </p>
      <p v-else-if="chosen" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        Computed on the analyzer over the samples of the window on screen — never on the drawn
        columns, where the difference of two envelopes is not the envelope of a difference.
      </p>
    </div>

    <template #footer>
      <button type="button" class="btn-secondary inline-flex items-center gap-2" @click="turnOff">
        <svg
          width="15"
          height="15"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2.2"
          stroke-linecap="round"
          class="text-red-600 dark:text-red-400"
          aria-hidden="true"
        >
          <path d="M12 3v8" />
          <path d="M6.4 6.6a8 8 0 1 0 11.2 0" />
        </svg>
        Turn off
      </button>
      <!-- What the tile and the two sources add up to, written out: the one line that says what
           pressing Apply will draw, in the inks it will be drawn in. -->
      <span class="flex flex-1 items-center justify-end gap-2 pr-1 text-xs text-neutral-500 dark:text-neutral-400">
        Result
        <span
          class="inline-flex items-center gap-1 rounded-full border border-neutral-200 px-2.5 py-0.5 font-mono text-xs text-neutral-800 dark:border-neutral-700 dark:text-neutral-100"
          :style="{ backgroundColor: 'var(--surface-control)' }"
        >
          <span class="font-semibold" :style="{ color: resultColor }">Math {{ props.slot }}</span>
          <span>=</span>
          <span class="whitespace-pre"
            ><span
              v-for="(piece, i) in resultParts"
              :key="i"
              :class="piece.color ? 'font-semibold' : ''"
              :style="piece.color ? { color: piece.color } : undefined"
              >{{ piece.text }}</span
            ></span
          >
        </span>
      </span>
      <button type="button" class="btn-secondary" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" :disabled="!chosen" @click="apply">Apply</button>
    </template>
  </Modal>
</template>
