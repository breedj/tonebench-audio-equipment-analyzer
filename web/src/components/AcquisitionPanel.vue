<script setup lang="ts">
import { computed, reactive } from 'vue'
import type { DeviceRanges, DeviceStatus } from '../api/client'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'

const props = defineProps<{
  status: DeviceStatus | null
  ranges: DeviceRanges | null
}>()

const emit = defineEmits<{
  start: [sampleRateHz: number, channelCount: number, blockFrameCount: number]
  stop: []
}>()

const { config, apply } = useSpectrumConfig()

const form = reactive({
  sampleRateHz: 48000,
  blockFrameCount: 4096,
})

function start() {
  // The device always samples every channel the hardware has (a stereo ADC can't sample only
  // one) — the L/R checkboxes below only decide which channel(s) get processed/displayed, via
  // SpectrumPipelineOptions.ProcessLeft/Right, not how many channels are acquired.
  emit('start', form.sampleRateHz, props.ranges?.maxChannelCount ?? 2, form.blockFrameCount)
}

// Sample rate / block size are only meaningful once acquisition is actually running; before that
// they're just staged for the next Start click. While acquiring, restarting is exactly what
// DeviceSessionService.StartAcquisitionAsync now does in place when the config changed — the UI
// never has to Stop first.
function applyLive() {
  if (props.status?.acquiring) start()
}

const processLeft = computed({
  get: () => config.value?.processLeft ?? true,
  set: (value: boolean) => apply({ processLeft: value }),
})

const processRight = computed({
  get: () => config.value?.processRight ?? true,
  set: (value: boolean) => apply({ processRight: value }),
})

// Exposed so App.vue can read the currently-selected (not-yet-started) sample rate/block size
// when applying a generator change — the generator needs these to bring up a stream on its own
// when no acquisition is running yet, and this form is the one place the user sets them.
defineExpose({ form })
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-4 dark:border-neutral-800">
    <h2 class="mb-3 text-sm font-semibold text-neutral-900 dark:text-neutral-100">Acquisition</h2>

    <div class="flex flex-wrap items-center gap-3 text-sm">
      <label class="flex items-center gap-2">
        Sample rate
        <select
          v-model.number="form.sampleRateHz"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="applyLive"
        >
          <option v-for="fs in props.ranges?.sampleRatesHz" :key="fs" :value="fs">{{ fs }} Hz</option>
        </select>
      </label>

      <fieldset class="flex items-center gap-2" title="Which channel(s) to process and display — the ADC always samples both.">
        <legend class="sr-only">Channels</legend>
        <label class="flex items-center gap-1">
          <input type="checkbox" v-model="processLeft" />
          L
        </label>
        <label class="flex items-center gap-1">
          <input type="checkbox" v-model="processRight" />
          R
        </label>
      </fieldset>

      <label class="flex items-center gap-2" title="Minimum 1024 — the real QA403's hardware queue depth; smaller values spin the acquisition loop near-continuously.">
        Block size
        <input
          type="number"
          min="1024"
          v-model.number="form.blockFrameCount"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="applyLive"
        />
      </label>

      <button
        type="button"
        class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
        :disabled="props.status?.acquiring"
        @click="start"
      >
        Start
      </button>
      <button
        type="button"
        class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
        :disabled="!props.status?.acquiring"
        @click="emit('stop')"
      >
        Stop
      </button>
    </div>
  </section>
</template>
