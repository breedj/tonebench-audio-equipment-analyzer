<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import type { AveragingMode, WindowType } from '../api/client'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'

const emit = defineEmits<{ error: [message: string] }>()

const { config, error: configError, load, apply: applyConfig } = useSpectrumConfig()

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
  averaging: 'Off' as AveragingMode,
  averageCount: 8,
  targetFrameRateHz: 20,
  measurementLoFreqHz: 20,
  measurementHiFreqHz: 20000,
})

function syncFormFromConfig() {
  if (!config.value) return
  form.fftSize = config.value.fftSize
  form.window = config.value.window
  form.averaging = config.value.averaging
  form.averageCount = config.value.averageCount
  form.targetFrameRateHz = config.value.targetFrameRateHz
  form.measurementLoFreqHz = config.value.measurementLoFreqHz
  form.measurementHiFreqHz = config.value.measurementHiFreqHz
  loaded.value = true
}

watch(config, syncFormFromConfig, { immediate: true })
watch(configError, (message) => {
  if (message) emit('error', message)
})

async function apply() {
  try {
    await applyConfig({ ...form })
  } catch (e) {
    emit('error', (e as Error).message)
  }
}

onMounted(load)
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-4 dark:border-neutral-800">
    <h2 class="mb-3 text-sm font-semibold text-neutral-900 dark:text-neutral-100">Spectrum</h2>

    <div v-if="loaded" class="flex flex-wrap items-end gap-3 text-sm">
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

      <label v-if="form.averaging !== 'Off'" class="flex flex-col gap-1">
        {{ form.averaging === 'Linear' ? 'Spectra averaged' : 'Time constant' }}
        <input
          type="number"
          min="2"
          v-model.number="form.averageCount"
          class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="apply"
        />
      </label>

      <label class="flex flex-col gap-1">
        Frame rate (fps)
        <input
          type="number"
          min="1"
          max="60"
          v-model.number="form.targetFrameRateHz"
          class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="apply"
        />
      </label>

      <!--
        The band THD+N / SNR / noise floor are integrated over — a measurement setting, not a
        display one. 20 Hz - 20 kHz is the standard audio band; widening it to Nyquist includes
        ultrasonic noise and makes those figures read worse at higher sample rates.
      -->
      <label
        class="flex flex-col gap-1"
        title="Bandwidth THD+N, SNR and the noise floor are measured over. Also keeps DC out of the noise integration."
      >
        Meas. band (Hz)
        <span class="flex items-center gap-1">
          <input
            type="number"
            min="0"
            v-model.number="form.measurementLoFreqHz"
            class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="apply"
          />
          <span class="text-neutral-400">–</span>
          <input
            type="number"
            min="1"
            v-model.number="form.measurementHiFreqHz"
            class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="apply"
          />
        </span>
      </label>
    </div>
    <div v-else class="text-xs text-neutral-400 dark:text-neutral-600">Loading…</div>
  </section>
</template>
