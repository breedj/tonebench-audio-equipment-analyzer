<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import type { AveragingMode, DeviceRanges, WeightingStandardName, WindowType } from '../api/client'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { useProjects } from '../composables/useProjects'
import { useUserWeighting } from '../composables/useUserWeighting'
import { BANDWIDTH_PRESETS, bandwidthPreset, bandwidthPresetValues, WEIGHTING_OPTIONS, type BandwidthPreset } from '../weighting'
import CollapsibleSection from './CollapsibleSection.vue'
import FileBrowserDialog from './FileBrowserDialog.vue'

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
  // The noise weighting (see weighting.ts) and the measurement band the noise figures integrate
  // over, as a preset; Custom is whatever Configuration ▸ Measurement holds, and choosing it changes
  // nothing -- the values are edited there.
  weighting: 'None' as WeightingStandardName,
  weightingUser1: false,
  weightingUser2: false,
  bandwidth: 'audio' as BandwidthPreset,
})

// The user weighting tables live on the server (the browser cannot read a local file); this shows
// what is loaded into each slot and lets a file be picked for the server to import.
const userWeighting = useUserWeighting()
const weightingFileSlot = ref<1 | 2 | null>(null)
const weightingImportError = ref<string | null>(null)

function slotLabel(slot: 1 | 2): string {
  return userWeighting.nameOf(slot) ?? 'no table'
}

async function importWeighting(path: string) {
  const slot = weightingFileSlot.value
  weightingFileSlot.value = null
  if (!slot) return
  weightingImportError.value = null
  try {
    await userWeighting.importFrom(slot, path)
  } catch (e) {
    weightingImportError.value = (e as Error).message.replace(/^POST \S+ failed: \d+ /, '')
  }
}

async function clearWeighting(slot: 1 | 2) {
  weightingImportError.value = null
  try {
    await userWeighting.clear(slot)
  } catch (e) {
    emit('error', (e as Error).message)
  }
}

async function applyWeighting() {
  try {
    await applyConfig({ weighting: { standard: form.weighting, user1: form.weightingUser1, user2: form.weightingUser2 } })
  } catch (e) {
    emit('error', (e as Error).message)
  }
}

async function applyBandwidth() {
  const values = bandwidthPresetValues(form.bandwidth)
  if (!values) return
  try {
    await applyConfig({ measurementLoFreqHz: values.loHz, measurementHiFreqHz: values.hiHz })
  } catch (e) {
    emit('error', (e as Error).message)
  }
}

// Sample rate — formerly Acquisition's own field — is staged from the measurement type's params
// (loadedParams), not the server-side spectrum config those other fields above use; it only takes
// effect on Run (or immediately, if something's already live — see onSampleRateChange).
const sampleRateHz = ref(192000)
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
  form.weighting = config.value.weighting?.standard ?? 'None'
  form.weightingUser1 = config.value.weighting?.user1 ?? false
  form.weightingUser2 = config.value.weighting?.user2 ?? false
  form.bandwidth = bandwidthPreset(config.value.measurementLoFreqHz, config.value.measurementHiFreqHz)
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
    // The weighting and the bandwidth have their own handlers; the four analysis fields go as they are.
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
  void userWeighting.load()
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
        <label class="flex flex-col gap-1">
          Sample rate
          <select
            v-model.number="sampleRateHz"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="onSampleRateChange"
          >
            <option v-for="fs in props.ranges?.sampleRatesHz" :key="fs" :value="fs">{{ fs }} Hz</option>
          </select>
        </label>

        <label class="flex flex-col gap-1">
          FFT size
          <select
            v-model.number="form.fftSize"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="apply"
          >
            <option v-for="n in FFT_SIZES" :key="n" :value="n">{{ n }}</option>
          </select>
        </label>

        <label class="flex flex-col gap-1">
          Window
          <select
            v-model="form.window"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="apply"
          >
            <option v-for="w in WINDOWS" :key="w.value" :value="w.value">{{ w.label }}</option>
          </select>
        </label>

        <!-- One non-wrapping row, so the count always sits beside the mode it belongs to rather
             than wrapping under it when the rail is narrow. -->
        <div class="flex items-end gap-2">
          <label class="flex flex-col gap-1">
            Averaging
            <select
              v-model="form.averaging"
              class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
              @change="apply"
            >
              <option v-for="m in AVERAGING_MODES" :key="m.value" :value="m.value">{{ m.label }}</option>
            </select>
          </label>

          <label
            v-if="form.averaging !== 'Off'"
            class="flex flex-col gap-1"
            :title="form.averaging === 'Linear' ? 'How many records are averaged before the display holds.' : 'Time constant of the exponential average, in records.'"
          >
            Averages
            <input
              type="number"
              min="1"
              v-model.number="form.averageCount"
              class="w-12 rounded border border-neutral-300 px-1.5 py-1 dark:border-neutral-700 dark:bg-neutral-800"
              @change="apply"
            />
          </label>
        </div>

        <label
          class="flex flex-col gap-1"
          title="The measurement band THD+N, SNR, the noise floor, the band RMS and the noise density are integrated over. Custom is the band set in Configuration ▸ Measurement."
        >
          Bandwidth
          <select
            v-model="form.bandwidth"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="applyBandwidth"
          >
            <option v-for="p in BANDWIDTH_PRESETS" :key="p.value" :value="p.value">{{ p.label }}</option>
            <option value="custom">Custom</option>
          </select>
        </label>

        <label
          class="flex flex-col gap-1"
          title="Weights the noise figures (THD+N, SNR, N-D, band RMS, noise density) and the displayed spectrum. The fundamental, harmonics, THD, gain and peak are never weighted."
        >
          Weighting
          <select
            v-model="form.weighting"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="applyWeighting"
          >
            <option v-for="w in WEIGHTING_OPTIONS" :key="w.value" :value="w.value">{{ w.label }}</option>
          </select>
        </label>
      </div>

      <!-- The two user tables, each a "frequency, dB" file (positive = attenuation, the QA40x
           format) the server reads. A checked slot combines with the standard curve; a checked
           empty slot weighs nothing, which the label says. -->
      <div class="flex flex-col gap-1 text-xs" data-user-weighting>
        <div v-for="slot in ([1, 2] as const)" :key="slot" class="flex items-center gap-1.5">
          <label class="flex items-center gap-1" :title="`Apply user weighting table ${slot} on top of the standard curve.`">
            <input
              type="checkbox"
              v-model="form[slot === 1 ? 'weightingUser1' : 'weightingUser2']"
              @change="applyWeighting"
            />
            User {{ slot }}
          </label>
          <span
            class="min-w-0 flex-1 truncate"
            :class="userWeighting.nameOf(slot) ? '' : 'text-neutral-400 dark:text-neutral-600'"
            :title="userWeighting.nameOf(slot) ? `${slotLabel(slot)} — ${(slot === 1 ? userWeighting.user1.value : userWeighting.user2.value)?.pointCount} points` : 'No table loaded. Pick a frequency, dB text file with the … button.'"
          >
            {{ slotLabel(slot) }}
          </span>
          <button
            type="button"
            class="rounded border border-neutral-300 px-1.5 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
            :title="`Load a weighting table into User ${slot} from a text file (frequency, dB per line; positive = attenuation).`"
            @click="weightingFileSlot = slot"
          >
            …
          </button>
          <button
            v-if="userWeighting.nameOf(slot)"
            type="button"
            class="rounded border border-neutral-300 px-1.5 py-0.5 text-neutral-500 hover:text-red-500 dark:border-neutral-700"
            :title="`Clear User ${slot}`"
            :aria-label="`Clear user weighting ${slot}`"
            @click="clearWeighting(slot)"
          >
            ✕
          </button>
        </div>
        <div v-if="weightingImportError" class="text-red-600 dark:text-red-400">{{ weightingImportError }}</div>
      </div>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Loading…</div>
    <FileBrowserDialog
      :open="weightingFileSlot !== null"
      mode="open"
      extension=".txt"
      :title="`Load weighting table into User ${weightingFileSlot ?? 1}`"
      @close="weightingFileSlot = null"
      @select="importWeighting"
    />
  </CollapsibleSection>
</template>
