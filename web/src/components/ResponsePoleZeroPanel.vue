<script setup lang="ts">
import { computed } from 'vue'
import type { ComplexPoint } from '../response/complexPlane'
import { fitRational, suggestOrder, type Singularity } from '../response/rationalFit'
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

/** The measured band — the only region the fit can speak for. */
const band = computed(() => {
  if (props.curve.length === 0) return null
  return { lowHz: props.curve[0].freqHz, highHz: props.curve[props.curve.length - 1].freqHz }
})

/**
 * A root outside the measured band is an extrapolation, not a measurement: the data constrains the
 * response between the endpoints and says nothing beyond them, so a pole placed there is whatever
 * the fit needed to bend the in-band curve. Flagged rather than dropped — it is often *roughly*
 * right, and hiding it would make the model look like it had fewer components than it does.
 */
function outsideBand(s: Singularity): boolean {
  const b = band.value
  return b !== null && (s.freqHz < b.lowHz || s.freqHz > b.highHz)
}

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
        <span>DC gain {{ fit.dcGainDb.toFixed(2) }} dB</span>
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

      <p v-if="fit.cancellations.length > 0" class="text-[11px] text-amber-600 dark:text-amber-400">
        {{ fit.cancellations.length }} pole–zero pair{{ fit.cancellations.length > 1 ? 's' : '' }} nearly
        cancel, which is the fit spending order on noise. Lower the order.
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
