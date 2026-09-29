<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { RESPONSE_RESOLUTIONS, RESPONSE_SPANS_HZ } from '../api/client'
import { useImpulseResponse } from '../composables/useImpulseResponse'
import Modal from './Modal.vue'

/**
 * Captures the analyzer's own impulse response, so a later measurement can divide it out.
 *
 * <para>A dialog rather than a button because this step has a <b>physical precondition</b>: the
 * generator output has to be patched straight back to the analyzer input, with the DUT out of the
 * path. That instruction is the main thing this component exists to show — a tooltip would not be
 * read, and a calibration taken by accident through the DUT divides the DUT out of every later
 * measurement, silently and plausibly. The server checks for it and refuses, and that refusal is
 * reported here.</para>
 */
const props = defineProps<{
  open: boolean
  /** The run this calibration is being captured for, so its rate and band match. */
  defaults: { frequencySpanHz: number; resolutionMultiplier: number; chirpStartHz: number }
}>()

const emit = defineEmits<{ close: []; captured: [id: number] }>()

const { references, capturingReference, captureReference, deleteReference, refreshReferences } = useImpulseResponse()

/** A calibration divided into every later measurement has to be quieter than they are. Mirrors `ImpulseReferenceCaptureRequest.MinAverageCount`. */
const MIN_AVERAGES = 4

const form = reactive({
  name: 'Bench loopback',
  frequencySpanHz: 96000 as number,
  resolutionMultiplier: 1,
  generatorLevelDbv: -20,
  averageCount: MIN_AVERAGES,
  chirpStartHz: 2,
})

const error = ref<string | null>(null)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    error.value = null
    form.frequencySpanHz = props.defaults.frequencySpanHz
    form.resolutionMultiplier = props.defaults.resolutionMultiplier
    form.chirpStartHz = props.defaults.chirpStartHz
    void refreshReferences().catch(() => undefined)
  },
  { immediate: true },
)

async function capture() {
  error.value = null
  try {
    const info = await captureReference({
      name: form.name.trim(),
      frequencySpanHz: form.frequencySpanHz,
      resolutionMultiplier: form.resolutionMultiplier,
      generatorLevelDbv: form.generatorLevelDbv,
      averageCount: Math.max(MIN_AVERAGES, form.averageCount),
      chirpStartHz: form.chirpStartHz,
    })
    emit('captured', info.id)
    emit('close')
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}

async function remove(id: number) {
  error.value = null
  try {
    await deleteReference(id)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}
</script>

<template>
  <Modal :open="open" title="Capture a loopback calibration" :close-on-backdrop="!capturingReference" @close="emit('close')">
    <div class="flex flex-col gap-3 text-xs">
      <p class="rounded border border-amber-300 bg-amber-50 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:bg-amber-950/40 dark:text-amber-200">
        <strong>Patch the generator output straight to the analyzer input</strong>, with the device
        under test out of the path. This measures the analyzer and its cables so a later run can
        divide them out — captured through a DUT, it would divide the DUT out instead.
      </p>

      <label class="flex flex-col gap-1">
        Name
        <input
          v-model="form.name"
          type="text"
          :disabled="capturingReference"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        />
      </label>

      <div class="grid grid-cols-2 gap-2">
        <label class="flex flex-col gap-1">
          Frequency span (Hz)
          <select
            v-model.number="form.frequencySpanHz"
            :disabled="capturingReference"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option v-for="span in RESPONSE_SPANS_HZ" :key="span" :value="span">{{ span }}</option>
          </select>
        </label>
        <label class="flex flex-col gap-1">
          Resolution (Hz)
          <select
            v-model.number="form.resolutionMultiplier"
            :disabled="capturingReference"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option v-for="option in RESPONSE_RESOLUTIONS" :key="option.multiplier" :value="option.multiplier">
              {{ option.label }}
            </option>
          </select>
        </label>
      </div>

      <div class="grid grid-cols-2 gap-2">
        <label class="flex flex-col gap-1" title="The generator level to calibrate at. Low enough not to clip the input, high enough to be well above the noise.">
          Generator (dBV)
          <input
            v-model.number="form.generatorLevelDbv"
            type="number"
            step="1"
            :disabled="capturingReference"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          />
        </label>
        <label class="flex flex-col gap-1" :title="`At least ${MIN_AVERAGES}: this is divided into every measurement that uses it, so its own noise lands in all of them.`">
          Averages
          <input
            v-model.number="form.averageCount"
            type="number"
            :min="MIN_AVERAGES"
            max="10"
            :disabled="capturingReference"
            class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          />
        </label>
      </div>

      <p class="text-[11px] text-neutral-500 dark:text-neutral-400">
        A calibration is tied to its sample rate — {{ (form.frequencySpanHz * 2) / 1000 }} kHz here — and cannot be
        used at another one.
      </p>

      <p v-if="error" class="rounded border border-red-300 bg-red-50 px-3 py-2 text-[11px] text-red-700 dark:border-red-800 dark:bg-red-950/40 dark:text-red-300">
        {{ error }}
      </p>

      <div class="flex items-center justify-end gap-2">
        <button
          type="button"
          :disabled="capturingReference"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-100 disabled:opacity-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="emit('close')"
        >
          Cancel
        </button>
        <button
          type="button"
          :disabled="capturingReference || form.name.trim().length === 0"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-50 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-neutral-300"
          @click="capture"
        >
          {{ capturingReference ? 'Capturing…' : 'Capture' }}
        </button>
      </div>

      <div v-if="references.length > 0" class="border-t border-neutral-200 pt-2 dark:border-neutral-800">
        <p class="mb-1 text-[11px] text-neutral-500 dark:text-neutral-400">Stored calibrations</p>
        <ul class="flex flex-col gap-1">
          <li v-for="reference in references" :key="reference.id" class="flex items-center justify-between gap-2">
            <span>
              {{ reference.name }}
              <span class="text-neutral-500 dark:text-neutral-400">
                — {{ (reference.sampleRateHz / 1000).toFixed(0) }} kHz, {{ reference.irSamples }} samples
              </span>
            </span>
            <button
              type="button"
              :disabled="capturingReference"
              class="text-neutral-500 hover:text-red-600 disabled:opacity-50 dark:hover:text-red-400"
              :aria-label="`Delete ${reference.name}`"
              @click="remove(reference.id)"
            >
              ✕
            </button>
          </li>
        </ul>
      </div>
    </div>
  </Modal>
</template>
