<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import type { GeneratorChannelState, GeneratorTone } from '../api/client'

const props = defineProps<{
  label: string
  channelStatus: GeneratorChannelState | undefined
}>()

const enabled = ref(false)
const tones = reactive<GeneratorTone[]>([{ freqHz: 1000, ampDbv: -6 }])

// Sync once from the backend's actual per-channel generator state on first load, so this
// checkbox can never start disagreeing with what's actually playing. Not a continuous sync —
// after this, the form is the user's to edit until Apply, so a periodic status poll can't
// clobber an in-progress edit. Mirrors the pre-split GeneratorPanel's own sync logic.
watch(
  () => props.channelStatus,
  (status) => {
    if (!status) return
    enabled.value = status.enabled
    if (status.tones.length > 0)
      tones.splice(0, tones.length, ...status.tones.map((t) => ({ ...t })))
  },
  { once: true },
)

function addTone() {
  tones.push({ freqHz: 1000, ampDbv: -6 })
}

function removeTone(index: number) {
  tones.splice(index, 1)
}

function currentConfig(): { enabled: boolean; tones: GeneratorTone[] } {
  return { enabled: enabled.value, tones: tones.map((t) => ({ ...t })) }
}

defineExpose({ currentConfig })
</script>

<template>
  <div>
    <h3 class="mb-2 text-xs font-semibold uppercase tracking-wide text-neutral-500 dark:text-neutral-400">{{ label }}</h3>

    <label class="mb-3 flex items-center gap-2 text-sm">
      <input type="checkbox" v-model="enabled" />
      Enabled
    </label>

    <div class="space-y-2">
      <div v-for="(tone, i) in tones" :key="i" class="flex items-center gap-2 text-sm">
        <input
          type="number"
          v-model.number="tone.freqHz"
          class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          placeholder="Freq (Hz)"
        />
        <span class="text-neutral-500">Hz @</span>
        <input
          type="number"
          v-model.number="tone.ampDbv"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          placeholder="Amp (dBV)"
        />
        <span class="text-neutral-500">dBV</span>
        <button type="button" class="text-neutral-400 hover:text-red-500" @click="removeTone(i)">&times;</button>
      </div>
    </div>

    <button
      type="button"
      class="mt-3 rounded border border-neutral-300 px-3 py-1 text-sm hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
      @click="addTone"
    >
      + Tone
    </button>
  </div>
</template>
