<script setup lang="ts">
import { computed } from 'vue'
import type { ResponseView } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { formatBandwidth, formatCorner, formatDb, formatHz, formatRatio } from '../response/format'
import BandwidthReferenceControls from './BandwidthReferenceControls.vue'
import CollapsibleSection from './CollapsibleSection.vue'

const props = defineProps<{
  traces: ResponseTrace[]
  view: ResponseView
  showLeft: boolean
  /** Which measurement the reference controls apply their change to. */
  stepped: boolean
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
const peakFreqLabel = 'Peak at'

/**
 * What the two rows were measured down from, spelled out — because the same table reads completely
 * differently depending on it, and a reader comparing two saved runs has no other way to tell.
 *
 * Absent (null) on a result saved before the reference was recorded. Those were peak-referenced, but
 * the row is left blank rather than back-filled: it says what the stored result says and no more.
 */
const referenceLabel = computed(() => {
  if (!trace.value || trace.value.referenceMode === null) return null
  if (trace.value.referenceMode === 'Peak') return 'Peak'
  return trace.value.referenceFellBackToPeak ? 'Peak (band not measured)' : 'Passband median'
})
</script>

<template>
  <CollapsibleSection title="Measurements">
    <!-- The reference the corners below are measured down from, above them: changing it re-finds
         the corners on the curves already on screen (see BandwidthReferenceControls). -->
    <BandwidthReferenceControls :stepped="stepped" />

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
          <td class="pr-2 font-sans whitespace-nowrap" title="Where the response stays within 1 dB of the reference, above it as well as below — a flatness figure. A downward-only search would walk straight up through a resonance without stopping.">
            ±1 dB
          </td>
          <td class="pr-2">{{ formatCorner(trace.oneDb.lowerHz, trace.oneDb.lowerClamped, 'lower') }}</td>
          <td class="pr-2">{{ formatCorner(trace.oneDb.upperHz, trace.oneDb.upperClamped, 'upper') }}</td>
          <td class="pr-2">{{ formatBandwidth(trace.oneDb) }}</td>
          <td><input v-model="showOneDb" type="checkbox" /></td>
        </tr>
        <tr>
          <td class="pr-2 font-sans whitespace-nowrap" title="Where the response has fallen 3 dB below the reference — a roll-off figure, so it is one-sided by convention and can lie beyond a resonance in the band.">
            −3 dB
          </td>
          <td class="pr-2">{{ formatCorner(trace.threeDb.lowerHz, trace.threeDb.lowerClamped, 'lower') }}</td>
          <td class="pr-2">{{ formatCorner(trace.threeDb.upperHz, trace.threeDb.upperClamped, 'upper') }}</td>
          <td class="pr-2">{{ formatBandwidth(trace.threeDb) }}</td>
          <td><input v-model="showThreeDb" type="checkbox" /></td>
        </tr>
      </tbody>
    </table>

    <!-- The label column is the one that gives: a value split over two lines ("4.41" / "dB") reads
         as two numbers, a label split over two does not. -->
    <dl v-if="trace" class="mt-2 grid grid-cols-[minmax(0,1fr)_auto_auto] gap-x-3 gap-y-1 text-xs">
      <dt class="text-neutral-500 dark:text-neutral-400">{{ peakLabel }}</dt>
      <dd class="whitespace-nowrap font-mono">{{ formatDb(trace.peakDb) }}</dd>
      <dd class="whitespace-nowrap font-mono text-neutral-500 dark:text-neutral-400">{{ formatRatio(trace.peakDb) }}</dd>

      <dt class="text-neutral-500 dark:text-neutral-400">{{ peakFreqLabel }}</dt>
      <dd class="whitespace-nowrap font-mono">{{ formatHz(trace.peakFreqHz) }}</dd>
      <dd></dd>

      <!-- One cell across both value columns: a mode name in the third column would set that
           column's width for every row and squeeze the labels above into wrapping. -->
      <template v-if="referenceLabel">
        <dt class="text-neutral-500 dark:text-neutral-400">Reference</dt>
        <dd class="col-span-2">
          <span class="whitespace-nowrap font-mono">{{ trace.referenceDb === null ? '—' : formatDb(trace.referenceDb) }}</span>
          <span class="ml-3 text-neutral-500 dark:text-neutral-400">{{ referenceLabel }}</span>
        </dd>
      </template>
    </dl>

    <p
      v-if="trace && trace.referenceFellBackToPeak"
      class="mt-2 text-[11px] text-amber-600 dark:text-amber-500"
    >
      The reference band lies outside the measured range, so these corners are referenced to the
      peak instead. Move the band inside the sweep, or set the reference to Peak deliberately.
    </p>

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
