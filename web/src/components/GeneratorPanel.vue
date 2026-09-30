<script setup lang="ts">
import { computed, ref, useTemplateRef, watch } from 'vue'
import type { DeviceStatus, GeneratorChannelRequest } from '../api/client'
import { useProjects } from '../composables/useProjects'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useGeneratorStimulus } from '../composables/useGeneratorStimulus'
import GeneratorChannelPanel from './GeneratorChannelPanel.vue'
import CollapsibleSection from './CollapsibleSection.vue'

// Shared with both channels' Metrics panels — one amplifier load, edited here instead of twice.
const { loadOhms } = useLoadOhms()

const { loadedParams } = useProjects()

// Forwarded to App.vue, which pushes the edit live if a generator is already playing (see
// applyGeneratorLive) — otherwise a no-op, since the value is already staged in this component's
// own state either way.
const emit = defineEmits<{ change: [] }>()

const channelPanel = useTemplateRef<InstanceType<typeof GeneratorChannelPanel>>('channelPanel')

// Left and right play the same stimulus — only whether each channel is on is separate. Staged
// from whichever measurement is currently selected, same as the shared stimulus form below;
// nothing here reaches the device until Run.
const leftEnabled = ref(true)
const rightEnabled = ref(false)
const snapToBin = ref(true)
const { kind: stimulusKind } = useGeneratorStimulus()

// Snapping only ever rewrites a channel's `tones` list (see DeviceSessionService.SnapTones) — a
// silent no-op for Multitone/WhiteNoise, which have none. Disabled rather than just documented so
// it can't look like it's doing something for those kinds.
// Sine tones and the multitone comb: the server snaps a comb tone by tone (DeviceSessionService.
// SnapTones), and the multitone TD+N reading depends on it -- unsnapped, every gap between two
// tones reads their own Hann sidelobes, about -60 dBr on a straight wire, as if it were distortion.
const canSnapToBin = computed(() => stimulusKind.value === 'Tones' || stimulusKind.value === 'Multitone')
// Switching to a snappable stimulus re-enables it by default (the common case wants it on);
// switching away disables it, same as before.
watch(stimulusKind, (kind) => {
  snapToBin.value = kind === 'Tones' || kind === 'Multitone'
})

watch(
  loadedParams,
  (params) => {
    if (!params) return
    leftEnabled.value = params.generatorLeft.enabled
    rightEnabled.value = params.generatorRight.enabled
    // Clamped against the loaded kind directly, rather than against stimulusKind (synced from the
    // child a tick later) — a measurement saved before this gate existed could have snapToBin true
    // alongside a Multitone/WhiteNoise kind, and this must not resurrect it as checked.
    snapToBin.value = params.snapToBin && params.generatorLeft.kind === 'Tones'
  },
  { immediate: true },
)

/** Reads the currently staged form state for App.vue's Run handler — building this has no side effects of its own. */
function buildRequest(): { left: GeneratorChannelRequest; right: GeneratorChannelRequest; snapToBin: boolean } | null {
  const shared = channelPanel.value?.currentConfig()
  if (!shared) return null
  return {
    left: { ...shared, enabled: leftEnabled.value },
    right: { ...shared, enabled: rightEnabled.value },
    snapToBin: snapToBin.value,
  }
}

// Called by App.vue after Run (or a live sample-rate change) comes back with re-snapped tone
// frequencies, so the form reflects what's actually playing instead of what was last typed. Takes
// the fresh status directly rather than reading it back off a prop — a prop would only update once
// Vue re-renders this component, which hasn't happened yet in the same tick as the caller's own
// `status.value = ...` assignment. Left and right play the same tones, so either channel's status
// reports the same snapped frequency — left is read here purely as the arbitrary pick.
function resyncTones(status: DeviceStatus) {
  channelPanel.value?.syncFromStatus(status.generatorLeft)
}

defineExpose({ buildRequest, resyncTones })
</script>

<template>
  <CollapsibleSection title="Generator" padding="p-4">
    <fieldset class="mb-3 flex items-center gap-4 text-sm" title="Which channel(s) play this stimulus once Run is pressed — the ADC always samples both.">
      <legend class="sr-only">Enabled channels</legend>
      <label class="flex items-center gap-1.5">
        <input type="checkbox" v-model="leftEnabled" @change="emit('change')" />
        Left
      </label>
      <label class="flex items-center gap-1.5">
        <input type="checkbox" v-model="rightEnabled" @change="emit('change')" />
        Right
      </label>
    </fieldset>

    <div class="mb-3 flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
      <label class="flex items-center gap-1.5" title="Amplifier load, used for both channels' power figures.">
        Load
        <input
          type="number"
          min="0.1"
          step="0.1"
          v-model.number="loadOhms"
          class="w-16 rounded border border-neutral-300 px-1 py-0.5 dark:border-neutral-700"
        />
        Ω
      </label>
    </div>

    <GeneratorChannelPanel
      ref="channelPanel"
      :load-state="loadedParams?.generatorLeft"
      @change="emit('change')"
    />

    <div class="mt-4 flex items-center gap-4">
      <!--
        A tone that doesn't land exactly on an FFT bin leaks across its neighbours whatever window is
        used. Snapping moves it to the nearest coherent bin — which depends on the FFT size, so the
        effective frequency changes when the analyzer is reconfigured. The frequency field above
        always shows what was typed; the label beneath it shows what's really playing once Run
        confirms it.
      -->
      <label
        class="flex items-center gap-1.5 text-sm text-neutral-500 dark:text-neutral-400"
        :class="{ 'opacity-40': !canSnapToBin }"
        :title="canSnapToBin
          ? 'Snap tone frequencies to the nearest coherent FFT bin, so a tone lands on exactly one bin with no leakage.'
          : 'Only applies to sine tones and the multitone comb — noise and the chirp have no frequency to snap.'"
      >
        <input v-model="snapToBin" type="checkbox" :disabled="!canSnapToBin" @change="emit('change')" />
        Snap to bin
      </label>
    </div>
  </CollapsibleSection>
</template>
