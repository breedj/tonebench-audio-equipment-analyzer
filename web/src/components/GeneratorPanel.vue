<script setup lang="ts">
import { computed, ref, useTemplateRef, watch } from 'vue'
import type { DeviceRanges, DeviceStatus, GeneratorChannelRequest, GeneratorKind } from '../api/client'
import { useProjects } from '../composables/useProjects'
import { useLoadOhms } from '../composables/useLoadOhms'
import { useGeneratorStimulus } from '../composables/useGeneratorStimulus'
import { canSnapToBin as canSnapToBinKind, snapToBinOnLoad } from '../measurements/snapToBin'
import { useStagedSampleRate } from '../composables/useStagedSampleRate'
import { generatorRateCeilingNotice } from '../generatorRateCeiling'
import GeneratorChannelPanel from './GeneratorChannelPanel.vue'
import CollapsibleSection from './CollapsibleSection.vue'

const props = defineProps<{
  ranges: DeviceRanges | null
}>()

// Shared with both channels' Metrics panels — one amplifier load, edited here instead of twice.
const { loadOhms } = useLoadOhms()

const { loadedParams } = useProjects()

// Forwarded to App.vue, which pushes the edit live if a generator is already playing (see
// applyGeneratorLive) — otherwise a no-op, since the value is already staged in this component's
// own state either way.
const emit = defineEmits<{ change: [] }>()

const channelPanel = useTemplateRef<InstanceType<typeof GeneratorChannelPanel>>('channelPanel')

// The rate the FFT settings panel beside this one currently has staged -- not what the device is
// streaming at (see useStagedSampleRate.ts).
const { stagedSampleRateHz } = useStagedSampleRate()

/**
 * Non-null when the FFT screen is staged at a rate this analyzer captures at but cannot play --
 * 384 kHz on a QA403, where the DAC emits garbage with enough near-DC content to cycle the device's
 * own output protection relay (see generatorRateCeiling.ts). The panel's whole body is replaced by
 * it: a stimulus composed here could not be played, and the server refuses a voiced generator at
 * that rate with a 409 on Run rather than clamping it.
 *
 * The staged form is left *intact* behind the notice rather than cleared -- dropping back to
 * 192 kHz restores the tone, the level and the channel checkboxes exactly as they were, so a rate
 * tried out of curiosity does not cost the user their stimulus.
 */
const rateCeilingNotice = computed(() =>
  generatorRateCeilingNotice(stagedSampleRateHz.value, props.ranges?.maxGeneratorSampleRateHz),
)
const generatorUsable = computed(() => rateCeilingNotice.value === null)

// Left and right play the same stimulus — only whether each channel is on is separate. Staged
// from whichever measurement is currently selected, same as the shared stimulus form below;
// nothing here reaches the device until Run.
const leftEnabled = ref(true)
const rightEnabled = ref(false)
const snapToBin = ref(true)
const { kind: stimulusKind } = useGeneratorStimulus()

// Which kinds snap is `snapToBin.ts`'s to say, for both this and the watcher below — the rule
// used to be written out twice here, in two different spellings, and they disagreed.
const canSnapToBin = computed(() => canSnapToBinKind(stimulusKind.value))

// Switching to a snappable stimulus re-enables it by default (the common case wants it on);
// switching away disables it.
//
// Not when the change came from *loading* a measurement, though: applying a stored type also moves
// the kind, a tick after the watcher below has already restored the box from what that measurement
// saved, so without this guard arriving at a Multitone measurement from a Tones one would tick a
// box its own parameters say is off — and arriving at it from another Multitone one would not,
// which is a setting that depends on where you came from.
// The kind the child is about to sync to on behalf of a load, if any — matched by value rather
// than held as a bare flag, because a load whose kind is the one already selected moves nothing
// and would leave a flag set, swallowing the user's next real stimulus change instead.
let pendingLoadKind: GeneratorKind | null = null
watch(stimulusKind, (kind) => {
  const expected = pendingLoadKind
  pendingLoadKind = null
  if (kind === expected) return
  snapToBin.value = canSnapToBinKind(kind)
})

watch(
  loadedParams,
  (params) => {
    if (!params) return
    leftEnabled.value = params.generatorLeft.enabled
    rightEnabled.value = params.generatorRight.enabled
    // Judged against the loaded kind rather than stimulusKind, which the child syncs a tick later
    // — see snapToBin.ts for the two things that are easy to get wrong here, both of which this
    // line had: the multitone comb does snap, and `Off` is not a stimulus.
    snapToBin.value = snapToBinOnLoad(params.snapToBin, params.generatorLeft.kind)
    // `Off` leaves the child's own default selected (GeneratorChannelPanel), so nothing is coming.
    pendingLoadKind = params.generatorLeft.kind === 'Off' ? null : params.generatorLeft.kind
  },
  { immediate: true },
)

/** Reads the currently staged form state for App.vue's Run handler — building this has no side effects of its own. */
function buildRequest(): { left: GeneratorChannelRequest; right: GeneratorChannelRequest; snapToBin: boolean } | null {
  const shared = channelPanel.value?.currentConfig()
  if (!shared) return null
  // Both channels are forced silent at a capture-only rate, here rather than by clearing the
  // checkboxes: this is the one seam every generator write passes through (Run, a live edit, and
  // the measurement-type save all call it), so no path can reach the device with a voiced channel
  // the analyzer would be damaged playing. GeneratorChannelPanel stays mounted behind the notice
  // for exactly this reason -- unmounted, `shared` would be null, this would return null, and
  // App.vue would skip the generator call altogether rather than sending the silence.
  const voiced = generatorUsable.value
  return {
    left: { ...shared, enabled: voiced && leftEnabled.value },
    right: { ...shared, enabled: voiced && rightEnabled.value },
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
    <!-- A rate this analyzer cannot play takes the panel out of service: the title and its collapse
         control stay (so the rail's shape does not change under the user), and everything that would
         compose a stimulus is replaced by the reason. `v-show` and not `v-if` -- see buildRequest:
         the channel panel has to stay mounted to report the staged config it silences. -->
    <p
      v-if="rateCeilingNotice"
      class="text-sm text-amber-700 dark:text-amber-400"
    >
      {{ rateCeilingNotice }}
    </p>

    <div v-show="generatorUsable" class="contents">
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
            : 'Only applies to sine tones and the multitone comb. Noise and the chirp have no single frequency; a square or triangle has one, but it is quantised to a whole sample period instead (shown under the frequency box).'"
        >
          <input v-model="snapToBin" type="checkbox" :disabled="!canSnapToBin" @change="emit('change')" />
          Snap to bin
        </label>
      </div>
    </div>
  </CollapsibleSection>
</template>
