<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import BandwidthField from './BandwidthField.vue'
import Modal from './Modal.vue'
import { AVAILABLE_METRIC_LABELS, GEN_METRIC_LABEL } from '../metrics'
import { useMetricSelection } from '../composables/useMetricSelection'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { bandwidthRangeError, DEFAULT_BANDWIDTH } from '../weighting'

// Gen first, separated from the per-channel catalogue below — it's the one entry here that isn't
// an L:/R: tile (see GEN_METRIC_LABEL), but it's staged/persisted through the same selection list.
const PICKER_LABELS = [GEN_METRIC_LABEL, ...AVAILABLE_METRIC_LABELS]

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const { selectedMetrics, setSelectedMetrics } = useMetricSelection()
const { config, apply: applyConfig } = useSpectrumConfig()

// Staged locally so Cancel discards without touching the shared selection — same "edit a local
// copy, commit on Apply" shape as SpectrumConfigPanel's form vs. useSpectrumConfig's live config.
const staged = reactive(new Set<string>())

/**
 * The measurement band, here rather than in the weighting panel or the Configuration dialog
 * (moved 2026-10-14) because it belongs to the metrics it qualifies: it is the band THD+N, SNR,
 * N+D, N-D, the band RMS and the two noise densities are integrated over, and its upper edge also
 * bounds which harmonics count toward THD and which IMD products are in band. It is a setting of
 * the open *measurement*, not of the bench — saved in the FFT type's params_json and re-applied
 * when that measurement is selected (MeasurementSaveService.ApplyAsync).
 *
 * Staged like the checkboxes and committed on Apply; BandwidthField owns the preset/Custom rule.
 */
const band = reactive({ loHz: DEFAULT_BANDWIDTH.loHz as number, hiHz: DEFAULT_BANDWIDTH.hiHz as number })
const bandError = ref<string | null>(null)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    staged.clear()
    for (const label of selectedMetrics.value) staged.add(label)
    // The live band, which is the open measurement's; 20 Hz – 20 kHz on a bench that has never
    // set one, since that is SpectrumPipelineOptions' own default.
    band.loHz = config.value?.measurementLoFreqHz ?? DEFAULT_BANDWIDTH.loHz
    band.hiHz = config.value?.measurementHiFreqHz ?? DEFAULT_BANDWIDTH.hiHz
    bandError.value = null
  },
)

function toggle(label: string, checked: boolean) {
  if (checked) staged.add(label)
  else staged.delete(label)
}

async function onApply() {
  const edges = { loHz: band.loHz, hiHz: band.hiHz }
  const error = bandwidthRangeError(edges.loHz, edges.hiHz)
  if (error) {
    bandError.value = error
    return
  }
  // Only when it actually moved: apply() marks the open measurement dirty, and pressing Apply
  // after changing nothing but a checkbox must not also claim the band was edited.
  if (edges.loHz !== config.value?.measurementLoFreqHz || edges.hiHz !== config.value?.measurementHiFreqHz) {
    try {
      await applyConfig({ measurementLoFreqHz: edges.loHz, measurementHiFreqHz: edges.hiHz })
    } catch (e) {
      bandError.value = (e as Error).message
      return
    }
  }
  setSelectedMetrics(PICKER_LABELS.filter((label) => staged.has(label)))
  emit('close')
}
</script>

<template>
  <Modal :open="props.open" title="Add or remove metrics" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <div class="flex flex-col gap-2">
        <div class="tb-rule">
          <span class="tb-group">Bandwidth</span>
        </div>
        <BandwidthField
          v-model:lo-hz="band.loHz"
          v-model:hi-hz="band.hiHz"
          label="Measurement band"
          class="max-w-xl"
          @change="bandError = null"
        />
        <p v-if="bandError" class="text-xs text-red-600 dark:text-red-400">{{ bandError }}</p>
      </div>

      <div class="tb-rule">
        <span class="tb-group">Tiles</span>
      </div>
      <div class="grid max-h-80 grid-cols-2 gap-x-4 gap-y-1.5 overflow-y-auto sm:grid-cols-3">
        <label v-for="label in PICKER_LABELS" :key="label" class="flex items-center gap-1.5">
          <input
            type="checkbox"
            class="h-4 w-4 rounded border-neutral-400 dark:border-neutral-600"
            :checked="staged.has(label)"
            @change="toggle(label, ($event.target as HTMLInputElement).checked)"
          />
          {{ label }}
        </label>
      </div>

      <div class="flex justify-end gap-2 border-t border-neutral-200 pt-3 dark:border-neutral-800">
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="emit('close')"
        >
          Cancel
        </button>
        <button
          type="button"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          @click="onApply"
        >
          Apply
        </button>
      </div>
    </div>
  </Modal>
</template>
