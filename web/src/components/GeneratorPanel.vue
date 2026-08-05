<script setup lang="ts">
import { useTemplateRef } from 'vue'
import type { DeviceStatus, GeneratorChannelRequest } from '../api/client'
import GeneratorChannelPanel from './GeneratorChannelPanel.vue'

const props = defineProps<{ status: DeviceStatus | null }>()

const emit = defineEmits<{
  apply: [left: GeneratorChannelRequest, right: GeneratorChannelRequest]
}>()

const leftPanel = useTemplateRef<InstanceType<typeof GeneratorChannelPanel>>('leftPanel')
const rightPanel = useTemplateRef<InstanceType<typeof GeneratorChannelPanel>>('rightPanel')

function apply() {
  const left = leftPanel.value?.currentConfig()
  const right = rightPanel.value?.currentConfig()
  if (!left || !right) return
  emit('apply', left, right)
}
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-4 dark:border-neutral-800">
    <h2 class="mb-3 text-sm font-semibold text-neutral-900 dark:text-neutral-100">Generator</h2>

    <div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
      <GeneratorChannelPanel ref="leftPanel" label="Left" :channel-status="props.status?.generatorLeft" />
      <GeneratorChannelPanel ref="rightPanel" label="Right" :channel-status="props.status?.generatorRight" />
    </div>

    <button
      type="button"
      class="mt-4 rounded bg-neutral-900 px-3 py-1 text-sm text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
      @click="apply"
    >
      Apply
    </button>
  </section>
</template>
