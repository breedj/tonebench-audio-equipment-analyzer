<script setup lang="ts">
import { computed } from 'vue'
import type { ResponseView } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { formatBandwidth, formatCorner, formatDb, formatHz, formatRatio } from '../response/format'
import CollapsibleSection from './CollapsibleSection.vue'

const props = defineProps<{
  traces: ResponseTrace[]
  view: ResponseView
  showLeft: boolean
}>()

const showOneDb = defineModel<boolean>('showOneDb', { required: true })
const showThreeDb = defineModel<boolean>('showThreeDb', { required: true })

/** The curve these numbers describe — the same one the chart's markers are drawn for. */
const trace = computed<ResponseTrace | null>(() => {
  if (props.view === 'Gain') return props.traces.find((t) => t.kind === 'Gain') ?? null
  return (props.showLeft ? props.traces.find((t) => t.kind === 'Left') : null)
    ?? props.traces.find((t) => t.kind === 'Right')
    ?? props.traces.find((t) => t.kind === 'Left')
    ?? null
})

/**
 * The previous app re-captioned these with the view (`frmFrequencyResponse.cs:746-781`), and the
 * distinction is real: in the dBV view the peak is an output level, in the gain view it is a gain.
 */
const peakLabel = computed(() => (props.view === 'Gain' ? 'Max. gain' : 'Max. amplitude'))
const peakFreqLabel = computed(() => (props.view === 'Gain' ? 'Highest gain freq.' : 'Highest amplitude freq.'))
</script>

<template>
  <CollapsibleSection title="Measurements">
    <table v-if="trace" class="w-full text-left text-[11px] whitespace-nowrap">
      <thead class="text-neutral-500 dark:text-neutral-400">
        <tr>
          <th class="font-normal"></th>
          <th class="font-normal">f(low)</th>
          <th class="font-normal">f(high)</th>
          <th class="font-normal">BW</th>
          <th class="font-normal">Show</th>
        </tr>
      </thead>
      <tbody class="font-mono">
        <tr>
          <td class="pr-2 font-sans whitespace-nowrap">1 dB</td>
          <td class="pr-2">{{ formatCorner(trace.oneDb.lowerHz, trace.oneDb.lowerClamped, 'lower') }}</td>
          <td class="pr-2">{{ formatCorner(trace.oneDb.upperHz, trace.oneDb.upperClamped, 'upper') }}</td>
          <td class="pr-2">{{ formatBandwidth(trace.oneDb) }}</td>
          <td><input v-model="showOneDb" type="checkbox" /></td>
        </tr>
        <tr>
          <td class="pr-2 font-sans whitespace-nowrap">3 dB</td>
          <td class="pr-2">{{ formatCorner(trace.threeDb.lowerHz, trace.threeDb.lowerClamped, 'lower') }}</td>
          <td class="pr-2">{{ formatCorner(trace.threeDb.upperHz, trace.threeDb.upperClamped, 'upper') }}</td>
          <td class="pr-2">{{ formatBandwidth(trace.threeDb) }}</td>
          <td><input v-model="showThreeDb" type="checkbox" /></td>
        </tr>
      </tbody>
    </table>

    <dl v-if="trace" class="mt-2 grid grid-cols-[auto_1fr_auto] gap-x-3 gap-y-1 text-xs">
      <dt class="text-neutral-500 dark:text-neutral-400">{{ peakLabel }}</dt>
      <dd class="font-mono">{{ formatDb(trace.peakDb) }}</dd>
      <dd class="font-mono text-neutral-500 dark:text-neutral-400">{{ formatRatio(trace.peakDb) }}</dd>

      <dt class="text-neutral-500 dark:text-neutral-400">{{ peakFreqLabel }}</dt>
      <dd class="font-mono">{{ formatHz(trace.peakFreqHz) }}</dd>
      <dd></dd>
    </dl>

    <p
      v-if="trace && (trace.threeDb.lowerClamped || trace.threeDb.upperClamped)"
      class="mt-2 text-[11px] text-neutral-500 dark:text-neutral-400"
    >
      A corner marked &lt; or &gt; was never reached inside the measured band — that is where the
      search stopped, not where the response rolls off.
    </p>

    <p v-if="!trace" class="text-xs text-neutral-500 dark:text-neutral-400">
      Nothing measured yet.
    </p>
  </CollapsibleSection>
</template>
