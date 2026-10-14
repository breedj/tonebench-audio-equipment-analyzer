<script setup lang="ts">
import { ref, watch } from 'vue'
import { BANDWIDTH_PRESETS, bandwidthPreset, bandwidthPresetValues, type BandwidthPreset } from '../weighting'

/**
 * The measurement band as one control: the two presets, plus **Custom**, which reveals the two
 * edges. Shared by the metrics picker (the FFT screen's band) and the three sweep panels, so a
 * band means the same thing and is spelled the same way wherever it is set.
 *
 * The band itself is the caller's state — these are the plain `loHz`/`hiHz` the caller stages,
 * applies or saves. Only the *preset* lives here, and it is deliberately not derived from the two
 * numbers on every render: the audio preset's edges **are** 20/20000, so a Custom choice whose
 * numbers happen to match a preset would snap the dropdown back and hide the boxes under the
 * user's hands. It is re-derived only when the band changes from outside (a measurement type's
 * stored settings loading, a dialog reopening) — which `fromHere` is what distinguishes.
 */

const props = defineProps<{
  loHz: number
  hiHz: number
  disabled?: boolean
  /** The caption above the dropdown. "Measurement band" in the metrics picker, "Bandwidth" where the panel is narrow. */
  label?: string
  /**
   * What the band does *here*, for the tooltip. There is no wording true of both callers: on the
   * FFT screen the band bounds the harmonics counted toward THD, and a sweep deliberately does the
   * opposite (ThdSweepRunner sets HarmonicMaxFreqHz to Nyquist, or every point above ~7 kHz would
   * read THD 0). So each caller says what is true of its own screen, and the default is the FFT's.
   */
  hint?: string
}>()

const emit = defineEmits<{
  'update:loHz': [value: number]
  'update:hiHz': [value: number]
  change: []
}>()

const DEFAULT_HINT =
  'The band THD+N, SNR, N+D, N-D, the band RMS and the noise density are integrated over, and what keeps DC out of that sum. Its upper edge also bounds which harmonics count toward THD and which intermodulation products are in band.'

const choice = ref<BandwidthPreset>(bandwidthPreset(props.loHz, props.hiHz))

// Set by this component's own edits, so the watcher below can tell them from a band arriving from
// outside. Both emits of a preset change land in one tick, and the watcher runs once per flush.
let fromHere = false

watch(
  () => [props.loHz, props.hiHz] as const,
  () => {
    if (fromHere) {
      fromHere = false
      return
    }
    choice.value = bandwidthPreset(props.loHz, props.hiHz)
  },
)

function onPresetChange() {
  // A preset carries its own edges into the boxes, so switching to Custom afterwards starts from
  // the band that is actually in force rather than from whatever was last typed.
  const values = bandwidthPresetValues(choice.value)
  if (!values) return
  fromHere = true
  emit('update:loHz', values.loHz)
  emit('update:hiHz', values.hiHz)
  emit('change')
}

function onEdge(which: 'lo' | 'hi', event: Event) {
  const value = (event.target as HTMLInputElement).valueAsNumber
  fromHere = true
  // Spelled out rather than emit(which === 'lo' ? ... ) : `emit` is an overload set, and a computed
  // event name matches neither overload -- which `vue-tsc -b`, the check `npm run build` runs,
  // rejects even though `vue-tsc --noEmit -p tsconfig.json` lets it through.
  if (which === 'lo') emit('update:loHz', value)
  else emit('update:hiHz', value)
  emit('change')
}
</script>

<template>
  <div class="flex flex-wrap items-end gap-3">
    <label
      class="tb-field min-w-[9rem] flex-1"
      :title="props.hint ?? DEFAULT_HINT"
    >
      {{ props.label ?? 'Bandwidth' }}
      <select v-model="choice" class="tb-input" :disabled="props.disabled" @change="onPresetChange">
        <option v-for="p in BANDWIDTH_PRESETS" :key="p.value" :value="p.value">{{ p.label }}</option>
        <option value="custom">Custom</option>
      </select>
    </label>
    <template v-if="choice === 'custom'">
      <label class="tb-field w-24" title="Lower edge of the measurement band, in Hz.">
        From (Hz)
        <input
          type="number"
          min="0"
          :value="props.loHz"
          :disabled="props.disabled"
          class="tb-input"
          @change="onEdge('lo', $event)"
        />
      </label>
      <label class="tb-field w-28" title="Upper edge of the measurement band, in Hz.">
        To (Hz)
        <input
          type="number"
          min="1"
          :value="props.hiHz"
          :disabled="props.disabled"
          class="tb-input"
          @change="onEdge('hi', $event)"
        />
      </label>
    </template>
  </div>
</template>
