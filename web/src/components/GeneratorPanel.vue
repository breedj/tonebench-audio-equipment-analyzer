<script setup lang="ts">
import { ref, useTemplateRef, watch } from 'vue'
import type { DeviceStatus, GeneratorChannelRequest } from '../api/client'
import GeneratorChannelPanel from './GeneratorChannelPanel.vue'

const props = defineProps<{ status: DeviceStatus | null }>()

const emit = defineEmits<{
  apply: [left: GeneratorChannelRequest, right: GeneratorChannelRequest, snapToBin: boolean]
}>()

const leftPanel = useTemplateRef<InstanceType<typeof GeneratorChannelPanel>>('leftPanel')
const rightPanel = useTemplateRef<InstanceType<typeof GeneratorChannelPanel>>('rightPanel')

// Server default is on, and this is a form field like the channel panels' own: seeded from status
// but not re-synced on every poll, or typing would fight the 1 Hz refresh. Seeded once, when the
// first status arrives.
const snapToBin = ref(true)
let snapSeeded = false
watch(
  () => props.status,
  (status) => {
    if (status && !snapSeeded) {
      snapToBin.value = status.generatorSnapToBin
      snapSeeded = true
    }
  },
  { immediate: true },
)

function apply() {
  const left = leftPanel.value?.currentConfig()
  const right = rightPanel.value?.currentConfig()
  if (!left || !right) return
  emit('apply', left, right, snapToBin.value)
}

// Called by App.vue after a sample-rate change comes back with re-snapped tone frequencies, so the
// form reflects what's actually playing instead of what was last typed. Takes the fresh status
// directly rather than reading it back off `props.status` — that prop only updates once Vue
// re-renders this component, which hasn't happened yet in the same tick as the caller's own
// `status.value = ...` assignment.
function resyncTones(status: DeviceStatus) {
  leftPanel.value?.syncFromStatus(status.generatorLeft)
  rightPanel.value?.syncFromStatus(status.generatorRight)
}

defineExpose({ resyncTones })
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-4 dark:border-neutral-800">
    <h2 class="mb-3 text-sm font-semibold text-neutral-900 dark:text-neutral-100">Generator</h2>

    <div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
      <GeneratorChannelPanel ref="leftPanel" label="Left" :channel-status="props.status?.generatorLeft" @change="apply" />
      <GeneratorChannelPanel ref="rightPanel" label="Right" :channel-status="props.status?.generatorRight" @change="apply" />
    </div>

    <div class="mt-4 flex items-center gap-4">
      <!--
        A tone that doesn't land exactly on an FFT bin leaks across its neighbours whatever window is
        used. Snapping moves it to the nearest coherent bin — which depends on the FFT size, so the
        effective frequency changes when the analyzer is reconfigured. The frequency fields above
        show what is really playing (status carries the snapped value), not what was typed.
      -->
      <label
        class="flex items-center gap-1.5 text-sm text-neutral-500 dark:text-neutral-400"
        title="Snap tone frequencies to the nearest coherent FFT bin, so a tone lands on exactly one bin with no leakage."
      >
        <input v-model="snapToBin" type="checkbox" @change="apply" />
        Snap to bin
      </label>
    </div>
  </section>
</template>
