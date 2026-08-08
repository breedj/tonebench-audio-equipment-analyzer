<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import type {
  GeneratorChannelRequest,
  GeneratorChannelState,
  GeneratorKind,
  GeneratorTone,
  SmoothingOctaves,
} from '../api/client'

const props = defineProps<{
  label: string
  channelStatus: GeneratorChannelState | undefined
}>()

const emit = defineEmits<{ change: [] }>()

// 'Off' is deliberately not offered here — the Enabled checkbox is this channel's on/off control,
// and a second way to say "silent" would just be two controls disagreeing.
const KINDS: { value: GeneratorKind; label: string }[] = [
  { value: 'Tones', label: 'Sine tones' },
  { value: 'Multitone', label: 'Multitone' },
  { value: 'WhiteNoise', label: 'White noise' },
  { value: 'EchoChirp', label: 'Frequency response (EchoChirp)' },
]

const SMOOTHING: { value: SmoothingOctaves; label: string }[] = [
  { value: 'None', label: 'None' },
  { value: 'OneOver96', label: '1/96' },
  { value: 'OneOver48', label: '1/48' },
  { value: 'OneOver24', label: '1/24' },
  { value: 'OneOver12', label: '1/12' },
  { value: 'OneOver6', label: '1/6' },
  { value: 'OneOver3', label: '1/3' },
]

const enabled = ref(false)
const kind = ref<GeneratorKind>('Tones')

// One state object per kind, all kept alive at once, so switching the dropdown back and forth
// never discards what the user typed into the other forms. currentConfig() sends only the
// selected kind's block.
const tones = reactive<GeneratorTone[]>([{ freqHz: 1000, ampDbv: -6 }])
const multitone = reactive({ tonesPerOctave: 5, totalRmsDbv: -50, rolloffDbPerOctave: 0 })
const noise = reactive({ levelDbv: -20 })
const chirp = reactive({
  amplitudeDbv: -20,
  startFreqHz: 20,
  endFreqHz: 20000,
  smoothing: 'None' as SmoothingOctaves,
})

// Sync once from the backend's actual per-channel generator state on first load, so this
// checkbox can never start disagreeing with what's actually playing. Not a continuous sync —
// after this, the form is the user's to edit until Apply, so a periodic status poll can't
// clobber an in-progress edit. Mirrors the pre-split GeneratorPanel's own sync logic.
watch(
  () => props.channelStatus,
  (status) => {
    if (!status) return
    enabled.value = status.enabled
    // 'Off' is the never-configured state and has no dropdown entry — leave the default selected.
    if (status.kind !== 'Off') kind.value = status.kind
    if (status.tones.length > 0 && status.kind === 'Tones')
      tones.splice(0, tones.length, ...status.tones.map((t) => ({ ...t })))
    Object.assign(multitone, status.multitone)
    Object.assign(noise, status.noise)
    Object.assign(chirp, status.chirp)
  },
  { once: true },
)

function addTone() {
  tones.push({ freqHz: 1000, ampDbv: -6 })
  emit('change')
}

function removeTone(index: number) {
  tones.splice(index, 1)
  emit('change')
}

// Called by GeneratorPanel after a sample-rate change re-snaps tones to the new bin frequencies
// server-side — pulls just the frequencies back in, since that's the only thing snapping touches.
// Takes the status explicitly (not read off the `channelStatus` prop) because that prop lags a
// render behind the response this is called with. Not routed through the mount-time watch below:
// that one only ever runs once, on purpose, so it can't clobber an in-progress edit.
function syncFromStatus(status: GeneratorChannelState) {
  if (status.kind !== 'Tones') return
  status.tones.forEach((t, i) => {
    if (tones[i]) tones[i].freqHz = t.freqHz
  })
}

function currentConfig(): GeneratorChannelRequest {
  const base = { enabled: enabled.value, kind: kind.value, tones: [] as GeneratorTone[] }

  switch (kind.value) {
    case 'Tones':
      return { ...base, tones: tones.map((t) => ({ ...t })) }
    case 'Multitone':
      return { ...base, multitone: { ...multitone } }
    case 'WhiteNoise':
      return { ...base, noise: { ...noise } }
    case 'EchoChirp':
      return { ...base, chirp: { ...chirp } }
    default:
      return base
  }
}

defineExpose({ currentConfig, syncFromStatus })
</script>

<template>
  <div>
    <h3 class="mb-2 text-xs font-semibold uppercase tracking-wide text-neutral-500 dark:text-neutral-400">{{ label }}</h3>

    <label class="mb-3 flex items-center gap-2 text-sm">
      <input type="checkbox" v-model="enabled" @change="emit('change')" />
      Enabled
    </label>

    <label class="mb-3 flex flex-col gap-1 text-sm">
      Stimulus
      <select
        v-model="kind"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        @change="emit('change')"
      >
        <option v-for="k in KINDS" :key="k.value" :value="k.value">{{ k.label }}</option>
      </select>
    </label>

    <div v-if="kind === 'Tones'">
      <div class="space-y-2">
        <div v-for="(tone, i) in tones" :key="i" class="flex items-center gap-2 text-sm">
          <input
            type="number"
            v-model.number="tone.freqHz"
            class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            placeholder="Freq (Hz)"
            @change="emit('change')"
          />
          <span class="text-neutral-500">Hz @</span>
          <input
            type="number"
            v-model.number="tone.ampDbv"
            class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            placeholder="Amp (dBV)"
            @change="emit('change')"
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

    <div v-else-if="kind === 'Multitone'" class="space-y-2 text-sm">
      <label class="flex flex-col gap-1">
        Tones per octave
        <input
          type="number"
          min="1"
          v-model.number="multitone.tonesPerOctave"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="emit('change')"
        />
      </label>
      <label class="flex flex-col gap-1">
        Total RMS level (w/ 0 dB rolloff) (dBV)
        <input
          type="number"
          v-model.number="multitone.totalRmsDbv"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="emit('change')"
        />
      </label>
      <label class="flex flex-col gap-1">
        Rolloff per octave (dB)
        <input
          type="number"
          v-model.number="multitone.rolloffDbPerOctave"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="emit('change')"
        />
      </label>
      <p class="text-xs text-neutral-400 dark:text-neutral-600">Fixed 5 Hz – 20 kHz band.</p>
    </div>

    <div v-else-if="kind === 'WhiteNoise'" class="space-y-2 text-sm">
      <label class="flex flex-col gap-1">
        Level (dBV)
        <input
          type="number"
          v-model.number="noise.levelDbv"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="emit('change')"
        />
      </label>
    </div>

    <div v-else-if="kind === 'EchoChirp'" class="space-y-2 text-sm">
      <label class="flex flex-col gap-1">
        Amplitude (dBV)
        <input
          type="number"
          v-model.number="chirp.amplitudeDbv"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="emit('change')"
        />
      </label>
      <label class="flex flex-col gap-1">
        Start frequency (Hz)
        <input
          type="number"
          min="1"
          v-model.number="chirp.startFreqHz"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="emit('change')"
        />
      </label>
      <label class="flex flex-col gap-1">
        End frequency (Hz)
        <input
          type="number"
          v-model.number="chirp.endFreqHz"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="emit('change')"
        />
      </label>
      <label class="flex flex-col gap-1">
        Smoothing (octaves)
        <select
          v-model="chirp.smoothing"
          class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="emit('change')"
        >
          <option v-for="s in SMOOTHING" :key="s.value" :value="s.value">{{ s.label }}</option>
        </select>
      </label>
    </div>
  </div>
</template>
