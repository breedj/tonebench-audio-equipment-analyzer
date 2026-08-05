<script setup lang="ts">
import type { DeviceRanges, DeviceStatus, SimulatedProfileRequest } from '../api/client'

defineProps<{
  status: DeviceStatus | null
  ranges: DeviceRanges | null
  error: string | null
}>()

const emit = defineEmits<{
  setInputRange: [dbv: number]
  setOutputRange: [dbv: number]
  setSimulatedProfile: [patch: SimulatedProfileRequest]
}>()

function onInputRangeChange(e: Event) {
  emit('setInputRange', Number((e.target as HTMLSelectElement).value))
}

function onOutputRangeChange(e: Event) {
  emit('setOutputRange', Number((e.target as HTMLSelectElement).value))
}

function onSimulatedNoiseChange(e: Event) {
  emit('setSimulatedProfile', { noiseEnabled: (e.target as HTMLInputElement).checked })
}

function onSimulatedHumChange(e: Event) {
  emit('setSimulatedProfile', { humEnabled: (e.target as HTMLInputElement).checked })
}
</script>

<template>
  <header
    class="flex flex-wrap items-center gap-4 border-b border-neutral-200 bg-white px-4 py-3 dark:border-neutral-800 dark:bg-neutral-900"
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
          class="flex items-center gap-1.5 text-sm text-neutral-500 dark:text-neutral-400"
          title="Simulated DUT noise floor (white + 1/f). Turn off for a clean spectrum."
        >
          <input type="checkbox" :checked="status.simulatedNoiseEnabled ?? false" @change="onSimulatedNoiseChange" />
          Noise
        </label>
        <label
          class="flex items-center gap-1.5 text-sm text-neutral-500 dark:text-neutral-400"
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

    <div v-if="status?.connected" class="flex items-center gap-3 text-sm text-neutral-500 dark:text-neutral-400">
      <span>S/N {{ status.serialNumber }}</span>
      <span>FW {{ status.firmwareVersion }}</span>
    </div>

    <div class="ml-auto flex items-center gap-4 text-sm">
      <label class="flex items-center gap-2">
        Input range
        <select
          class="rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          :value="status?.inputRangeDbv"
          @change="onInputRangeChange"
        >
          <option v-for="dbv in ranges?.inputRangesDbv" :key="dbv" :value="dbv">{{ dbv }} dBV</option>
        </select>
      </label>
      <label class="flex items-center gap-2">
        Output range
        <select
          class="rounded border border-neutral-300 bg-white px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          :value="status?.outputRangeDbv"
          @change="onOutputRangeChange"
        >
          <option v-for="dbv in ranges?.outputRangesDbv" :key="dbv" :value="dbv">{{ dbv }} dBV</option>
        </select>
      </label>
    </div>

    <p v-if="error" class="w-full text-sm text-red-600 dark:text-red-400">{{ error }}</p>
  </header>
</template>
