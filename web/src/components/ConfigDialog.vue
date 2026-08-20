<script setup lang="ts">
import { reactive, watch } from 'vue'
import Modal from './Modal.vue'
import { api } from '../api/client'
import { useAppTheme, type ThemeMode } from '../composables/useAppTheme'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import { useDevice } from '../composables/useDevice'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const { themeMode } = useAppTheme()
const { captureDelayMs } = useCaptureDelay()
const { config, apply: applyConfig } = useSpectrumConfig()
const { status: deviceStatus } = useDevice()

// Staged locally like the bandwidth field below, applied on change. Only meaningful on a device
// with a hardware ranger (autoRangeStepDownDelayMs is null on the simulator, which has its own
// client-side ranger in App.vue instead) — the field is hidden in that case.
const autoRangeStepDownDelayMs = reactive({ value: 1000 })

watch(
  deviceStatus,
  (s) => {
    if (s?.autoRangeStepDownDelayMs != null) autoRangeStepDownDelayMs.value = s.autoRangeStepDownDelayMs
  },
  { immediate: true },
)

async function applyAutoRangeStepDownDelay() {
  deviceStatus.value = await api.setAutoRangeStepDownDelay(autoRangeStepDownDelayMs.value)
}

const THEME_OPTIONS: { value: ThemeMode; label: string }[] = [
  { value: 'Auto', label: 'Auto' },
  { value: 'Light', label: 'Light' },
  { value: 'Dark', label: 'Dark' },
]

// The band THD+N / SNR / noise floor are integrated over — global (applies to whatever is
// currently running, independent of which measurement type is selected), not part of any
// measurement type's saved params. Staged locally like SpectrumConfigPanel's own form, applied on
// change straight through the same live useSpectrumConfig() config this used to live next to.
const bandwidth = reactive({ loFreqHz: 20, hiFreqHz: 20000 })

watch(
  config,
  (c) => {
    if (!c) return
    bandwidth.loFreqHz = c.measurementLoFreqHz
    bandwidth.hiFreqHz = c.measurementHiFreqHz
  },
  { immediate: true },
)

async function applyBandwidth() {
  await applyConfig({ measurementLoFreqHz: bandwidth.loFreqHz, measurementHiFreqHz: bandwidth.hiFreqHz })
}
</script>

<template>
  <Modal :open="props.open" title="Configuration" :close-on-backdrop="false" @close="emit('close')">
    <div class="flex flex-col gap-5 text-sm">
      <section>
        <h3 class="mb-2 text-xs font-semibold text-neutral-900 dark:text-neutral-100">Display</h3>
        <label class="flex flex-col gap-1">
          Style
          <select
            v-model="themeMode"
            class="w-32 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          >
            <option v-for="o in THEME_OPTIONS" :key="o.value" :value="o.value">{{ o.label }}</option>
          </select>
        </label>
      </section>

      <section>
        <h3 class="mb-2 text-xs font-semibold text-neutral-900 dark:text-neutral-100">Acquisition</h3>
        <label
          class="flex flex-col gap-1"
          title="How long to wait, after Run or Single enables the generator, before capture actually starts. Lets the generator/output settle (e.g. through external gear) before the FFT starts averaging."
        >
          Capture delay (ms)
          <input
            type="number"
            min="0"
            step="10"
            v-model.number="captureDelayMs"
            class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          />
        </label>
      </section>

      <section>
        <h3 class="mb-2 text-xs font-semibold text-neutral-900 dark:text-neutral-100">Measurement</h3>
        <label
          class="flex flex-col gap-1"
          title="Bandwidth THD+N, SNR and the noise floor are measured over. Also keeps DC out of the noise integration. Applies globally, regardless of which measurement is selected."
        >
          Metrics bandwidth (Hz)
          <span class="flex items-center gap-1">
            <input
              type="number"
              min="0"
              v-model.number="bandwidth.loFreqHz"
              class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
              @change="applyBandwidth"
            />
            <span class="text-neutral-400">–</span>
            <input
              type="number"
              min="1"
              v-model.number="bandwidth.hiFreqHz"
              class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
              @change="applyBandwidth"
            />
          </span>
        </label>
      </section>

      <section v-if="deviceStatus?.autoRangeStepDownDelayMs != null">
        <h3 class="mb-2 text-xs font-semibold text-neutral-900 dark:text-neutral-100">Auto-ranging</h3>
        <label
          class="flex flex-col gap-1"
          title="How long a peak must stay under-range before the input range steps down to a more sensitive setting. Higher values ride out amplitude-modulated signals (e.g. two close tones beating) without chasing every dip; lower values react faster to a genuinely quieter signal."
        >
          Step-down delay (ms)
          <input
            type="number"
            min="0"
            step="100"
            v-model.number="autoRangeStepDownDelayMs.value"
            class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="applyAutoRangeStepDownDelay"
          />
        </label>
      </section>

      <div class="flex justify-end border-t border-neutral-200 pt-3 dark:border-neutral-800">
        <button
          type="button"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          @click="emit('close')"
        >
          Close
        </button>
      </div>
    </div>
  </Modal>
</template>
