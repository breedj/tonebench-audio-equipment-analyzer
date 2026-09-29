<script setup lang="ts">
import { computed } from 'vue'
import type { ImpedanceTrace } from '../composables/useImpedance'
import CollapsibleSection from './CollapsibleSection.vue'
import { formatHz } from '../response/format'
import {
  dbOhmsToOhms,
  formatCapacitance,
  formatCurrent,
  formatInductance,
  formatOhms,
} from '../impedance/equivalent'
import { findExtremes, findResonance } from '../impedance/resonance'
import {
  DUT_TYPES,
  analyseCapacitor,
  analyseInductor,
  type DutType,
} from '../impedance/component'
import {
  CAPACITOR_DISSIPATION_NOTE,
  CAPACITOR_NO_SRF_NOTE,
  CAPACITOR_NOT_CAPACITIVE_NOTE,
  INDUCTOR_DCR_NOTE,
  LOUDSPEAKER_NO_Q_NOTE,
  LOUDSPEAKER_NO_RESONANCE_NOTE,
  LOUDSPEAKER_NOTE,
  equivalentAt,
  equivalentRows,
  formatBand,
  formatQ,
  formatRatio,
  loudspeakerTwoPeakNote,
} from '../impedance/readings'

/**
 * The numbers an impedance curve is actually read for: where it peaks, what it sits at, and — at one
 * frequency you pick — the series R and L or C it is equivalent to there.
 *
 * <p>The equivalent-component readout is the point of this panel. A trace tells you the impedance is
 * 8.5 ohms at 45 degrees; what a person wants to know is that it is 6 ohms in series with a
 * millihenry, which is a component they can reason about. The arithmetic lives in
 * `impedance/equivalent.ts` so it can be tested — nothing in a .vue file is — and the formatting of
 * the component block and its notes in `impedance/readings.ts`, which the report's impedance
 * chapter prints from too.</p>
 */
const dutType = defineModel<DutType>('dutType', { required: true })

/**
 * The frequency the equivalent-circuit readout describes.
 *
 * <p>A model rather than local state because two other things quote it: the comparison table under
 * the chart reads every checked history curve at this frequency, and the report's Equivalent circuit
 * block prints it. A number the panel kept to itself would leave those two picking their own.</p>
 */
const cursorHz = defineModel<number>('equivalentHz', { required: true })

const props = defineProps<{
  traces: ImpedanceTrace[]
  medianOhms: number | null
  driveCurrentAmps: number | null
  referenceOhms: number
}>()

const trace = computed(() => props.traces.find((t) => t.kind === 'Impedance') ?? null)

const peak = computed(() => {
  const t = trace.value
  if (!t) return null
  return { ohms: dbOhmsToOhms(t.peakDbOhms), freqHz: t.peakFreqHz }
})

/**
 * The curve as ohms, taken from the per-column <b>maximum</b> — the array the chart draws as its
 * line and the one the trace's own `peakDbOhms` came from, so nothing derived here can disagree with
 * the peak already on this panel. (A stepped sweep puts one measured point in every column, so its
 * min and max are the same number and the question does not arise.)
 */
const curveOhms = computed<number[] | null>(() => {
  const t = trace.value
  if (!t || t.freqHz.length === 0) return null
  return t.magnitudeMaxDbOhms.map(dbOhmsToOhms)
})

/**
 * The minimum |Z|, which is the number an amplifier cares about: the worst-case load, where the
 * output stage is asked for the most current. A nominal 8 ohm driver routinely dips below 6.
 */
const extremes = computed(() => {
  const t = trace.value
  const ohms = curveOhms.value
  return t && ohms ? findExtremes(t.freqHz, ohms) : null
})

/**
 * The Thiele-Small block, and only when the user has said the DUT is a loudspeaker.
 *
 * <p>It used to appear whenever the curve had any peak at all, which was wrong in a way that showed
 * plausible numbers: a choke or a transformer winding peaks at its <i>self-resonance</i>, so the
 * block fired and reported fs, Re and three Qs — of which only fs meant anything, and it meant
 * something different. Asking is the fix; nothing in the curve can distinguish the two.</p>
 */
const resonance = computed(() => {
  if (dutType.value !== 'Loudspeaker') return null
  const t = trace.value
  const ohms = curveOhms.value
  return t && ohms ? findResonance(t.freqHz, ohms) : null
})

/**
 * What a second impedance peak means, when the curve has one — and when it has, that note replaces
 * the Thiele-Small table rather than sitting under it.
 *
 * <p>Every row of that table is arithmetically correct for the peak `findResonance` chose and
 * physically meaningless for a ported box, which is exactly the kind of wrong a reader cannot catch
 * from the numbers themselves. The report's impedance chapter withholds them for the same reason,
 * through `componentReading`.</p>
 */
const twoPeakNote = computed(() => {
  const r = resonance.value
  return r && r.rival ? loudspeakerTwoPeakNote(r.fsHz, r.rival) : null
})

const capacitor = computed(() => {
  if (dutType.value !== 'Capacitor') return null
  const t = trace.value
  const ohms = curveOhms.value
  return t && ohms ? analyseCapacitor(t.freqHz, ohms, t.phaseDeg) : null
})

const inductor = computed(() => {
  if (dutType.value !== 'Inductor') return null
  const t = trace.value
  const ohms = curveOhms.value
  return t && ohms ? analyseInductor(t.freqHz, ohms, t.phaseDeg) : null
})

/**
 * The measured column nearest the chosen frequency, read as a series equivalent, and the rows the
 * block shows for it. Both come from impedance/readings.ts, which the report chapter and the
 * comparison table read too — the arithmetic is the kind where a degrees/radians slip produces a
 * plausible wrong answer, and nothing in a .vue is testable.
 */
const atCursor = computed(() => {
  const t = trace.value
  return t ? equivalentAt(t, cursorHz.value) : null
})

const equivalentBlock = computed(() => equivalentRows(atCursor.value))
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
          <td
            class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400"
            title="The worst-case load: where the DUT draws the most current from whatever is driving it."
          >
            Minimum |Z|
          </td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ extremes ? formatOhms(extremes.minOhms) : '—' }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">at</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ extremes ? formatHz(extremes.minFreqHz) : '—' }}
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

  <CollapsibleSection title="Component" size="xs">
    <label class="flex flex-col gap-1 text-xs">
      <span
        title="What is on the fixture. Nothing in an impedance curve can tell a choke's self-resonance from a driver's motional resonance -- they are the same shape -- so the readout asks rather than guesses."
      >
        Device under test
      </span>
      <select
        v-model="dutType"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      >
        <option v-for="option in DUT_TYPES" :key="option.value" :value="option.value">{{ option.label }}</option>
      </select>
    </label>

    <!-- Capacitor -->
    <table v-if="capacitor" class="w-full text-xs">
      <tbody class="font-mono">
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="Fitted across every sample that is clearly capacitive and below self-resonance.">C</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatCapacitance(capacitor.capacitance?.value ?? null) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">over</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatBand(capacitor.capacitance) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="Self-resonant frequency: where the phase crosses from capacitive to inductive.">SRF</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ capacitor.srfHz === null ? '—' : formatHz(capacitor.srfHz) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="|Z| at self-resonance, where the reactances cancel and only the loss is left. The one place ESR is honestly measurable.">ESR</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ capacitor.esrOhms === null ? '—' : formatOhms(capacitor.esrOhms) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="Equivalent series inductance, fitted above self-resonance with the capacitive term removed.">ESL</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatInductance(capacitor.esl?.value ?? null) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="tan delta = R/|X| in the capacitive band.">D</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ formatRatio(capacitor.dissipationFactor) }}
            <span v-if="capacitor.dissipationFactor !== null && !capacitor.dissipationTrustworthy" class="text-amber-600 dark:text-amber-400">?</span>
          </td>
        </tr>
      </tbody>
    </table>

    <p
      v-if="capacitor && capacitor.dissipationFactor !== null && !capacitor.dissipationTrustworthy"
      class="text-xs text-amber-600 dark:text-amber-400"
    >
      {{ CAPACITOR_DISSIPATION_NOTE }}
    </p>
    <p v-else-if="capacitor && !capacitor.capacitance" class="text-xs text-neutral-500 dark:text-neutral-400">
      {{ CAPACITOR_NOT_CAPACITIVE_NOTE }}
    </p>
    <p v-else-if="capacitor && capacitor.srfHz === null" class="text-xs text-neutral-500 dark:text-neutral-400">
      {{ CAPACITOR_NO_SRF_NOTE }}
    </p>

    <!-- Inductor -->
    <table v-if="inductor" class="w-full text-xs">
      <tbody class="font-mono">
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="Fitted across every sample that is clearly inductive and below self-resonance.">L</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatInductance(inductor.inductance?.value ?? null) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">over</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatBand(inductor.inductance) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="Winding resistance: the real part where |Z| is lowest.">DCR</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ inductor.dcrOhms === null ? '—' : formatOhms(inductor.dcrOhms) }}
            <span v-if="inductor.dcrOhms !== null && !inductor.dcrTrustworthy" class="text-amber-600 dark:text-amber-400">?</span>
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="Self-resonant frequency: where the phase crosses from inductive to capacitive.">SRF</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ inductor.srfHz === null ? '—' : formatHz(inductor.srfHz) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="|Z| at self-resonance. For an inductor this is a peak, where the winding's own capacitance parallels it.">Z at SRF</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ inductor.zAtSrfOhms === null ? '—' : formatOhms(inductor.zAtSrfOhms) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="|X|/R over the inductance fit's band.">Q</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatRatio(inductor.q) }}</td>
        </tr>
      </tbody>
    </table>

    <p v-if="inductor && inductor.dcrOhms !== null && !inductor.dcrTrustworthy" class="text-xs text-amber-600 dark:text-amber-400">
      {{ INDUCTOR_DCR_NOTE }}
    </p>

    <!-- Loudspeaker -->
    <table v-if="resonance && !twoPeakNote" class="w-full text-xs">
      <tbody class="font-mono">
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="Resonance frequency.">fs</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatHz(resonance.fsHz) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Zmax</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatOhms(resonance.zMaxOhms) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="Voice-coil resistance, read as the minimum |Z| above resonance. Close to, but not the same as, a DC ohmmeter's answer.">Re</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatOhms(resonance.reOhms) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400" title="The frequencies either side of fs where |Z| falls to the geometric mean of Zmax and Re. The width between them is what sets Q.">f1 / f2</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">
            {{ resonance.f1Hz === null || resonance.f2Hz === null
              ? '—'
              : `${formatHz(resonance.f1Hz)} / ${formatHz(resonance.f2Hz)}` }}
          </td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Qms</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatQ(resonance.qms) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Qes</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatQ(resonance.qes) }}</td>
        </tr>
        <tr>
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">Qts</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ formatQ(resonance.qts) }}</td>
        </tr>
      </tbody>
    </table>

    <p v-if="twoPeakNote" class="text-xs text-amber-600 dark:text-amber-400">
      {{ twoPeakNote }}
    </p>
    <p v-else-if="resonance && resonance.qms === null" class="text-xs text-amber-600 dark:text-amber-400">
      {{ LOUDSPEAKER_NO_Q_NOTE }}
    </p>
    <p v-else-if="resonance" class="text-xs text-neutral-500 dark:text-neutral-400">
      {{ LOUDSPEAKER_NOTE }}
    </p>

    <p v-if="dutType === 'Loudspeaker' && !resonance" class="text-xs text-neutral-500 dark:text-neutral-400">
      {{ LOUDSPEAKER_NO_RESONANCE_NOTE }}
    </p>
  </CollapsibleSection>

  <CollapsibleSection title="Equivalent circuit" size="xs">
    <label class="flex flex-col gap-1 text-xs">
      At frequency (Hz)
      <input
        v-model.number="cursorHz"
        type="number"
        min="1"
        step="any"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
      />
    </label>

    <table class="w-full text-xs">
      <tbody class="font-mono">
        <tr v-for="row in equivalentBlock" :key="row.label">
          <td class="py-0.5 pr-2 font-sans text-neutral-500 dark:text-neutral-400">{{ row.label }}</td>
          <td class="py-0.5 text-right text-neutral-900 dark:text-neutral-100">{{ row.value }}</td>
        </tr>
      </tbody>
    </table>
  </CollapsibleSection>
</template>
