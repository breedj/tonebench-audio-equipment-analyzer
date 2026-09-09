<script setup lang="ts">
import { computed, ref } from 'vue'
import type { ImpedanceTrace } from '../composables/useImpedance'
import CollapsibleSection from './CollapsibleSection.vue'
import { formatHz } from '../response/format'
import {
  dbOhmsToOhms,
  formatCurrent,
  formatOhms,
  formatReactiveComponent,
  seriesEquivalent,
} from '../impedance/equivalent'

/**
 * The numbers an impedance curve is actually read for: where it peaks, what it sits at, and — at one
 * frequency you pick — the series R and L or C it is equivalent to there.
 *
 * <p>The equivalent-component readout is the point of this panel. A trace tells you the impedance is
 * 8.5 ohms at 45 degrees; what a person wants to know is that it is 6 ohms in series with a
 * millihenry, which is a component they can reason about. The arithmetic lives in
 * `impedance/equivalent.ts` so it can be tested — nothing in a .vue file is.</p>
 */
const props = defineProps<{
  traces: ImpedanceTrace[]
  medianOhms: number | null
  driveCurrentAmps: number | null
  referenceOhms: number
}>()

const trace = computed(() => props.traces.find((t) => t.kind === 'Impedance') ?? null)

/** The frequency the equivalent-circuit readout describes. */
const cursorHz = ref(1000)

const peak = computed(() => {
  const t = trace.value
  if (!t) return null
  return { ohms: dbOhmsToOhms(t.peakDbOhms), freqHz: t.peakFreqHz }
})

/** The measured column nearest the chosen frequency, as ohms and degrees. */
const atCursor = computed(() => {
  const t = trace.value
  if (!t || t.freqHz.length === 0) return null

  let best = 0
  for (let i = 1; i < t.freqHz.length; i++) {
    if (Math.abs(t.freqHz[i] - cursorHz.value) < Math.abs(t.freqHz[best] - cursorHz.value)) best = i
  }

  // The midpoint of the column's min/max, not the max: the max carries the aggregation bias of a
  // maximum over several bins.
  const ohms =
    0.5 * (dbOhmsToOhms(t.magnitudeMinDbOhms[best]) + dbOhmsToOhms(t.magnitudeMaxDbOhms[best]))

  return {
    freqHz: t.freqHz[best],
    ohms,
    phaseDeg: t.phaseDeg[best],
    coherence: t.coherence[best],
    equivalent: seriesEquivalent(ohms, t.phaseDeg[best], t.freqHz[best]),
  }
})

const componentLabel = computed(() => {
  const e = atCursor.value?.equivalent
  if (!e) return 'Series L or C'
  if (e.inductanceH !== null) return 'Series inductance'
  if (e.capacitanceF !== null) return 'Series capacitance'
  return 'Series reactance'
})
</script>

<template>
  <CollapsibleSection title="Measurements" size="xs">
    <table class="w-full text-xs">
      <tbody class="font-mono">
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Peak |Z|</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ peak ? formatOhms(peak.ohms) : '—' }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="For a driver this is its resonance.">
            at
          </td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ peak ? formatHz(peak.freqHz) : '—' }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Median |Z|</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ medianOhms === null ? '—' : formatOhms(medianOhms) }}
          </td>
        </tr>
        <tr>
          <td
            class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400"
            title="Roughly what the DUT was driven with. Approximate: a sweep's current varies with Z across the band."
          >
            Drive current
          </td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ formatCurrent(driveCurrentAmps) }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Reference R</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatOhms(referenceOhms) }}</td>
        </tr>
      </tbody>
    </table>
  </CollapsibleSection>

  <CollapsibleSection title="Equivalent circuit" size="xs">
    <label class="flex flex-col gap-1 text-xs">
      At frequency (Hz)
      <input
        v-model.number="cursorHz"
        type="number"
        min="1"
        step="any"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
    </label>

    <table class="w-full text-xs">
      <tbody class="font-mono">
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Measured at</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ atCursor ? formatHz(atCursor.freqHz) : '—' }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">|Z|</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ atCursor ? formatOhms(atCursor.ohms) : '—' }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Phase</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ atCursor ? `${atCursor.phaseDeg.toFixed(1)}°` : '—' }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Series R</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ atCursor ? formatOhms(atCursor.equivalent.resistanceOhms) : '—' }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Series X</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ atCursor ? formatOhms(atCursor.equivalent.reactanceOhms) : '—' }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">{{ componentLabel }}</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ atCursor ? formatReactiveComponent(atCursor.equivalent) : '—' }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Coherence</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ atCursor ? atCursor.coherence.toFixed(4) : '—' }}
          </td>
        </tr>
      </tbody>
    </table>
  </CollapsibleSection>
</template>
