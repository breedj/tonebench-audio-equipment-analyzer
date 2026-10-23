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
import ReadingValue from './ReadingValue.vue'
import { useAppTheme } from '../composables/useAppTheme'
import { bandwidthColor, type BandwidthMarker } from '../charts/bandwidthColors'

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

// The colour each band's row shows beside its name — the chart's own dashed markers, and the same
// swatch the display rail puts beside the matching checkbox.
const { isDark } = useAppTheme()
function bandwidthSwatch(marker: BandwidthMarker): string {
  return bandwidthColor(marker, isDark.value)
}
</script>

<template>
  <CollapsibleSection title="Measurements">
    <!-- The reference the corners below are measured down from, above them: changing it re-finds
         the corners on the curves already on screen (see BandwidthReferenceControls). -->
    <BandwidthReferenceControls :stepped="stepped" />

    <!-- Both blocks run the panel's full width, stacked, at 12.5 px over 11 px uppercase headers —
         Joost's 2026-10-12 layout. They were capped at 24rem and set side by side, which read as
         two half-width tables rather than one readout, and at `text-xs` the figures were smaller
         than the labels beside them. `tabular-nums` is what makes a column of numbers line up on
         the decimal point; without it the proportional digits wander. -->
    <div v-if="trace" class="text-[12.5px] tabular-nums">
      <div
        class="grid grid-cols-[1fr_1fr_1fr_1fr_44px] gap-2 border-b border-neutral-200 py-1.5 text-right text-[11px] font-semibold uppercase tracking-[0.04em] text-neutral-500 dark:border-neutral-800 dark:text-neutral-400"
      >
        <span class="text-left">Band</span>
        <span>f low</span>
        <span>f high</span>
        <span>Width</span>
        <span class="text-center">Plot</span>
      </div>
      <!-- Each row opens with the dashed swatch its markers are drawn in (charts/bandwidthColors.ts),
           so the table, the chart and the display rail name the same curve the same way. -->
      <div
        class="grid grid-cols-[1fr_1fr_1fr_1fr_44px] items-center gap-2 border-b border-neutral-100 py-1.5 text-right dark:border-neutral-800"
      >
        <span
          class="inline-flex items-center gap-2 text-left"
          title="Where the response stays within 1 dB of the reference, above it as well as below — a flatness figure. A downward-only search would walk straight up through a resonance without stopping."
        >
          <span class="w-3.5 shrink-0 border-t-2 border-dashed" :style="{ borderColor: bandwidthSwatch('oneDb') }"></span>
          ±1 dB
        </span>
        <ReadingValue :text="formatCorner(trace.oneDb.lowerHz, trace.oneDb.lowerClamped, 'lower')" />
        <ReadingValue :text="formatCorner(trace.oneDb.upperHz, trace.oneDb.upperClamped, 'upper')" />
        <ReadingValue class="font-medium" :text="formatBandwidth(trace.oneDb)" />
        <span class="flex justify-center">
          <input v-model="showOneDb" type="checkbox" class="tb-check" aria-label="Plot the ±1 dB markers" />
        </span>
      </div>
      <div class="grid grid-cols-[1fr_1fr_1fr_1fr_44px] items-center gap-2 py-1.5 text-right">
        <span
          class="inline-flex items-center gap-2 text-left"
          title="Where the response has fallen 3 dB below the reference — a roll-off figure, so it is one-sided by convention and can lie beyond a resonance in the band."
        >
          <span class="w-3.5 shrink-0 border-t-2 border-dashed" :style="{ borderColor: bandwidthSwatch('threeDb') }"></span>
          −3 dB
        </span>
        <ReadingValue :text="formatCorner(trace.threeDb.lowerHz, trace.threeDb.lowerClamped, 'lower')" />
        <ReadingValue :text="formatCorner(trace.threeDb.upperHz, trace.threeDb.upperClamped, 'upper')" />
        <ReadingValue class="font-medium" :text="formatBandwidth(trace.threeDb)" />
        <span class="flex justify-center">
          <input v-model="showThreeDb" type="checkbox" class="tb-check" aria-label="Plot the −3 dB markers" />
        </span>
      </div>
    </div>

    <!-- The readings. Three columns — what it is, what it reads, and the note that qualifies it (a
         linear ratio, the reference's own name, the frequency an extreme sits at) — each row on its
         own hairline, so a long label and a long note cannot run into each other.

         The first two columns are **capped** rather than fractional. As `1.2fr 1fr 1fr` the value
         was right-aligned at the end of its own third, so on a wide panel "Max. amplitude" and
         "8.99 dB" sat some 500 px apart and the eye had nothing to carry it across (reported
         2026-10-23). 10rem clears the longest label there is — "Group delay diff. @ ref", measured at 125 px
         against the column's 160 — and 6rem
         any figure, so the pair reads as one line while the note stays out at the right margin.
         A cap and not `max-content`: each row is its own grid, so a content-sized label column
         would be a different width per row and the figures would no longer line up with each
         other. `minmax(0,...)` is what still lets both shrink where this panel lands in the
         two-wide flow under the chart.

         The note is **left-aligned beside the value**, not pinned to the right margin, which is the
         same reading as the pair above it: right-aligned in a `1fr` column it sat alone at the far
         end of a wide panel, so "8.97 dB" and the "Peak" that qualifies it were a panel's width
         apart (Joost, 2026-10-23, the second half of the same report). -->
    <dl v-if="trace" class="mt-1 text-[12.5px] tabular-nums">
      <div
        class="grid grid-cols-[minmax(0,10rem)_minmax(0,6rem)_minmax(0,1fr)] gap-3 border-b border-neutral-100 py-1.5 dark:border-neutral-800"
      >
        <dt class="text-neutral-500 dark:text-neutral-400">{{ peakLabel }}</dt>
        <dd class="m-0 text-right"><ReadingValue :text="formatDb(trace.peakDb)" /></dd>
        <dd class="m-0 whitespace-nowrap text-neutral-500 dark:text-neutral-400">
          {{ formatRatio(trace.peakDb) }}
        </dd>
      </div>

      <div
        class="grid grid-cols-[minmax(0,10rem)_minmax(0,6rem)_minmax(0,1fr)] gap-3 border-b border-neutral-100 py-1.5 dark:border-neutral-800"
      >
        <dt class="text-neutral-500 dark:text-neutral-400">{{ peakFreqLabel }}</dt>
        <dd class="m-0 text-right"><ReadingValue :text="formatHz(trace.peakFreqHz)" /></dd>
        <dd class="m-0"></dd>
      </div>

      <div
        v-if="referenceLabel"
        class="grid grid-cols-[minmax(0,10rem)_minmax(0,6rem)_minmax(0,1fr)] gap-3 border-b border-neutral-100 py-1.5 dark:border-neutral-800"
      >
        <dt class="text-neutral-500 dark:text-neutral-400">Reference</dt>
        <dd class="m-0 text-right">
          <ReadingValue :text="trace.referenceDb === null ? '—' : formatDb(trace.referenceDb)" />
        </dd>
        <dd class="m-0 text-neutral-500 dark:text-neutral-400">{{ referenceLabel }}</dd>
      </div>

      <!-- Delay, from the curve the chart's delay series is drawn from: at the reference band (or
           the peak, under a peak reference), then the extremes over the whole measured band with
           the frequency each sits at. Dashes for a stored result from before the delays existed. -->
      <template v-if="delays">
        <div
          class="grid grid-cols-[minmax(0,10rem)_minmax(0,6rem)_minmax(0,1fr)] gap-3 border-b border-neutral-100 py-1.5 dark:border-neutral-800"
        >
          <dt
            class="text-neutral-500 dark:text-neutral-400"
            title="Group delay, −dφ/df, as the median over the reference band — the same band the corners are measured from. Under a peak reference, at the peak."
          >
            Group delay{{ delaySuffix }} @ ref
          </dt>
          <dd class="m-0 text-right"><ReadingValue :text="formatDelay(delays.groupDelayAtReferenceS)" /></dd>
          <dd class="m-0"></dd>
        </div>

        <div
          class="grid grid-cols-[minmax(0,10rem)_minmax(0,6rem)_minmax(0,1fr)] gap-3 border-b border-neutral-100 py-1.5 dark:border-neutral-800"
        >
          <dt
            class="text-neutral-500 dark:text-neutral-400"
            title="Phase delay, −φ/(360·f), over the same band. Referenced so that the phase at the band sits inside ±180°, so an inverting DUT reads half a period."
          >
            Phase delay{{ delaySuffix }} @ ref
          </dt>
          <dd class="m-0 text-right"><ReadingValue :text="formatDelay(delays.phaseDelayAtReferenceS)" /></dd>
          <dd class="m-0"></dd>
        </div>

        <div
          class="grid grid-cols-[minmax(0,10rem)_minmax(0,6rem)_minmax(0,1fr)] gap-3 border-b border-neutral-100 py-1.5 dark:border-neutral-800"
        >
          <dt class="text-neutral-500 dark:text-neutral-400" title="The smallest group delay anywhere in the measured band, and where.">
            Group delay min
          </dt>
          <dd class="m-0 text-right"><ReadingValue :text="formatDelay(delays.groupDelayMin?.s)" /></dd>
          <dd class="m-0 whitespace-nowrap text-neutral-500 dark:text-neutral-400">
            {{ delays.groupDelayMin ? `@ ${formatHz(delays.groupDelayMin.freqHz)}` : '' }}
          </dd>
        </div>

        <div class="grid grid-cols-[minmax(0,10rem)_minmax(0,6rem)_minmax(0,1fr)] gap-3 py-1.5">
          <dt
            class="text-neutral-500 dark:text-neutral-400"
            title="The largest group delay anywhere in the measured band, and where — on an amplifier, usually at the low-frequency corner."
          >
            Group delay max
          </dt>
          <dd class="m-0 text-right"><ReadingValue :text="formatDelay(delays.groupDelayMax?.s)" /></dd>
          <dd class="m-0 whitespace-nowrap text-neutral-500 dark:text-neutral-400">
            {{ delays.groupDelayMax ? `@ ${formatHz(delays.groupDelayMax.freqHz)}` : '' }}
          </dd>
        </div>
      </template>
    </dl>

    <p v-if="delays && !delays.recorded" class="text-[12px] text-neutral-500 dark:text-neutral-400">
      Delay was not recorded for this stored result; measure it again to get the delay curves.
    </p>

    <p v-if="trace && trace.referenceFellBackToPeak" class="text-[12px] text-amber-600 dark:text-amber-500">
      The reference band lies outside the measured range, so these corners are referenced to the
      peak instead. Move the band inside the sweep, or set the reference to Peak deliberately.
    </p>

    <p
      v-if="trace && (trace.threeDb.lowerClamped || trace.threeDb.upperClamped)"
      class="text-[12px] text-neutral-500 dark:text-neutral-400"
    >
      A corner marked &lt; or &gt; was never reached inside the measured band — that is where the
      search stopped, not where the response rolls off.
    </p>

    <p v-if="!trace" class="text-[12px] text-neutral-500 dark:text-neutral-400">Nothing measured yet.</p>
  </CollapsibleSection>
</template>
