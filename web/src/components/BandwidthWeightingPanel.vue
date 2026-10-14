<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import type { WeightingStandardName } from '../api/client'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { useUserWeighting } from '../composables/useUserWeighting'
import { WEIGHTING_OPTIONS } from '../weighting'
import CollapsibleSection from './CollapsibleSection.vue'
import FileBrowserDialog from './FileBrowserDialog.vue'

/**
 * The FFT screen's noise weighting, in a panel of its own (moved out of FFT settings on
 * 2026-09-21). It is not an FFT setting: the size, window and averaging decide how a record is
 * *transformed*, while this decides what the *numbers* read from it mean — the curve the noise is
 * weighed through. Applies live through the same useSpectrumConfig() as the FFT panel, and is
 * saved with the FFT measurement type.
 *
 * The measurement band left this panel on 2026-10-14 for the metrics picker (the Metrics button
 * above the chart), where it sits with the tiles it qualifies; the two are one setting either way
 * — see MetricPickerDialog.vue.
 */

const emit = defineEmits<{
  error: [message: string]
}>()

const { config, apply: applyConfig } = useSpectrumConfig()

const loaded = ref(false)
const form = reactive({
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

function syncFormFromConfig() {
  if (!config.value) return
  form.weighting = config.value.weighting?.standard ?? 'None'
  form.weightingUser1 = config.value.weighting?.user1 ?? false
  form.weightingUser2 = config.value.weighting?.user2 ?? false
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
  <CollapsibleSection title="Weighting" padding="p-4">
    <div v-if="loaded" class="flex flex-col gap-3 text-sm">
      <div class="flex flex-wrap items-end gap-3">
        <label
          class="tb-field"
          title="Weights the noise figures (THD+N, SNR, N-D, band RMS, noise density) and the displayed spectrum. The fundamental, harmonics, THD, gain and peak are never weighted."
        >
          Weighting
          <select
            v-model="form.weighting"
            class="tb-input"
            @change="applyWeighting"
          >
            <option v-for="w in WEIGHTING_OPTIONS" :key="w.value" :value="w.value">{{ w.label }}</option>
          </select>
        </label>
      </div>

      <!-- The two user tables, each a "frequency, dB" file (positive = attenuation, the QA40x
           format) the server reads. A checked slot combines with the standard curve; a checked
           empty slot weighs nothing, which the label says. -->
      <div class="tb-field" data-user-weighting>
        <div v-for="slot in ([1, 2] as const)" :key="slot" class="tb-check-row">
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
      remember-as="weighting"
      @close="weightingFileSlot = null"
      @select="importWeighting"
    />
  </CollapsibleSection>
</template>
