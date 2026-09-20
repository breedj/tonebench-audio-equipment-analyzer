<script setup lang="ts">
import { computed } from 'vue'
import type { IntermodulationFrame } from '../streaming/protocol'
import { ratioPercentToDb } from '../metrics'
import { imdProductLabel } from '../imd/products'
import CollapsibleSection from './CollapsibleSection.vue'

const props = defineProps<{
  imd: Record<number, IntermodulationFrame>
  channel: 0 | 1
  channelLabel: 'L' | 'R'
  /** True while the pipeline has a two-tone spec, so "no frame" can be told apart from "no IMD configured". */
  configured: boolean
  /** True when the pipeline said it cannot separate the tones at this FFT size — see STREAM_FLAG_IMD_UNRESOLVED. */
  unresolved?: boolean
}>()

const frame = computed(() => props.imd[props.channel] ?? null)

const rows = computed(() => {
  const f = frame.value
  if (!f) return []
  return [
    { key: 'f1', label: 'Tone f1', combination: '', freqHz: f.toneLowHz, dbv: f.toneLowDbv, dbc: null as number | null },
    { key: 'f2', label: 'Tone f2', combination: '', freqHz: f.toneHighHz, dbv: f.toneHighDbv, dbc: null as number | null },
    ...f.products.map((p, i) => ({
      key: `p${i}`,
      label: `IM${p.order}`,
      combination: imdProductLabel(f.method, p, f),
      freqHz: p.freqHz,
      dbv: p.dbv,
      dbc: p.dbc as number | null,
    })),
  ]
})

const reference = computed(() => {
  const f = frame.value
  if (!f) return ''
  return f.method === 'Smpte' ? `re ${formatHz(f.toneHighHz)} carrier` : 're RMS sum of both tones'
})

function formatHz(hz: number): string {
  return hz >= 1000 ? `${(hz / 1000).toFixed(hz % 1000 === 0 ? 0 : 2)} kHz` : `${hz.toFixed(1)} Hz`
}
</script>

<template>
  <CollapsibleSection :title="`${channelLabel}: Intermodulation`" size="xs">
    <div v-if="frame" class="overflow-x-auto">
      <table class="w-full text-left text-sm">
        <thead>
          <tr class="text-xs text-neutral-500 dark:text-neutral-400">
            <th class="py-1 pr-4 font-medium">Product</th>
            <th class="py-1 pr-4 font-medium">Frequency</th>
            <th class="py-1 pr-4 font-medium">Level</th>
            <th class="py-1 font-medium" :title="reference">dBc</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.key" class="border-t border-neutral-100 font-mono dark:border-neutral-800">
            <td class="py-1 pr-4">
              {{ row.label }}
              <span v-if="row.combination" class="text-xs text-neutral-400 dark:text-neutral-600">{{ row.combination }}</span>
            </td>
            <td class="py-1 pr-4">{{ row.freqHz.toFixed(1) }} Hz</td>
            <td class="py-1 pr-4">{{ row.dbv.toFixed(2) }} dBV</td>
            <td class="py-1">{{ row.dbc === null ? '—' : `${row.dbc.toFixed(1)} dB` }}</td>
          </tr>
        </tbody>
        <tfoot class="text-xs">
          <tr class="border-t border-neutral-200 font-mono dark:border-neutral-700">
            <td class="py-1 pr-4 font-sans font-medium">IMD ({{ frame.method === 'Smpte' ? 'SMPTE' : 'CCIF' }})</td>
            <td class="py-1 pr-4" colspan="2">{{ frame.imdPercent.toFixed(5) }} % · {{ ratioPercentToDb(frame.imdPercent).toFixed(2) }} dB</td>
            <td class="py-1 text-neutral-400 dark:text-neutral-600">{{ reference }}</td>
          </tr>
          <tr v-for="o in frame.perOrder" :key="o.order" class="font-mono">
            <td class="py-0.5 pr-4 font-sans">IM{{ o.order }}</td>
            <td class="py-0.5 pr-4" colspan="3">{{ o.percent.toFixed(5) }} % · {{ ratioPercentToDb(o.percent).toFixed(2) }} dB</td>
          </tr>
          <tr class="font-mono">
            <td class="py-0.5 pr-4 font-sans" title="The level of a single sine whose peak equals the pair's peak — what an amplifier's rating is quoted against.">Sine-eq. level</td>
            <td class="py-0.5 pr-4" colspan="3">{{ frame.sineEquivalentDbv.toFixed(2) }} dBV</td>
          </tr>
          <tr class="font-mono">
            <td class="py-0.5 pr-4 font-sans" title="The pair's true RMS, sqrt(V1² + V2²) — what a voltmeter reads.">Combined RMS</td>
            <td class="py-0.5 pr-4" colspan="3">{{ frame.combinedRmsDbv.toFixed(2) }} dBV</td>
          </tr>
        </tfoot>
      </table>
    </div>
    <div v-else-if="configured && unresolved" class="text-xs text-amber-600 dark:text-amber-400">
      The FFT is too small to separate the two tones (they need to be at least 8 bins apart) — increase the
      FFT size.
    </div>
    <div v-else-if="configured" class="text-xs text-neutral-400 dark:text-neutral-600">Waiting for data…</div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Set up exactly two sine tones (or pick an IMD preset) to measure intermodulation.</div>
  </CollapsibleSection>
</template>
