<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import type { WeightingStandardName } from '../api/client'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { useUserWeighting } from '../composables/useUserWeighting'
import { BANDWIDTH_PRESETS, bandwidthPreset, bandwidthPresetValues, WEIGHTING_OPTIONS, type BandwidthPreset } from '../weighting'
import CollapsibleSection from './CollapsibleSection.vue'
import FileBrowserDialog from './FileBrowserDialog.vue'

/**
 * The FFT screen's measurement band and noise weighting, in a panel of their own (moved out of
 * FFT settings on 2026-09-21). Neither is an FFT setting: the size, window and averaging decide
 * how a record is *transformed*, while these two decide what the *numbers* read from it mean —
 * the band THD+N, SNR, the noise floor, band RMS and density are integrated over (and whose upper
 * edge bounds which harmonics and IMD products count), and the curve the noise is weighed through.
 * Both still apply live through the same useSpectrumConfig() as the FFT panel, and both are saved
 * with the FFT measurement type as before; only the place on screen changed.
 */

const emit = defineEmits<{
  error: [message: string]
}>()

const { config, apply: applyConfig } = useSpectrumConfig()

const loaded = ref(false)
const form = reactive({
  // The measurement band as a preset; Custom is whatever Configuration ▸ Measurement holds, and
  // choosing it changes nothing -- the values are edited there.
  bandwidth: 'audio' as BandwidthPreset,
  weighting: 'None' as WeightingStandardName,
  weightingUser1: false,
  weightingUser2: false,
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
    // No prefix to strip any more -- an ApiError's message is the server's own sentence; see
    // api/client.ts's apiErrorMessage.
    weightingImportError.value = (e as Error).message
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

function syncFormFromConfig() {
  if (!config.value) return
  form.weighting = config.value.weighting?.standard ?? 'None'
  form.weightingUser1 = config.value.weighting?.user1 ?? false
  form.weightingUser2 = config.value.weighting?.user2 ?? false
  form.bandwidth = bandwidthPreset(config.value.measurementLoFreqHz, config.value.measurementHiFreqHz)
  loaded.value = true
}

watch(config, syncFormFromConfig, { immediate: true })

onMounted(() => {
  // The config itself is loaded by the FFT settings panel (one shared fetch); this only needs the
  // user tables, and a mount before the config arrives is covered by the watcher above.
  void userWeighting.load()
})
</script>

<template>
  <CollapsibleSection title="Bandwidth & weighting" padding="p-4">
    <div v-if="loaded" class="flex flex-col gap-3 text-sm">
      <div class="flex flex-wrap items-end gap-3">
        <label
          class="flex flex-col gap-1"
          title="The measurement band THD+N, SNR, the noise floor, the band RMS and the noise density are integrated over; its upper edge also bounds which harmonics count toward THD. Custom is the band set in Configuration ▸ Measurement."
        >
          Bandwidth
          <select
            v-model="form.bandwidth"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
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
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
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
