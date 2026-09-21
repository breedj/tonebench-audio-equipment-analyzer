<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import type { GeneratorChannelRequest, GeneratorChannelState, GeneratorKind, GeneratorTone, ImdMethodName, SmoothingOctaves } from '../api/client'
import { useGeneratorStimulus } from '../composables/useGeneratorStimulus'
import { useMetricSelection } from '../composables/useMetricSelection'
import { IMD_DEFAULT_MAX_ORDER, IMD_PRESETS, imdPreset, presetTones, sineEquivalentDbv } from '../imd/presets'

const props = defineProps<{
  /**
   * Set whenever a measurement is (re)selected — see GeneratorPanel.vue. Loads this stimulus's
   * kind/tones/etc into the form. Not "live device status": the whole point of the Run/Stop model
   * is that selecting a measurement stages its generator config without playing it, so this always
   * comes from the measurement type's stored parameters, never from a status poll (which would show
   * whatever's *actually* configured, i.e. usually silence).
   */
  loadState: GeneratorChannelState | null | undefined
}>()

// Bubbled up to GeneratorPanel, then App.vue — pushes the edit live if a generator is already
// playing, otherwise a no-op (the value's already staged here regardless). See
// App.vue's applyGeneratorLive.
const emit = defineEmits<{ change: [] }>()

// 'Off' is deliberately not offered here — GeneratorPanel's own L/R "Enabled" checkboxes are the
// on/off control, and a second way to say "silent" would just be two controls disagreeing.
//
// 'EchoChirp' is deliberately left out of this list — not deleted, just hidden from this picker.
// The kind, its form below, and the underlying request/state types all still exist; it's slated
// for a different UI elsewhere later.
const KINDS: { value: GeneratorKind; label: string }[] = [
  { value: 'Tones', label: 'Sine tones' },
  { value: 'Multitone', label: 'Multitone' },
  { value: 'WhiteNoise', label: 'White noise' },
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

// Shared with GeneratorPanel (which gates its "Snap to bin" checkbox on the current kind — snapping
// only ever touches a channel's `tones` list server-side, see DeviceSessionService.SnapTones, so
// it's a silent no-op for Multitone/WhiteNoise) and with GeneratorMetricsPanel.vue, which needs this
// staged config reactively even after Stop silences DeviceStatus.generatorLeft/Right — see
// useGeneratorStimulus.ts.
const { kind, tones, multitone, noise, chirp, imd } = useGeneratorStimulus()
const { selectedMetrics, setSelectedMetrics } = useMetricSelection()

const IMD_METHODS: { value: ImdMethodName; label: string }[] = [
  { value: 'Smpte', label: 'SMPTE / DIN (sidebands re carrier)' },
  { value: 'Ccif', label: 'CCIF / ITU-R (difference tones)' },
]

// A preset owns the tone list: its two frequencies are fixed and both levels follow one
// "sine-equivalent level" (see imd/presets.ts), so the per-tone inputs are read-only under it.
// Custom leaves the list free; with exactly two tones it still gets an IMD reading, with the
// method and order chosen here.
const presetActive = computed(() => kind.value === 'Tones' && imd.preset !== 'Custom')

// A preset's tone fields are read-only, so their spinner arrows do nothing but hide the last
// digit of "19000" and "-12.02" in a 4rem box; they are dropped while the preset holds the field.
const NO_SPINNER = '[appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none'
const twoTones = computed(() => kind.value === 'Tones' && tones.length === 2)
const presetLevelDbv = computed(() => {
  const level = sineEquivalentDbv(tones)
  return level === null ? -6 : Math.round(level * 100) / 100
})

function applyPreset(levelDbv: number) {
  const preset = imdPreset(imd.preset)
  tones.splice(0, tones.length, ...presetTones(preset, levelDbv))
  actualFreqHz.splice(0, actualFreqHz.length, null, null)
}

function onKindChange() {
  // Picking the multitone stimulus is asking for its reading, the way an IMD preset is: surface
  // the TD+N tiles once. A later removal is respected.
  if (kind.value === 'Multitone') {
    const missing = ['TD+N %', 'TD+N dB'].filter((label) => !selectedMetrics.value.includes(label))
    if (missing.length) setSelectedMetrics([...selectedMetrics.value, ...missing])
  }
  emit('change')
}

function onPresetChange() {
  if (imd.preset !== 'Custom') {
    const preset = imdPreset(imd.preset)
    applyPreset(presetLevelDbv.value)
    imd.method = preset.method
    imd.maxOrder = preset.defaultMaxOrder
    // Picking an IMD test is asking for the reading: surface the tiles once, the way a user
    // would have to via the metric picker otherwise. A later removal is respected.
    const missing = ['IMD %', 'IMD dB'].filter((label) => !selectedMetrics.value.includes(label))
    if (missing.length) setSelectedMetrics([...selectedMetrics.value, ...missing])
  }
  emit('change')
}

function onPresetLevelChange(event: Event) {
  const value = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(value)) return
  applyPreset(value)
  emit('change')
}

function onMethodChange() {
  imd.maxOrder = IMD_DEFAULT_MAX_ORDER[imd.method]
  emit('change')
}

// What the device is actually playing per tone, in parallel with `tones` — kept separate so
// syncFromStatus (below) can report the real, possibly-snapped bin frequency without overwriting
// what the user typed. Index-aligned with `tones`; null until Run confirms an actual frequency.
const actualFreqHz = reactive<(number | null)[]>([null])

// Stages this form from a measurement type's stored config — see `loadState` above. Also clears
// actualFreqHz: a freshly-loaded measurement hasn't been Run yet, so there's no real bin frequency
// to show until it has.
watch(
  () => props.loadState,
  (state) => {
    if (!state) return
    // 'Off' is the never-configured state and has no dropdown entry — leave the default selected.
    if (state.kind !== 'Off') kind.value = state.kind
    if (state.tones.length > 0 && state.kind === 'Tones')
      tones.splice(0, tones.length, ...state.tones.map((t) => ({ ...t })))
    actualFreqHz.fill(null, 0, actualFreqHz.length)
    Object.assign(multitone, state.multitone)
    Object.assign(noise, state.noise)
    Object.assign(chirp, state.chirp)
  },
  { immediate: true },
)

function addTone() {
  tones.push({ freqHz: 1000, ampDbv: -6 })
  actualFreqHz.push(null)
  emit('change')
}

function removeTone(index: number) {
  tones.splice(index, 1)
  actualFreqHz.splice(index, 1)
  emit('change')
}

// A hand edit of a tone under a preset would silently leave the preset's name on a pair it no
// longer describes, so the inputs are disabled there; a change to the list's length under Custom
// is what turns the IMD analysis on (two tones) or off.
function onToneEdit() {
  emit('change')
}

// Called by GeneratorPanel after Run (or a sample-rate change while running) re-snaps tones to the
// new bin frequencies server-side. Updates `actualFreqHz` only — not `tones[i].freqHz` — so the
// textbox keeps showing what was typed (e.g. 1000) while the label beneath it reports what's
// really playing (e.g. 999.76, the nearest coherent bin).
function syncFromStatus(status: GeneratorChannelState) {
  if (status.kind !== 'Tones') return
  status.tones.forEach((t, i) => {
    if (i < actualFreqHz.length) actualFreqHz[i] = t.freqHz
  })
}

/** The stimulus config shared by both channels — GeneratorPanel adds each channel's own `enabled` on top. */
function currentConfig(): Omit<GeneratorChannelRequest, 'enabled'> {
  const base = { kind: kind.value, tones: [] as GeneratorTone[] }

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
    <label class="mb-3 flex flex-col gap-1 text-sm">
      Stimulus
      <select
        v-model="kind"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        @change="onKindChange"
      >
        <option v-for="k in KINDS" :key="k.value" :value="k.value">{{ k.label }}</option>
      </select>
    </label>

    <div v-if="kind === 'Tones'">
      <label class="mb-3 flex flex-col gap-1 text-sm" title="A standard two-tone intermodulation test: sets both tones up from one sine-equivalent level (the pair's peak equals that sine's peak) and turns the IMD reading on.">
        IMD preset
        <select
          v-model="imd.preset"
          class="rounded border border-neutral-300 px-2 py-1 text-xs dark:border-neutral-700 dark:bg-neutral-800"
          @change="onPresetChange"
        >
          <option v-for="p in IMD_PRESETS" :key="p.value" :value="p.value">{{ p.label }}</option>
        </select>
      </label>

      <label v-if="presetActive" class="mb-3 flex items-center gap-2 text-sm" title="The level of a single sine whose peak equals the two-tone pair's peak. 4:1 puts the tones 1.94 and 13.98 dB below it, 1:1 puts both 6.02 dB below.">
        Level
        <input
          type="number"
          step="0.5"
          :value="presetLevelDbv"
          class="w-20 rounded border border-neutral-300 px-1.5 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @change="onPresetLevelChange"
        />
        <span class="text-neutral-500">dBV sine-eq.</span>
      </label>

      <div class="space-y-2">
        <div v-for="(tone, i) in tones" :key="i" class="flex items-start gap-2 text-sm">
          <div class="flex flex-col gap-0.5">
            <input
              type="number"
              v-model.number="tone.freqHz"
              :disabled="presetActive"
              class="w-16 rounded border border-neutral-300 px-1.5 py-1 disabled:opacity-60 dark:border-neutral-700 dark:bg-neutral-800"
              :class="{ [NO_SPINNER]: presetActive }"
              placeholder="Freq (Hz)"
              @change="onToneEdit"
            />
            <!-- No unit of its own: the "Hz @" beside the field already names it, and at w-16 the
                 unit wrapped onto a second line. -->
            <span
              v-if="actualFreqHz[i] != null"
              class="whitespace-nowrap text-xs text-neutral-400 dark:text-neutral-600"
              title="The coherent bin frequency actually playing."
            >
              → {{ actualFreqHz[i]!.toFixed(2) }}
            </span>
          </div>
          <span class="pt-1.5 text-neutral-500">Hz @</span>
          <input
            type="number"
            v-model.number="tone.ampDbv"
            :disabled="presetActive"
            class="w-16 rounded border border-neutral-300 px-1.5 py-1 disabled:opacity-60 dark:border-neutral-700 dark:bg-neutral-800"
            :class="{ [NO_SPINNER]: presetActive }"
            placeholder="Amp (dBV)"
            @change="onToneEdit"
          />
          <span class="pt-1.5 text-neutral-500">dBV</span>
          <button v-if="!presetActive" type="button" class="pt-1.5 text-neutral-400 hover:text-red-500" title="Remove tone" @click="removeTone(i)">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0-1 14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2L4 6h16Z" />
            </svg>
          </button>
        </div>
      </div>

      <button
        v-if="!presetActive"
        type="button"
        class="mt-3 rounded border border-neutral-300 px-3 py-1 text-sm hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        @click="addTone"
      >
        + Tone
      </button>

      <!-- Two tones are an IMD test whether or not a preset put them there; the analysis needs to
           know which convention to count products by. Hidden with any other tone count, where no
           IMD is measured at all. -->
      <div v-if="twoTones" class="mt-3 flex flex-wrap items-end gap-3 text-sm">
        <label class="flex flex-col gap-1" title="SMPTE/DIN: sidebands f2 ± n·f1, relative to the HF carrier. CCIF: difference products |m·f1 − n·f2|, relative to the RMS sum of both tones.">
          IMD method
          <select
            v-model="imd.method"
            class="w-full rounded border border-neutral-300 px-2 py-1 text-xs dark:border-neutral-700 dark:bg-neutral-800"
            @change="onMethodChange"
          >
            <option v-for="m in IMD_METHODS" :key="m.value" :value="m.value">{{ m.label }}</option>
          </select>
        </label>
        <label class="flex flex-col gap-1" title="Highest product order counted. SMPTE 5 = four sideband pairs; CCIF 3 = the 2nd-order difference tone plus both 3rd-order products.">
          Max order
          <input
            type="number"
            min="2"
            max="12"
            v-model.number="imd.maxOrder"
            class="w-16 rounded border border-neutral-300 px-1.5 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="emit('change')"
          />
        </label>
      </div>
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
