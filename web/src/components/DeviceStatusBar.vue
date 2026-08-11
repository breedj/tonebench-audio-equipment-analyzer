<script setup lang="ts">
import type { DeviceStatus, SimulatedProfileRequest } from '../api/client'

defineProps<{
  status: DeviceStatus | null
  error: string | null
}>()

const emit = defineEmits<{
  setSimulatedProfile: [patch: SimulatedProfileRequest]
}>()

function onSimulatedNoiseChange(e: Event) {
  emit('setSimulatedProfile', { noiseEnabled: (e.target as HTMLInputElement).checked })
}

function onSimulatedHumChange(e: Event) {
  emit('setSimulatedProfile', { humEnabled: (e.target as HTMLInputElement).checked })
}
</script>

<template>
  <footer
    class="flex flex-wrap items-center gap-4 border-t border-neutral-200 bg-white px-4 py-2 text-xs dark:border-neutral-800 dark:bg-neutral-900"
  >
    <div class="flex items-center gap-2">
      <span class="h-2.5 w-2.5 rounded-full" :class="status?.connected ? 'bg-emerald-500' : 'bg-neutral-400'" />
      <span class="font-semibold text-neutral-900 dark:text-neutral-100">
        {{ status?.connected ? status.productName : 'Disconnected' }}
      </span>
      <span
        v-if="status?.isSimulated"
        class="rounded bg-amber-100 px-1.5 py-0.5 text-xs font-medium text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"
      >
        Simulated
      </span>
      <!-- Simulator-only: what the synthesized DUT adds on top of the generator's stimulus. -->
      <template v-if="status?.isSimulated">
        <label
          class="flex items-center gap-1.5 text-neutral-500 dark:text-neutral-400"
          title="Simulated DUT noise floor (white + 1/f). Turn off for a clean spectrum."
        >
          <input type="checkbox" :checked="status.simulatedNoiseEnabled ?? false" @change="onSimulatedNoiseChange" />
          Noise
        </label>
        <label
          class="flex items-center gap-1.5 text-neutral-500 dark:text-neutral-400"
          title="Simulated mains hum: 50 Hz plus two harmonics, each 6 dB down."
        >
          <input type="checkbox" :checked="status.simulatedHumEnabled ?? false" @change="onSimulatedHumChange" />
          Hum
        </label>
      </template>
      <span
        v-if="status?.acquiring"
        class="rounded bg-emerald-100 px-1.5 py-0.5 text-xs font-medium text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-300"
      >
        Acquiring
      </span>
    </div>

    <div v-if="status?.connected" class="flex items-center gap-3 text-neutral-500 dark:text-neutral-400">
      <span>S/N {{ status.serialNumber }}</span>
      <span>FW {{ status.firmwareVersion }}</span>
      <!--
        Only shown once it happens, because it should never happen. A dropped chunk splices the
        sample stream, which lifts the measured noise floor and smears tones — so this is a warning
        that the numbers on screen are wrong, not a throughput statistic.
      -->
      <span
        v-if="status.droppedRxChunks > 0"
        class="rounded bg-red-100 px-1.5 py-0.5 text-xs font-medium text-red-800 dark:bg-red-900/40 dark:text-red-300"
        title="RX chunks dropped because the host fell behind. Each one splices the sample stream, so noise-floor and THD+N readings taken since are unreliable."
      >
        {{ status.droppedRxChunks }} dropped
      </span>
    </div>

    <div class="ml-auto flex items-center gap-4">
      <!--
        Read-only: auto-ranging is always on (see AcquisitionPanel's former Auto checkbox, now
        removed) — either the device's own hardware ranger or, where that's not available (the
        simulator), App.vue's live peak-level monitor (see determineAttenuation in attenuation.ts).
        Either way this is never something to pick by hand anymore, only to watch.
      -->
      <span
        class="flex items-center gap-2 text-neutral-500 dark:text-neutral-400"
        title="Automatically kept matched to the input signal — by the device's own auto-ranging where available, otherwise by continuously monitoring the peak level."
      >
        Input range
        <span class="rounded border border-neutral-200 px-2 py-0.5 font-mono dark:border-neutral-800">
          {{ status?.inputRangeDbv ?? '--' }} dBV
        </span>
      </span>
      <!--
        Read-only: the output range is derived from the generator's own amplitude
        (OutputRangeSelector), the way QuantAsylum's software does it, because it is an analog
        attenuator whose setting sets the DAC's noise floor. Leaving it on 18 dBV for a -15 dBV tone
        wasted 33 dB of headroom and put that noise straight into every loopback measurement.
      -->
      <span
        class="flex items-center gap-2 text-neutral-500 dark:text-neutral-400"
        title="Selected automatically from the generator amplitude — the lowest range with enough headroom, for the lowest DAC noise."
      >
        Output range
        <span class="rounded border border-neutral-200 px-2 py-0.5 font-mono dark:border-neutral-800">
          {{ status?.outputRangeDbv ?? '--' }} dBV
        </span>
      </span>
    </div>

    <p v-if="error" class="w-full text-red-600 dark:text-red-400">{{ error }}</p>
  </footer>
</template>
