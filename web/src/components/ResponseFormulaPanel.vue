<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { ComplexPoint } from '../response/complexPlane'
import { fitRational, suggestOrder, POOR_FIT_DB, type FitResult } from '../response/rationalFit'
import { measuredBand } from '../response/poleZeroMap'
import {
  formatTransferFunction,
  renderTransferFunction,
  TRANSFER_FUNCTION_FORMATS,
  type TransferFunctionFormat,
} from '../response/transferFunctionText'
import CollapsibleSection from './CollapsibleSection.vue'

/**
 * The fitted model written out as H(s), with the four exports worth copying.
 *
 * A sibling of `ResponsePoleZeroPanel` rather than a section inside it: that panel owns the order
 * controls, the root table and four warnings already, and this is a rendering-and-export job that
 * wants its own collapse state. It takes the same `curve` and applies the same order, so the two can
 * never describe different models — the alternative, passing the panel's `fit` down, would have made
 * App.vue mediate a second chart-to-panel-to-panel loop for nothing.
 *
 * All of the arithmetic and every string is in `transferFunction.ts` / `transferFunctionText.ts`,
 * where it is tested; nothing in a `.vue` file is.
 */
const props = defineProps<{
  curve: ComplexPoint[]
  /** The pole/zero panel's pinned orders, so both panels describe one model. */
  order: number
  zeroOrder: number
  autoOrder: boolean
}>()

const band = computed(() => measuredBand(props.curve))

const suggestion = computed(() => (props.curve.length >= 6 ? suggestOrder(props.curve, 8).order : null))
const activeOrder = computed(() =>
  props.autoOrder ? (suggestion.value ?? 2) : Math.max(1, Math.min(10, props.order)),
)
const activeZeroOrder = computed(() =>
  props.autoOrder ? activeOrder.value : Math.max(0, Math.min(activeOrder.value, props.zeroOrder)),
)

const fit = computed<FitResult | null>(() =>
  props.curve.length >= 6 ? fitRational(props.curve, activeZeroOrder.value, activeOrder.value) : null,
)

const rendered = computed(() => (fit.value ? renderTransferFunction(fit.value, band.value) : null))

const format = ref<TransferFunctionFormat>('Text')
const text = computed(() => (fit.value ? formatTransferFunction(fit.value, band.value, format.value) : ''))

const poorFit = computed(() => fit.value !== null && fit.value.rmsErrorDb > POOR_FIT_DB)

const copyState = ref<'idle' | 'copied' | 'manual'>('idle')
const textarea = ref<HTMLTextAreaElement | null>(null)

// A new format or a new model makes any "Copied" stale.
watch([format, fit], () => {
  copyState.value = 'idle'
})

/**
 * Copy, with a fallback that is not an apology.
 *
 * The in-app Browser pane refuses clipboard writes, and a secure context plus a focused document are
 * required elsewhere, so this cannot be assumed to work. On refusal the text is selected instead and
 * the button says so — never a success that did not happen.
 */
async function copy() {
  try {
    if (!navigator.clipboard?.writeText) throw new Error('no clipboard')
    await navigator.clipboard.writeText(text.value)
    copyState.value = 'copied'
  } catch {
    copyState.value = 'manual'
    textarea.value?.focus()
    textarea.value?.select()
  }
}
</script>

<template>
  <CollapsibleSection title="Transfer function">
    <div v-if="!rendered" class="text-[11px] text-neutral-500 dark:text-neutral-400">
      Not enough measured points in the plotted window to fit a model.
    </div>

    <template v-else>
      <!-- The formula itself. Rendered as spans rather than an image so it selects and scales. -->
      <div class="overflow-x-auto rounded border border-neutral-200 bg-[var(--surface-control)] p-2 font-mono text-[11px] leading-5 dark:border-neutral-800 dark:bg-neutral-900">
        <div class="flex items-center gap-1.5 whitespace-nowrap">
          <span class="tb-group">H(s) =</span>
          <span class="text-neutral-900 dark:text-neutral-100">{{ rendered.gain }}</span>
          <span v-if="rendered.numerator.length || rendered.denominator.length">·</span>
          <span class="inline-flex flex-col items-center">
            <span class="px-1">
              <template v-if="rendered.numerator.length === 0">1</template>
              <template v-for="(factor, fi) in rendered.numerator" :key="`n${fi}`">
                <span v-if="fi > 0" class="px-0.5">·</span>
                <template v-for="(part, pi) in factor.parts" :key="`n${fi}-${pi}`">
                  <sup v-if="part.sup">{{ part.text }}</sup>
                  <template v-else>{{ part.text }}</template>
                </template>
              </template>
            </span>
            <span
              v-if="rendered.denominator.length"
              class="w-full border-t border-neutral-400 px-1 dark:border-neutral-600"
            >
              <template v-for="(factor, fi) in rendered.denominator" :key="`d${fi}`">
                <span v-if="fi > 0" class="px-0.5">·</span>
                <template v-for="(part, pi) in factor.parts" :key="`d${fi}-${pi}`">
                  <sup v-if="part.sup">{{ part.text }}</sup>
                  <template v-else>{{ part.text }}</template>
                </template>
              </template>
            </span>
          </span>
        </div>
      </div>

      <p class="text-[11px] text-neutral-500 dark:text-neutral-400">{{ rendered.note }}</p>
      <p class="text-[11px] text-neutral-500 dark:text-neutral-400">{{ rendered.conventions }}</p>

      <p v-if="poorFit" class="text-[11px] text-amber-600 dark:text-amber-400">
        The model is {{ fit!.rmsErrorDb.toFixed(2) }} dB rms from the measurement, so this formula
        describes the fit rather than the DUT.
      </p>

      <!-- The exports, folded away by default: the formula above is what a reader wants on screen,
           while the dropdown and the text box are there for the one time you copy it out. -->
      <CollapsibleSection title="Copy as…" size="xs" :default-open="false">
      <!-- Capped: a format dropdown stretched across the whole chart column reads as a mistake. The
           text box below is not — a formula is long, and the width is what stops it wrapping. -->
      <div class="flex max-w-sm items-center gap-2 text-xs">
        <select
          v-model="format"
          class="tb-input"
        >
          <option v-for="option in TRANSFER_FUNCTION_FORMATS" :key="option.id" :value="option.id">
            {{ option.label }}
          </option>
        </select>
        <button
          type="button"
          class="rounded border px-2 py-1 btn-action"
          title="Copies the text below. Some browsers refuse — the in-app preview pane is one — and it is selected for you instead."
          @click="copy"
        >
          {{ copyState === 'copied' ? 'Copied' : 'Copy' }}
        </button>
      </div>

      <textarea
        ref="textarea"
        :value="text"
        readonly
        rows="7"
        spellcheck="false"
        class="w-full resize-y rounded border border-neutral-300 p-2 font-mono text-[10px] leading-4 dark:border-neutral-700"
      />

      <p v-if="copyState === 'manual'" class="text-[11px] text-amber-600 dark:text-amber-400">
        This window will not write to the clipboard — the text is selected, so copy it by hand.
      </p>
      </CollapsibleSection>
    </template>
  </CollapsibleSection>
</template>
