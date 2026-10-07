<script setup lang="ts">
import { computed } from 'vue'
import type { ComplexPoint } from '../response/complexPlane'
import {
  CANCELLATION_SWING_DB,
  dcGainExtrapolation,
  fitRational,
  POOR_FIT_DB,
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
const props = defineProps<{
  curve: ComplexPoint[]
  /** Whether the chirp's removed delay has been put back — the display panel's own checkbox. */
  restoreDelay: boolean
  /** How much delay the estimator removed, or null when it removed none (no measured reference). */
  latencySamples: number | null
}>()

/**
 * The delay the fit is being asked to swallow, or null when there is none to worry about.
 *
 * Only a chirp run against a measured reference reports a latency at all, and there the rotation is
 * the DUT's own delay rather than the record's arbitrary start point — so a non-null value with the
 * box unticked is always worth saying out loud.
 */
const delayStillRemoved = computed(() =>
  !props.restoreDelay && props.latencySamples !== null ? props.latencySamples : null,
)

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

/**
 * How many of the fitted entries the sweep actually constrained, and how many are extrapolation.
 *
 * <p>Counted in <i>entries</i> — a conjugate pair is one — so the numbers match the rows below
 * rather than the map's legend, which counts roots and so says 5 where this says 3.</p>
 */
const bandSplit = computed(() => {
  const all = fit.value ? [...fit.value.poles, ...fit.value.zeros] : []
  const outside = all.filter(outsideBand).length
  return { total: all.length, outside, inside: all.length - outside }
})

/**
 * Most of the model is extrapolation — and the lever for that is the <i>order</i>, which is the one
 * thing the panel never said outright.
 *
 * <p>Reported 2026-10-06, from the first-order filter: Auto picked order 3, the table listed the
 * right pole at 2.02 kHz and three out-of-band entries, and nothing on screen suggested order 1 —
 * so the reasonable next move looked like the chart's "Hide out of band" checkbox, which does not
 * touch the fit and cannot. The existing cancellation note covers the other way a fit wastes order
 * (a pair that does nothing in band); this covers roots the band never reached.</p>
 *
 * <p>Not shown when every root is outside, which has its own note and is usually already at the
 * lowest order on offer, and not shown at parity — an order-2 fit with one in-band pair and one
 * out-of-band zero pair is an ordinary, good fit.</p>
 */
const mostlyExtrapolated = computed(
  () => !allRootsOutOfBand.value && bandSplit.value.inside > 0 && bandSplit.value.outside > bandSplit.value.inside,
)

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

</script>

<template>
  <CollapsibleSection title="Poles and zeros">
    <div v-if="!fit" class="text-[11px] text-neutral-500 dark:text-neutral-400">
      Not enough measured points in the plotted window to fit a model.
    </div>

    <template v-else>
      <!-- Capped: this panel moved into the chart column on 2026-09-27, and a `justify-between` row
           in a 600 px box puts "Poles" and its spinner at opposite ends of the screen. -->
      <div class="flex max-w-[14rem] flex-col gap-1 text-xs">
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
              class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            />
          </label>
          <label class="flex items-center justify-between gap-2" title="The numerator order: how many zeros the model may have, never more than poles. 0 for an all-pole DUT — a low-pass filter, an amplifier without an output transformer — which is otherwise fitted with as many zeros as poles and spends them on noise.">
            Zeros
            <input
              v-model.number="zeroOrder"
              type="number"
              min="0"
              :max="activeOrder"
              class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            />
          </label>
        </template>
        <span v-else class="text-neutral-500 dark:text-neutral-400">Order {{ activeOrder }} (poles and zeros)</span>
      </div>

      <!-- Content-sized, like the Measurements panel's: four short columns spread over the chart
           column's width read as a table with its cells missing. `self-start` is the part that does
           it; see that panel for why dropping `w-full` on its own is not enough. -->
      <table class="self-start text-left text-[11px] whitespace-nowrap">
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
      <!-- First, because it explains most of the others. A chirp run whose removed delay is still
           out hands the fit a phase lead that is not a rational function of s, and the fit spends
           poles imitating it: the order climbs and a pair usually lands in the right half-plane.
           The |H(s)| surface said this from the start and the map did not, which is exactly how a
           2 kHz Butterworth came to be read as an order-7 model with a right-half-plane pair. -->
      <p v-if="delayStillRemoved" class="text-[11px] text-amber-600 dark:text-amber-400">
        The chirp's removed delay ({{ delayStillRemoved.toFixed(1) }} samples) is still out, so these
        roots are partly an artefact of it — expect too high an order and a right-half-plane pair.
        Tick <strong>Restore removed delay</strong> before reading them.
      </p>

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

      <p v-if="mostlyExtrapolated" class="text-[11px] text-amber-600 dark:text-amber-400">
        Only {{ bandSplit.inside }} of the {{ bandSplit.total }} entries above
        {{ bandSplit.inside === 1 ? 'lies' : 'lie' }} inside the measured band; the other
        {{ bandSplit.outside }} {{ bandSplit.outside === 1 ? 'is' : 'are' }} extrapolation. Untick
        <strong>Choose order automatically</strong> and try
        <strong>Poles {{ bandSplit.inside }}</strong>, with <strong>Zeros 0</strong> for an all-pole
        DUT such as a filter or an amplifier without an output transformer. Note the out-of-band
        roots are still part of H(s) and of the fit error — a root above the band edge, especially a
        high-Q one, can carry the whole top of the band.
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
