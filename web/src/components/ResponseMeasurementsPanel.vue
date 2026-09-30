<script setup lang="ts">
import { computed } from 'vue'
import type { ResponsePhaseKindName, ResponseView } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { useBandwidthReference } from '../composables/useBandwidthReference'
import { formatBandwidth, formatCorner, formatDb, formatDelay, formatHz, formatRatio } from '../response/format'
import {
  delayReadings,
  delayResponseTrace,
  peakLabel as peakLabelFor,
  primaryResponseTrace,
  referenceLabel as referenceLabelFor,
} from '../response/measurements'
import BandwidthReferenceControls from './BandwidthReferenceControls.vue'
import CollapsibleSection from './CollapsibleSection.vue'

const props = defineProps<{
  traces: ResponseTrace[]
  view: ResponseView
  showLeft: boolean
  /** What phase the run measured — decides which curve the delay rows are read from, as on the chart. */
  phaseKind: ResponsePhaseKindName
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

// The delay rows read the curve the chart draws its delay from, at the same band the corners and
// the drive target use -- so "at reference" means the same place in every row of this panel. The
// band comes from the shared store because the trace carries only the mode that was applied.
const { bandwidthReferenceLowHz, bandwidthReferenceHighHz } = useBandwidthReference()
const delayTrace = computed(() => delayResponseTrace(props.traces, props.view, props.showLeft, props.phaseKind))
const delays = computed(() =>
  delayTrace.value
    ? delayReadings(delayTrace.value, { lowHz: bandwidthReferenceLowHz.value, highHz: bandwidthReferenceHighHz.value })
    : null,
)
const delaySuffix = computed(() => (props.phaseKind === 'ChannelDifference' ? ' diff.' : ''))
</script>

<template>
  <CollapsibleSection title="Measurements">
    <!-- The reference the corners below are measured down from, above them: changing it re-finds
         the corners on the curves already on screen (see BandwidthReferenceControls). -->
    <BandwidthReferenceControls :stepped="stepped" />

    <!-- Half a panel wide, not the whole panel and not its own content: this moved into the chart
         column on 2026-09-27, and a full-width table there spread five short columns across 700 px,
         with the ±1 dB label and its figures a hand's width apart. Content-sized was the answer to
         that and read as cramped (Joost, 2026-09-30), so both blocks here are now `w-full` under a
         24rem cap — about half again the width they were, with the slack shared out between the
         columns rather than dumped into one. The cap is what keeps that safe at every width: under
         it the block simply takes the panel, so a narrow column (the two-wide flow under the chart
         at lg) shrinks it back to content instead of overflowing, which `whitespace-nowrap` would
         otherwise turn into a horizontal scrollbar. -->
    <table v-if="trace" class="w-full max-w-96 text-left text-[11px] whitespace-nowrap">
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

    <!-- The same 24rem as the table above, so the two blocks line up on both edges. The slack goes
         into the *label* column (`1fr`, the value columns `auto`), which puts every figure on the
         block's right-hand side under the one above it — spread across three `auto` tracks instead,
         the columns would land wherever the widest row put them. The label column is also the one
         that gives when the panel is narrow: a value split over two lines ("4.41" / "dB") reads as
         two numbers, a label split over two does not. -->
    <dl v-if="trace" class="mt-2 grid w-full max-w-96 grid-cols-[1fr_auto_auto] gap-x-4 gap-y-1 text-xs">
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

      <!-- Delay, from the curve the chart's delay series is drawn from: at the reference band (or
           the peak, under a peak reference), then the extremes over the whole measured band with
           the frequency each sits at. Dashes for a stored result from before the delays existed. -->
      <template v-if="delays">
        <dt class="text-neutral-500 dark:text-neutral-400" title="Group delay, −dφ/df, as the median over the reference band — the same band the corners are measured from. Under a peak reference, at the peak.">
          Group delay{{ delaySuffix }} @ ref
        </dt>
        <dd class="whitespace-nowrap font-mono">{{ formatDelay(delays.groupDelayAtReferenceS) }}</dd>
        <dd></dd>

        <dt class="text-neutral-500 dark:text-neutral-400" title="Phase delay, −φ/(360·f), over the same band. Referenced so that the phase at the band sits inside ±180°, so an inverting DUT reads half a period.">
          Phase delay{{ delaySuffix }} @ ref
        </dt>
        <dd class="whitespace-nowrap font-mono">{{ formatDelay(delays.phaseDelayAtReferenceS) }}</dd>
        <dd></dd>

        <dt class="text-neutral-500 dark:text-neutral-400" title="The smallest group delay anywhere in the measured band, and where.">Group delay min</dt>
        <dd class="whitespace-nowrap font-mono">{{ formatDelay(delays.groupDelayMin?.s) }}</dd>
        <dd class="whitespace-nowrap font-mono text-neutral-500 dark:text-neutral-400">{{ delays.groupDelayMin ? formatHz(delays.groupDelayMin.freqHz) : '' }}</dd>

        <dt class="text-neutral-500 dark:text-neutral-400" title="The largest group delay anywhere in the measured band, and where — on an amplifier, usually at the low-frequency corner.">Group delay max</dt>
        <dd class="whitespace-nowrap font-mono">{{ formatDelay(delays.groupDelayMax?.s) }}</dd>
        <dd class="whitespace-nowrap font-mono text-neutral-500 dark:text-neutral-400">{{ delays.groupDelayMax ? formatHz(delays.groupDelayMax.freqHz) : '' }}</dd>
      </template>
    </dl>

    <p
      v-if="delays && !delays.recorded"
      class="mt-2 text-[11px] text-neutral-500 dark:text-neutral-400"
    >
      Delay was not recorded for this stored result; measure it again to get the delay curves.
    </p>

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
