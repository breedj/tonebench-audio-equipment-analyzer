<script setup lang="ts">
import { computed } from 'vue'
import type { IntermodulationFrame, MetersFrame, MultitoneFrame, NoiseFiguresFrame } from '../streaming/protocol'
import type { GeneratorTone } from '../api/client'
import { channelTiles, NOT_APPLICABLE, stimulusSummary, type MetricTile } from '../metrics'
import { useChartDisplay } from '../composables/useChartDisplay'
import { useGeneratorStimulus } from '../composables/useGeneratorStimulus'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useMetricSelection } from '../composables/useMetricSelection'

const props = defineProps<{
  channelLabel: 'L' | 'R'
  meters: MetersFrame | null
  generatorTones: GeneratorTone[]
  /** This channel's two-tone IMD figure, when the pipeline is measuring one — adds the IMD tiles. */
  imd?: IntermodulationFrame | null
  /** The pipeline has an IMD spec but could not resolve it at this FFT size — the tiles show a dash. */
  imdUnresolved?: boolean
  /** This channel's band RMS / noise density / weighting frame — adds the noise tiles and the weighting qualifier. */
  noise?: NoiseFiguresFrame | null
  /** This channel's multitone figure, while the generator plays a comb — adds the TD+N tiles. */
  multitone?: MultitoneFrame | null
}>()

// Shared with the other channel — see useLoadOhms.ts. Edited in the Generator pane, not here; the
// DUT gain likewise (useChartDisplay, beside Load Ω).
const { loadOhms } = useLoadOhms()
const { dutGainDb } = useChartDisplay()
const { selectedMetrics, removeMetric } = useMetricSelection()

// The *staged* stimulus, not DeviceStatus's generatorTones above: those are silenced by Stop (see
// useGeneratorStimulus.ts), and a SINGLE capture of a two-tone pair would then go back to printing
// its meaningless noise figures the moment the generator went quiet. This only decides whether the
// noise figures have a single fundamental to reference — every number still comes from the frames.
const { kind, tones, multitone: multitoneSettings } = useGeneratorStimulus()
const stimulus = computed(() => stimulusSummary(kind.value, tones, multitoneSettings))

const allTiles = computed(() =>
  channelTiles(
    props.meters, props.generatorTones, loadOhms.value, props.imd ?? null, props.imdUnresolved ?? false, props.noise ?? null, dutGainDb.value,
    props.multitone ?? null, stimulus.value,
  ),
)

// Selected fixed metrics, in the order they were picked — see metrics.ts's AVAILABLE_METRIC_LABELS.
// The generator summary is a separate, channel-independent tile now (see GeneratorMetricsPanel.vue).
const tiles = computed(() => {
  const byLabel = new Map(allTiles.value.map((t) => [t.label, t]))
  return selectedMetrics.value.map((label) => byLabel.get(label)).filter((t): t is MetricTile => t !== undefined)
})

// Same L/R hues as SpectrumChart.vue/OscilloscopeChart.vue's traces, so a tile visually ties back
// to "which channel" at a glance instead of every box reading identically.
const accentClass = computed(() =>
  props.channelLabel === 'L'
    ? 'border-l-4 border-l-blue-500 dark:border-l-blue-400'
    : 'border-l-4 border-l-orange-500 dark:border-l-orange-400',
)
</script>

<template>
  <div v-if="tiles.length" class="flex flex-wrap gap-3">
    <div
      v-for="tile in tiles"
      :key="tile.label"
      :class="accentClass"
      class="min-w-[9rem] rounded-xl border border-neutral-200 bg-white px-3 py-2 shadow-sm dark:border-neutral-800 dark:bg-neutral-900"
    >
      <div class="mb-1 flex items-center justify-between gap-3">
        <span class="text-xs text-neutral-500 dark:text-neutral-400">
          {{ channelLabel }}: {{ tile.label }}
          <span v-if="tile.qualifier" class="ml-1 rounded bg-amber-100 px-1 font-medium text-amber-800 dark:bg-amber-900/40 dark:text-amber-300" :title="`Weighted: ${tile.qualifier}`">{{ tile.qualifier }}</span>
        </span>
        <button
          type="button"
          class="shrink-0 text-neutral-400 hover:text-red-500 dark:text-neutral-600 dark:hover:text-red-400"
          title="Remove this metric"
          :aria-label="`Remove ${tile.label}`"
          @click="removeMetric(tile.label)"
        >
          <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" aria-hidden="true">
            <path d="M18 6 6 18M6 6l12 12" />
          </svg>
        </button>
      </div>
      <div
        v-if="tile.caveat"
        class="cursor-help font-mono text-lg italic text-neutral-400 underline decoration-dotted underline-offset-4 dark:text-neutral-500"
        :title="`Not applicable — ${tile.caveat}.`"
      >{{ NOT_APPLICABLE }}</div>
      <div v-else class="whitespace-pre-line font-mono text-lg">{{ tile.value }}</div>
    </div>
  </div>
  <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">
    {{ props.meters || props.noise ? 'No metrics selected — add some with the metrics button.' : 'Waiting for data…' }}
  </div>
</template>
