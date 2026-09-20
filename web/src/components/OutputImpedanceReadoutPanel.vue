<script setup lang="ts">
import { computed } from 'vue'
import type { ImpedanceTrace } from '../composables/useImpedance'
import { formatInductance, formatOhms } from '../impedance/equivalent'
import { formatDampingFactor, MIN_MEASURABLE_OHMS, zoutReadings } from '../outputImpedance/readings'
import CollapsibleSection from './CollapsibleSection.vue'

/**
 * The figures an output-impedance run is quoted by: Zout and damping factor at 20 Hz, 1 kHz and
 * 20 kHz (the three a specification sheet gives), the worst point, the median, and — when the run
 * measured a phase — the series R and L at the top of the band, which is the output coil. Read
 * through outputImpedance/readings.ts, which the report reads too, so paper and screen agree.
 */
const props = defineProps<{
  trace: ImpedanceTrace | null
  nominalOhms: number
  loadOhms: number
  secondLoadOhms: number | null
  rightChannelIsReference: boolean
  generatorAmpDbv: number
  inputRangeDbv: number | null
  firstGainAt1kDb: number | null
  secondGainAt1kDb: number | null
  warning: string | null
}>()

const readings = computed(() => (props.trace ? zoutReadings(props.trace, props.nominalOhms, props.rightChannelIsReference) : null))

function hz(f: number): string {
  return f >= 1000 ? `${(f / 1000).toFixed(f >= 10000 ? 1 : 2)} kHz` : `${f.toFixed(f < 100 ? 1 : 0)} Hz`
}
</script>

<template>
  <CollapsibleSection title="Measurements">
    <template v-if="readings">
      <table class="w-full text-left text-[11px] whitespace-nowrap">
        <thead class="text-neutral-500 dark:text-neutral-400">
          <tr>
            <th class="font-normal"></th>
            <th class="font-normal">Zout</th>
            <th class="font-normal" :title="`Damping factor: ${nominalOhms} Ω over |Zout|.`">DF</th>
          </tr>
        </thead>
        <tbody class="font-mono">
          <tr v-for="row in [readings.low, readings.at1k, readings.high]" :key="row.freqHz">
            <td class="pr-2 font-sans">{{ hz(row.freqHz) }}</td>
            <td class="pr-2">{{ formatOhms(row.ohms) }}</td>
            <td>{{ formatDampingFactor(row.dampingFactor) }}</td>
          </tr>
          <tr>
            <td class="pr-2 font-sans" title="The highest impedance on the curve — the lowest damping factor — and where it is.">Worst</td>
            <td class="pr-2">{{ formatOhms(readings.worst.ohms) }}</td>
            <td>{{ formatDampingFactor(readings.worst.dampingFactor) }} <span class="text-neutral-500">at {{ hz(readings.worst.freqHz) }}</span></td>
          </tr>
          <tr>
            <td class="pr-2 font-sans">Median</td>
            <td class="pr-2">{{ formatOhms(readings.medianOhms) }}</td>
            <td>{{ formatDampingFactor(readings.medianOhms > MIN_MEASURABLE_OHMS ? nominalOhms / readings.medianOhms : null) }}</td>
          </tr>
        </tbody>
      </table>

      <div v-if="readings.topEquivalent" class="mt-2 text-[11px]" title="The series equivalent of Zout at the top of the band: the resistance and, when the phase is inductive, the inductance — an output coil reads here.">
        <div class="mb-1 text-neutral-500 dark:text-neutral-400">Series equivalent at {{ hz(readings.high.freqHz) }}</div>
        <div class="font-mono">R {{ formatOhms(readings.topEquivalent.resistanceOhms) }} · L {{ formatInductance(readings.topEquivalent.inductanceH) }}</div>
      </div>

      <div class="mt-2 space-y-0.5 text-[11px] text-neutral-500 dark:text-neutral-400">
        <div title="The two loads the passes were measured into — the R₁ and R₂ the output impedance is solved from.">Loads {{ formatOhms(loadOhms) }} · {{ secondLoadOhms === null ? 'open' : formatOhms(secondLoadOhms) }}</div>
        <div v-if="firstGainAt1kDb !== null && secondGainAt1kDb !== null" title="The two gains the 1 kHz reading was taken from. Their difference is the whole measurement, which is why both passes share one input range.">
          Gain at 1 kHz: {{ firstGainAt1kDb.toFixed(3) }} dB · {{ secondGainAt1kDb.toFixed(3) }} dB
        </div>
        <div title="The generator level both passes ran at — probed in the first pass for an output-referred target.">Generator {{ generatorAmpDbv.toFixed(2) }} dBV</div>
        <div v-if="inputRangeDbv !== null" title="The one input range both passes were read at.">Input range {{ inputRangeDbv.toFixed(0) }} dBV</div>
        <div>{{ rightChannelIsReference ? 'Complex — right channel is the reference' : 'Magnitude only — assumes Zout in phase with the load' }}</div>
      </div>

      <p v-if="warning" class="mt-2 text-[11px] text-amber-600 dark:text-amber-500">{{ warning }}</p>
      <p v-if="readings.worst.ohms <= MIN_MEASURABLE_OHMS" class="mt-2 text-[11px] text-amber-600 dark:text-amber-500">
        Every point read no output impedance at all — an ideal source, so there is no curve to draw. On the
        simulator that is the status bar's Output dropdown at Ideal; set it to Solid state or Tube amp.
      </p>
    </template>
    <p v-else class="text-xs text-neutral-400 dark:text-neutral-600">No output impedance measured yet.</p>
  </CollapsibleSection>
</template>
