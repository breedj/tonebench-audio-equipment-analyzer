<script setup lang="ts">
import { computed } from 'vue'
import type { ResponseView } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { formatBandwidth, formatCorner, formatDb, formatHz, formatRatio } from '../response/format'
import { peakLabel as peakLabelFor, primaryResponseTrace, referenceLabel as referenceLabelFor } from '../response/measurements'
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

// The curve, the captions and the reference wording all come from response/measurements.ts, which
// the PDF report reads too — so the paper says exactly what this panel says.
const trace = computed<ResponseTrace | null>(() => primaryResponseTrace(props.traces, props.view, props.showLeft))
const peakLabel = computed(() => peakLabelFor(props.view))
const peakFreqLabel = 'Peak at'
const referenceLabel = computed(() => (trace.value ? referenceLabelFor(trace.value) : null))
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
