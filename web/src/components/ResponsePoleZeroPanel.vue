<script setup lang="ts">
import { computed } from 'vue'
import type { ComplexPoint } from '../response/complexPlane'
import {
  CANCELLATION_SWING_DB,
  dcGainExtrapolation,
  fitRational,
  singularityInBand,
  suggestOrder,
  type Singularity,
} from '../response/rationalFit'
import { measuredBand } from '../response/poleZeroMap'
import { formatHz } from '../response/format'
import CollapsibleSection from './CollapsibleSection.vue'

/**
 * The fitted pole/zero table, beside the Nyquist and Nichols views.
 *
 * Fits the *same* `ComplexPoint[]` the chart draws — windowed, sign-corrected, delay-corrected — so
 * the table can never describe a curve other than the one on screen.
 */
const props = defineProps<{ curve: ComplexPoint[] }>()

/** The denominator order — how many poles the model has. */
const order = defineModel<number>('order', { required: true })
/**
 * The numerator order — how many zeros — pinned separately since 2026-09-21. With one order for
 * both, an all-pole DUT (any of the four classic filters, most amplifiers below their output
 * stage) is fitted with as many zeros as poles, and the fitter spends them: measured on the
 * simulated fifth-order Butterworth, Poles = Zeros = 5 read 0.31 dB rms with three spurious zeros
 * and two right-half-plane poles, while Poles 5, Zeros 0 is the textbook answer. Auto keeps the two
 * equal, because the knee search has no way of knowing which the DUT is.
 */
const zeroOrder = defineModel<number>('zeroOrder', { required: true })
/** Follow `suggestOrder` (poles = zeros); off, both orders are pinned by hand. */
const autoOrder = defineModel<boolean>('autoOrder', { required: true })

const suggestion = computed(() => (props.curve.length >= 6 ? suggestOrder(props.curve, 8) : null))

const activeOrder = computed(() =>
  autoOrder.value ? (suggestion.value?.order ?? 2) : Math.max(1, Math.min(10, order.value)),
)

/** Never more zeros than poles: a DUT's transfer function is proper, and an improper fit is a fit of nothing physical. */
const activeZeroOrder = computed(() =>
  autoOrder.value ? activeOrder.value : Math.max(0, Math.min(activeOrder.value, zeroOrder.value)),
)

const fit = computed(() =>
  props.curve.length >= 6 ? fitRational(props.curve, activeZeroOrder.value, activeOrder.value) : null,
)

defineExpose({ fit })

/** The measured band — the only region the fit can speak for, and the map's own definition of it. */
const band = computed(() => measuredBand(props.curve))

/**
 * A root outside the measured band is an extrapolation, not a measurement: the data constrains the
 * response between the endpoints and says nothing beyond them, so a pole placed there is whatever
 * the fit needed to bend the in-band curve. Flagged rather than dropped — it is often *roughly*
 * right, and hiding it would make the model look like it had fewer components than it does.
 */
function outsideBand(s: Singularity): boolean {
  return band.value !== null && !singularityInBand(s, band.value)
}

/**
 * Non-null when the model bends below the sweep on its way to DC, in which case the DC gain is an
 * extrapolation through a corner nothing measured and is labelled as one. See
 * `dcGainExtrapolation`, which is also what keeps this out of the template: the rule is the
 * falsifiable part, and nothing in a `.vue` is testable under the node environment vitest runs in.
 */
const dcExtrapolated = computed(() => (fit.value ? dcGainExtrapolation(fit.value, band.value) : null))

/**
 * Every root the fit placed lies outside the sweep. Then the measurement has constrained no
 * component at all — it has only said the response is featureless between its endpoints — and the
 * whole table is extrapolation. Measured on a push-pull EL84 amplifier over 13 Hz–91 kHz, which is
 * flat to 0.11 dB across the audio band and whose every pole lies beyond the sweep at both ends.
 */
const allRootsOutOfBand = computed(() => {
  const roots = fit.value ? [...fit.value.poles, ...fit.value.zeros] : []
  return band.value !== null && roots.length > 0 && roots.every(outsideBand)
})

function rowsOf(list: Singularity[]) {
  return list.map((s) => ({
    freq: formatHz(s.freqHz),
    q: s.q === null ? '—' : s.q.toFixed(3),
    kind: s.isPair ? 'pair' : 'real',
    flags: [s.isRhp ? 'RHP' : null, outsideBand(s) ? 'out of band' : null].filter(Boolean).join(', '),
  }))
}

const poleRows = computed(() => (fit.value ? rowsOf(fit.value.poles) : []))
const zeroRows = computed(() => (fit.value ? rowsOf(fit.value.zeros) : []))

/** Above this the model is not describing the measurement, whatever the table says. */
const POOR_FIT_DB = 0.5
</script>

<template>
  <CollapsibleSection title="Poles and zeros">
    <div v-if="!fit" class="text-[11px] text-neutral-500 dark:text-neutral-400">
      Not enough measured points in the plotted window to fit a model.
    </div>

    <template v-else>
      <div class="flex flex-col gap-1 text-xs">
        <label class="flex items-center gap-1.5">
          <input v-model="autoOrder" type="checkbox" />
          <span title="Takes the lowest order that fits about as well as the best one tried, rather than the lowest error — which is almost always the highest order.">
            Choose order automatically
          </span>
        </label>
        <template v-if="!autoOrder">
          <label class="flex items-center justify-between gap-2" title="The denominator order: how many poles the model may have.">
            Poles
            <input
              v-model.number="order"
              type="number"
              min="1"
              max="10"
              class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            />
          </label>
          <label class="flex items-center justify-between gap-2" title="The numerator order: how many zeros the model may have, never more than poles. 0 for an all-pole DUT — a low-pass filter, an amplifier without an output transformer — which is otherwise fitted with as many zeros as poles and spends them on noise.">
            Zeros
            <input
              v-model.number="zeroOrder"
              type="number"
              min="0"
              :max="activeOrder"
              class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            />
          </label>
        </template>
        <span v-else class="text-neutral-500 dark:text-neutral-400">Order {{ activeOrder }} (poles and zeros)</span>
      </div>

      <table class="w-full text-left text-[11px] whitespace-nowrap">
        <thead class="text-neutral-500 dark:text-neutral-400">
          <tr>
            <th class="font-normal"></th>
            <th class="font-normal">f</th>
            <th class="font-normal">Q</th>
            <th class="font-normal"></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, i) in poleRows" :key="`p${i}`">
            <td class="pr-2 text-neutral-500 dark:text-neutral-400">Pole{{ row.kind === 'pair' ? ' ×2' : '' }}</td>
            <td class="pr-2 font-mono text-neutral-800 dark:text-neutral-100">{{ row.freq }}</td>
            <td class="pr-2 font-mono">{{ row.q }}</td>
            <td class="text-amber-600 dark:text-amber-400">{{ row.flags }}</td>
          </tr>
          <tr v-for="(row, i) in zeroRows" :key="`z${i}`">
            <td class="pr-2 text-neutral-500 dark:text-neutral-400">Zero{{ row.kind === 'pair' ? ' ×2' : '' }}</td>
            <td class="pr-2 font-mono text-neutral-800 dark:text-neutral-100">{{ row.freq }}</td>
            <td class="pr-2 font-mono">{{ row.q }}</td>
            <td class="text-amber-600 dark:text-amber-400">{{ row.flags }}</td>
          </tr>
          <tr v-if="poleRows.length === 0 && zeroRows.length === 0">
            <td colspan="4" class="text-neutral-500 dark:text-neutral-400">No finite poles or zeros.</td>
          </tr>
        </tbody>
      </table>

      <div class="flex flex-col gap-0.5 text-[11px] text-neutral-500 dark:text-neutral-400">
        <span>
          DC gain {{ dcExtrapolated ? '≈ ' : '' }}{{ fit.dcGainDb.toFixed(2) }} dB<span
            v-if="dcExtrapolated"
            class="text-amber-600 dark:text-amber-400"
          >
            · extrapolated</span
          >
        </span>
        <span>Fit error {{ fit.rmsErrorDb.toFixed(3) }} dB rms · {{ fit.maxErrorDb.toFixed(3) }} dB peak</span>
      </div>

      <!-- The residual is what makes the table falsifiable. A pole list from a model that does not
           match the measurement is a description of nothing, so it is said plainly rather than left
           for the reader to work out from a number. -->
      <p v-if="fit.rmsErrorDb > POOR_FIT_DB" class="text-[11px] text-amber-600 dark:text-amber-400">
        The model does not follow the measurement ({{ fit.rmsErrorDb.toFixed(2) }} dB rms). Treat these
        values as meaningless until the fitted curve tracks the measured one — raise the order, or
        narrow the frequency window onto the region of interest.
      </p>

      <p v-if="dcExtrapolated" class="text-[11px] text-amber-600 dark:text-amber-400">
        The DC gain is extrapolated: the model's lowest root, a {{ dcExtrapolated.kind }} at
        {{ formatHz(dcExtrapolated.rootHz) }}, sits below the measured band, so the figure comes from a
        corner the sweep never reached rather than from the measurement. The passband gain is what the
        Bode trace reports.
      </p>

      <p v-if="allRootsOutOfBand" class="text-[11px] text-amber-600 dark:text-amber-400">
        Every root lies outside the measured band, so the sweep constrains no pole or zero — it says
        only that the response is featureless between its endpoints. Widen the sweep to place the
        components.
      </p>

      <!-- Not beside the note above: a fit whose roots are all outside the band is usually already at
           the lowest order the panel offers, where "lower the order" is advice that cannot be taken,
           and the out-of-band note says the more useful half of it. -->
      <p
        v-if="fit.cancellations.length > 0 && !allRootsOutOfBand"
        class="text-[11px] text-amber-600 dark:text-amber-400"
      >
        {{ fit.cancellations.length }} pole–zero pair{{ fit.cancellations.length > 1 ? 's move' : ' moves' }} the
        response by under {{ CANCELLATION_SWING_DB }} dB across the measured band, which is the fit
        spending order on noise. Lower the order.
      </p>

      <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
        A rational s-domain fit of the plotted band. It cannot represent a pure delay, and it can only
        place a root where the data constrains one — a pole outside
        <template v-if="band">{{ formatHz(band.lowHz) }}–{{ formatHz(band.highHz) }}</template>
        <template v-else>the measured band</template>
        is an extrapolation. Widen the sweep rather than trusting one.
      </p>
    </template>
  </CollapsibleSection>
</template>
