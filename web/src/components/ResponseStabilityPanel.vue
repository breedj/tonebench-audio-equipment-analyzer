<script setup lang="ts">
import { computed } from 'vue'
import type { StabilityMargins } from '../response/margins'
import type { NyquistRadiusMode } from '../response/projection'
import { formatHz } from '../response/format'
import CollapsibleSection from './CollapsibleSection.vue'

/**
 * Gain and phase margin beside the polar views.
 *
 * Its own panel rather than a row in `ResponseMeasurementsPanel`: that one is a bandwidth table keyed
 * to a `ResponseView`, with its own columns and per-row Show toggles, and these numbers share
 * neither its shape nor its meaning.
 */
const props = defineProps<{
  margins: StabilityMargins
  radiusMode: NyquistRadiusMode
  projection: 'Nyquist' | 'Nichols'
}>()

const invertSign = defineModel<boolean>('invertSign', { required: true })

function formatDeg(v: number | null): string {
  return v === null ? '—' : `${v.toFixed(1)}°`
}

function formatDb(v: number | null): string {
  return v === null ? '—' : `${(v + 0).toFixed(2).replace(/^-0\.00$/, '0.00')} dB`
}

/** Under the dB radius map the picture's distances are not metric, so only the numbers may be trusted. */
const distancesAreMetric = computed(() => props.projection === 'Nichols' || props.radiusMode === 'Linear')

const rows = computed(() => [
  { label: 'Phase margin', value: formatDeg(props.margins.phaseMarginDeg), at: props.margins.gainCrossoverHz },
  { label: 'Gain margin', value: formatDb(props.margins.gainMarginDb), at: props.margins.phaseCrossoverHz },
])
</script>

<template>
  <CollapsibleSection title="Stability">
    <table class="w-full text-left text-[11px] whitespace-nowrap">
      <thead class="text-neutral-500 dark:text-neutral-400">
        <tr>
          <th class="font-normal"></th>
          <th class="font-normal">Value</th>
          <th class="font-normal">at</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.label">
          <td class="pr-2 text-neutral-500 dark:text-neutral-400">{{ row.label }}</td>
          <td class="pr-2 font-mono text-neutral-800 dark:text-neutral-100">{{ row.value }}</td>
          <td class="font-mono">{{ row.at === null ? '—' : formatHz(row.at) }}</td>
        </tr>
      </tbody>
    </table>

    <p v-if="margins.outOfBand" class="text-[11px] text-amber-600 dark:text-amber-400">
      The curve crosses neither unity gain nor the critical phase inside the plotted window. These
      are not margins of zero — there is nothing here to measure one from.
    </p>
    <p
      v-else-if="margins.gainCrossings.length > 1 || margins.phaseCrossings.length > 1"
      class="text-[11px] text-neutral-500 dark:text-neutral-400"
    >
      {{ margins.gainCrossings.length }} unity-gain and {{ margins.phaseCrossings.length }} critical-phase
      crossings; the worst of each is reported.
    </p>

    <label class="tb-check-row">
      <input v-model="invertSign" type="checkbox" class="tb-check" />
      Invert loop sign (180°)
    </label>

    <!-- Not a footnote. A margin quoted from a closed-loop response is a number about nothing, and
         the polarity of a loop-gain injection is something the analyzer genuinely cannot see. -->
    <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      Margins describe a <strong>loop gain</strong>. They mean stability only if this sweep measured
      one — the generator injected in series into the feedback path, the two inputs either side of
      the injection. Measured input-to-output, an amplifier's closed-loop response yields no usable
      loop gain. Whether the summing-junction inversion sits inside the measured path depends on the
      injection point, so check the sign toggle above against a known-stable case before trusting the
      figures.
    </p>
    <p v-if="!distancesAreMetric" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      The dB radius keeps the whole range on screen but is not distance-preserving: read the margins
      from these numbers, not from how close the curve looks to −1.
    </p>
  </CollapsibleSection>
</template>
