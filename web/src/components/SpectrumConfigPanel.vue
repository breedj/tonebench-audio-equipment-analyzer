<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import type { AveragingMode, DeviceRanges, WindowType } from '../api/client'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { useProjects } from '../composables/useProjects'
import { useStagedSampleRate } from '../composables/useStagedSampleRate'
import CollapsibleSection from './CollapsibleSection.vue'

const props = defineProps<{
  ranges: DeviceRanges | null
}>()

const emit = defineEmits<{
  error: [message: string]
  /** Sample rate changed and something's live — App.vue restarts acquisition with it, same as any other live-apply edit. */
  'sample-rate-change': []
}>()

const { config, error: configError, load, apply: applyConfig } = useSpectrumConfig()
const { loadedParams, markDirty } = useProjects()

const FFT_SIZES = [4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576]

const WINDOWS: { value: WindowType; label: string }[] = [
  { value: 'Rectangular', label: 'Rectangular' },
  { value: 'Hann', label: 'Hann' },
  { value: 'Hamming', label: 'Hamming' },
  { value: 'BlackmanHarris', label: 'Blackman-Harris' },
  // Kaiser alpha=5 and alpha=7 (beta = pi*alpha, ARTA's naming): 2.28 and 2.68 bins of ENBW at
  // -120 and -171 dB of sidelobe suppression, i.e. the two rungs between Blackman-Harris and
  // Flat Top. Listed in ENBW order with the rest of the tail.
  { value: 'Kaiser5', label: 'Kaiser 5' },
  { value: 'Kaiser7', label: 'Kaiser 7' },
  { value: 'FlatTop', label: 'Flat Top' },
]

const AVERAGING_MODES: { value: AveragingMode; label: string }[] = [
  { value: 'Off', label: 'Off' },
  { value: 'Linear', label: 'Linear' },
  { value: 'Exponential', label: 'Exponential' },
]

const loaded = ref(false)
const form = reactive({
  fftSize: 65536,
  window: 'Hann' as WindowType,
  averaging: 'Linear' as AveragingMode,
  averageCount: 8,
  // The measurement band and the noise weighting used to be here too; they are
  // BandwidthWeightingPanel's now, since neither says how a record is transformed.
})

// Sample rate — formerly Acquisition's own field — is staged from the measurement type's params
// (loadedParams), not the server-side spectrum config those other fields above use; it only takes
// effect on Run (or immediately, if something's already live — see onSampleRateChange).
//
// Held in a module singleton rather than a local ref so GeneratorPanel can watch it: a rate this
// analyzer captures at but cannot play (384 kHz on a QA403) takes that panel out of service — see
// useStagedSampleRate.ts and generatorRateCeiling.ts.
const { stagedSampleRateHz: sampleRateHz } = useStagedSampleRate()
watch(
  loadedParams,
  (params) => {
    if (!params) return
    sampleRateHz.value = params.sampleRateHz
  },
  { immediate: true },
)

function onSampleRateChange() {
  markDirty()
  emit('sample-rate-change')
}

// Which channel(s) get processed/displayed — moved here from Acquisition, which otherwise had
// nothing left in it. Already server-persisted via useSpectrumConfig, same as FFT size/window/etc.
const processLeft = computed({
  get: () => config.value?.processLeft ?? true,
  set: (value: boolean) => applyConfig({ processLeft: value }),
})
const processRight = computed({
  get: () => config.value?.processRight ?? false,
  set: (value: boolean) => applyConfig({ processRight: value }),
})

function syncFormFromConfig() {
  if (!config.value) return
  form.fftSize = config.value.fftSize
  form.window = config.value.window
  form.averaging = config.value.averaging
  form.averageCount = config.value.averageCount
  // Frame rate is no longer user-editable — always 20, regardless of what a measurement's stored
  // config says (see the removed "Frame rate (fps)" field below).
  loaded.value = true
}

watch(config, syncFormFromConfig, { immediate: true })
watch(configError, (message) => {
  if (message) emit('error', message)
})

async function apply() {
  try {
    await applyConfig({ fftSize: form.fftSize, window: form.window, averaging: form.averaging, averageCount: form.averageCount })
  } catch (e) {
    emit('error', (e as Error).message)
  }
}

/** The currently staged sample rate, for App.vue's Run handler and measurement-type save — see App.vue's buildStagedConfig. */
function getSampleRateHz(): number {
  return sampleRateHz.value
}

onMounted(() => {
  load()
})

defineExpose({ getSampleRateHz })
</script>

<template>
  <CollapsibleSection title="FFT settings" padding="p-4">
    <div v-if="loaded" class="flex flex-col gap-3 text-sm">
      <fieldset class="flex items-center gap-2" title="Which channel(s) to process and display, and which the Run button's generators can actually play — the ADC always samples both.">
        <legend class="sr-only">Channels</legend>
        <label class="flex items-center gap-1">
          <input type="checkbox" v-model="processLeft" />
          Left
        </label>
        <label class="flex items-center gap-1">
          <input type="checkbox" v-model="processRight" />
          Right
        </label>
      </fieldset>

      <div class="flex flex-wrap items-end gap-3">
        <label class="tb-field">
          Sample rate
          <select
            v-model.number="sampleRateHz"
            class="tb-input"
            @change="onSampleRateChange"
          >
            <option v-for="fs in props.ranges?.sampleRatesHz" :key="fs" :value="fs">{{ fs }} Hz</option>
          </select>
        </label>

        <label class="tb-field">
          FFT size
          <select
            v-model.number="form.fftSize"
            class="tb-input"
            @change="apply"
          >
            <option v-for="n in FFT_SIZES" :key="n" :value="n">{{ n }}</option>
          </select>
        </label>

        <label class="tb-field">
          Window
          <select
            v-model="form.window"
            class="tb-input"
            @change="apply"
          >
            <option v-for="w in WINDOWS" :key="w.value" :value="w.value">{{ w.label }}</option>
          </select>
        </label>

        <!-- One non-wrapping row, so the count always sits beside the mode it belongs to rather
             than wrapping under it when the rail is narrow. -->
        <div class="grid grid-cols-[2fr_1fr] items-end gap-2.5">
          <label class="tb-field">
            Averaging
            <select
              v-model="form.averaging"
              class="tb-input"
              @change="apply"
            >
              <option v-for="m in AVERAGING_MODES" :key="m.value" :value="m.value">{{ m.label }}</option>
            </select>
          </label>

          <label
            v-if="form.averaging !== 'Off'"
            class="tb-field"
            :title="form.averaging === 'Linear' ? 'How many records are averaged before the display holds.' : 'Time constant of the exponential average, in records.'"
          >
            Averages
            <input
              type="number"
              min="1"
              v-model.number="form.averageCount"
              class="w-12 rounded border border-neutral-300 px-1.5 py-1 dark:border-neutral-700"
              @change="apply"
            />
          </label>
        </div>
      </div>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Loading…</div>
  </CollapsibleSection>
</template>
