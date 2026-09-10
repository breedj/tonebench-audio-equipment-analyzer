<script setup lang="ts">
import { computed, ref } from 'vue'
import type { ImpedanceTrace } from '../composables/useImpedance'
import CollapsibleSection from './CollapsibleSection.vue'
import { formatHz } from '../response/format'
import {
  dbOhmsToOhms,
  formatCapacitance,
  formatCurrent,
  formatInductance,
  formatOhms,
  formatReactiveComponent,
  seriesEquivalent,
} from '../impedance/equivalent'
import { findExtremes, findResonance } from '../impedance/resonance'
import {
  DUT_TYPES,
  analyseCapacitor,
  analyseInductor,
  type DutType,
} from '../impedance/component'

/**
 * The numbers an impedance curve is actually read for: where it peaks, what it sits at, and — at one
 * frequency you pick — the series R and L or C it is equivalent to there.
 *
 * <p>The equivalent-component readout is the point of this panel. A trace tells you the impedance is
 * 8.5 ohms at 45 degrees; what a person wants to know is that it is 6 ohms in series with a
 * millihenry, which is a component they can reason about. The arithmetic lives in
 * `impedance/equivalent.ts` so it can be tested — nothing in a .vue file is.</p>
 */
const dutType = defineModel<DutType>('dutType', { required: true })

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

/** "1.2 kHz–48 kHz", so a fitted value is never quoted without the band it came from. */
function formatBand(fit: { fromHz: number; toHz: number } | null): string {
  return fit ? `${formatHz(fit.fromHz)}–${formatHz(fit.toHz)}` : '—'
}

function formatRatio(value: number | null): string {
  if (value === null) return '—'
  return value < 0.01 ? value.toExponential(1) : value.toFixed(3)
}

function formatQ(q: number | null): string {
  return q === null ? '—' : q.toFixed(2)
}

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
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
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
      D is below what this fixture can resolve, so treat it as calibration residue rather than a
      measurement: a tenth of a degree of leftover channel phase is more invented loss than a good
      capacitor has. ESR at self-resonance is the number to trust.
    </p>
    <p v-else-if="capacitor && !capacitor.capacitance" class="text-xs text-neutral-500 dark:text-neutral-400">
      Nothing in this sweep is clearly capacitive — the phase never goes far enough below zero for
      the reactance to dominate the loss — so there is no capacitance to report. Either this is not a
      capacitor, or the sweep is above its self-resonance.
    </p>
    <p v-else-if="capacitor && capacitor.srfHz === null" class="text-xs text-neutral-500 dark:text-neutral-400">
      The sweep never reaches self-resonance, so there is no ESR or ESL to report — only C, which is
      the dominant term and is measured well. Raise the top of the sweep to reach them.
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
      The sweep never gets down to where the resistance dominates — the curve is still clearly
      inductive at its lowest point — so DCR is a small real part read out of a large reactance and
      any residual phase lands straight on it. Start the sweep lower.
    </p>

    <!-- Loudspeaker -->
    <table v-if="resonance" class="w-full text-xs">
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

    <p v-if="resonance && resonance.qms === null" class="text-xs text-amber-600 dark:text-amber-400">
      The sweep does not reach far enough past resonance on one side to find where the curve falls to
      its half-power point, so there is no width to read a Q from. Widen the sweep.
    </p>
    <p v-else-if="resonance" class="text-xs text-neutral-500 dark:text-neutral-400">
      Vas, Bl and Mms need a second run with a known added mass or a sealed box, so they are not
      derivable from this one and are deliberately not shown. Smoothing blunts Zmax and widens the
      skirts, so measure with none if these numbers matter.
    </p>

    <p v-if="dutType === 'Loudspeaker' && !resonance" class="text-xs text-neutral-500 dark:text-neutral-400">
      No motional resonance found: the curve is flat, still rising at the top of the sweep, or its
      peak is under half again its baseline. Nothing here is reported from a shape that is not there.
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
