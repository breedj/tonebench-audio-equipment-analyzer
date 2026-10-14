<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import type { GeneratorChannelRequest, GeneratorChannelState, GeneratorKind, GeneratorTone, ImdMethodName, SmoothingOctaves } from '../api/client'
import { useGeneratorStimulus } from '../composables/useGeneratorStimulus'
import { useStagedSampleRate } from '../composables/useStagedSampleRate'
import {
  isWaveKind,
  waveEffectiveFreqHz,
  waveHarmonicsBelowNyquist,
  waveIdealHarmonicsDbc,
  wavePeakVolts,
  wavePeriodFrames,
} from '../periodicWave'
import { useMetricSelection } from '../composables/useMetricSelection'
import { IMD_DEFAULT_MAX_ORDER, IMD_PRESETS, imdPreset, presetTones, sineEquivalentDbv } from '../imd/presets'
import { generatorLevelFromDbv, generatorLevelToDbv } from '../generatorLevel'
import GeneratorLevelUnitSelect from './GeneratorLevelUnitSelect.vue'

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
  { value: 'Square', label: 'Square' },
  { value: 'Triangle', label: 'Triangle' },
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
const { kind, tones, multitone, noise, wave, chirp, imd, levelUnit } = useGeneratorStimulus()

// The rate Run would ask for, not the one the device happens to be streaming at — the quantised
// frequency depends on it, and the panel has to show the right answer before anything is running.
const { stagedSampleRateHz } = useStagedSampleRate()

/**
 * What a square or triangle will really play, and what it costs: only whole even sample periods
 * exist, so 10 kHz at 192 kHz lands on 9.6 kHz (see periodicWave.ts). Shown rather than silently
 * applied, the same way a snapped tone's real bin frequency is shown beneath what was typed.
 */
const waveReading = computed(() => {
  if (!isWaveKind(kind.value)) return null
  const fs = stagedSampleRateHz.value
  const periodFrames = wavePeriodFrames(wave.freqHz, fs)
  if (periodFrames <= 0) return null
  return {
    effectiveFreqHz: waveEffectiveFreqHz(wave.freqHz, fs),
    periodFrames,
    harmonics: waveHarmonicsBelowNyquist(wave.freqHz, fs),
    peakV: wavePeakVolts(kind.value, wave.levelDbv),
    // Line by line rather than as one THD figure: the THD tile sums only the harmonics the analyzer
    // tracks, so an "ideal THD" quoted here would not be the number beside it on screen -- while
    // these are exactly the rows of the Harmonics table underneath.
    idealHarmonics: waveIdealHarmonicsDbc(kind.value),
    quantised: Math.abs(waveEffectiveFreqHz(wave.freqHz, fs) - wave.freqHz) > 0.0005,
  }
})
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

// Spinner arrows eat the last digit of "19000" or "-12.02" in a 4rem box while stepping by an
// amount that means nothing on either field -- 1 Hz on a frequency, and on a level either 0.5 dB
// or, in volts, a whole volt from a box that reads 0.562. Dropped from every frequency box and
// every level box (Joost, 2026-10-14); `levelStep` stays, because it is also what keeps a typed
// 0.562 from being an out-of-step value the browser marks invalid.
const NO_SPINNER = '[appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none'
const twoTones = computed(() => kind.value === 'Tones' && tones.length === 2)
const presetLevelDbv = computed(() => {
  const level = sineEquivalentDbv(tones)
  return level === null ? -6 : Math.round(level * 100) / 100
})

// Every level box below is stored in dBV and typed in `levelUnit` -- see generatorLevel.ts. The
// level fields therefore bind `:value`/`@change` rather than v-model: an unedited box must keep
// showing the stored number to the last digit, and a round trip through a unit it is not displayed
// in would round it.
const levelStep = computed(() => (levelUnit.value === 'Dbv' ? '0.5' : 'any'))

function displayLevel(ampDbv: number): number {
  return generatorLevelFromDbv(ampDbv, levelUnit.value)
}

/** The dBV a level box now means, or null when what was typed has none (blank, or 0 V). */
function typedLevelDbv(event: Event): number | null {
  return generatorLevelToDbv(Number((event.target as HTMLInputElement).value), levelUnit.value)
}

/**
 * One level box's edit: store the dBV it now means, or -- when what was typed has no dBV at all --
 * put the stored level back rather than leaving a value on screen that was refused.
 *
 * `stored` is what to fall back to, and `assign` where the new dBV goes; every level on the panel
 * goes through here, so a stimulus kind cannot end up half-converted.
 */
function onLevelEdit(event: Event, stored: number, assign: (ampDbv: number) => void) {
  const dbv = typedLevelDbv(event)
  if (dbv === null) {
    ;(event.target as HTMLInputElement).value = String(displayLevel(stored))
    return
  }
  assign(dbv)
  emit('change')
}

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
  onLevelEdit(event, presetLevelDbv.value, applyPreset)
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
    // A type saved before these kinds existed carries no wave block; keep the default rather than
    // assigning undefined over a reactive object's fields.
    if (state.wave) Object.assign(wave, state.wave)
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
    case 'Square':
    case 'Triangle':
      return { ...base, wave: { ...wave } }
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
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
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
          class="rounded border border-neutral-300 px-2 py-1 text-xs dark:border-neutral-700"
          @change="onPresetChange"
        >
          <option v-for="p in IMD_PRESETS" :key="p.value" :value="p.value">{{ p.label }}</option>
        </select>
      </label>

      <!-- Stacked rather than one row: with the unit dropdown beside the box there is no room left
           for "sine-eq." on the same line, and it wrapped mid-phrase. -->
      <label v-if="presetActive" class="mb-3 flex flex-col gap-1 text-sm" title="The level of a single sine whose peak equals the two-tone pair's peak. 4:1 puts the tones 1.94 and 13.98 dB below it, 1:1 puts both 6.02 dB below.">
        Level (sine-equivalent)
        <div class="flex items-center gap-2">
          <input
            type="number"
            :step="levelStep"
            :value="displayLevel(presetLevelDbv)"
            class="w-20 rounded border border-neutral-300 px-1.5 py-1 dark:border-neutral-700"
            :class="NO_SPINNER"
            @change="onPresetLevelChange"
          />
          <GeneratorLevelUnitSelect @change="emit('change')" />
        </div>
      </label>

      <div class="space-y-2">
        <div v-for="(tone, i) in tones" :key="i" class="flex items-start gap-2 text-sm">
          <div class="flex flex-col gap-0.5">
            <input
              type="number"
              v-model.number="tone.freqHz"
              :disabled="presetActive"
              class="w-16 rounded border border-neutral-300 px-1.5 py-1 disabled:opacity-60 dark:border-neutral-700"
              :class="NO_SPINNER"
              placeholder="Freq (Hz)"
              @change="onToneEdit"
            />
            <!-- No unit of its own: the "Hz" beside the field already names it, and at w-16 the
                 unit wrapped onto a second line. -->
            <span
              v-if="actualFreqHz[i] != null"
              class="whitespace-nowrap text-xs text-neutral-400 dark:text-neutral-600"
              title="The coherent bin frequency actually playing."
            >
              → {{ actualFreqHz[i]!.toFixed(2) }}
            </span>
          </div>
          <span class="pt-1.5 text-neutral-500">Hz</span>
          <input
            type="number"
            :step="levelStep"
            :value="displayLevel(tone.ampDbv)"
            :disabled="presetActive"
            class="w-16 rounded border border-neutral-300 px-1.5 py-1 disabled:opacity-60 dark:border-neutral-700"
            :class="NO_SPINNER"
            placeholder="Level"
            @change="onLevelEdit($event, tone.ampDbv, (v) => (tone.ampDbv = v))"
          />
          <GeneratorLevelUnitSelect class="mt-px" @change="emit('change')" />
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
            class="w-full rounded border border-neutral-300 px-2 py-1 text-xs dark:border-neutral-700"
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
            class="w-16 rounded border border-neutral-300 px-1.5 py-1 dark:border-neutral-700"
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
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @change="emit('change')"
        />
      </label>
      <label class="flex flex-col gap-1">
        Total RMS level (w/ 0 dB rolloff)
        <div class="flex items-center gap-2">
          <input
            type="number"
            :step="levelStep"
            :value="displayLevel(multitone.totalRmsDbv)"
            class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            :class="NO_SPINNER"
            @change="onLevelEdit($event, multitone.totalRmsDbv, (v) => (multitone.totalRmsDbv = v))"
          />
          <GeneratorLevelUnitSelect @change="emit('change')" />
        </div>
      </label>
      <!-- Rolloff stays in dB whatever the level unit is: it is a ratio per octave, not a level,
           and there is no volt figure for it to be expressed as. -->
      <label class="flex flex-col gap-1">
        Rolloff per octave (dB)
        <input
          type="number"
          v-model.number="multitone.rolloffDbPerOctave"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @change="emit('change')"
        />
      </label>
      <p class="text-xs text-neutral-400 dark:text-neutral-600">Fixed 5 Hz – 20 kHz band.</p>
    </div>

    <div v-else-if="kind === 'WhiteNoise'" class="space-y-2 text-sm">
      <label class="flex flex-col gap-1">
        Level
        <div class="flex items-center gap-2">
          <input
            type="number"
            :step="levelStep"
            :value="displayLevel(noise.levelDbv)"
            class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            :class="NO_SPINNER"
            @change="onLevelEdit($event, noise.levelDbv, (v) => (noise.levelDbv = v))"
          />
          <GeneratorLevelUnitSelect @change="emit('change')" />
        </div>
      </label>
    </div>

    <!-- Square and triangle share one form, as they share one settings block: the dropdown above is
         the shape. The reading beneath the frequency is not decoration -- the period is quantised to
         a whole even number of samples, so what plays is often not what was typed. -->
    <div v-else-if="kind === 'Square' || kind === 'Triangle'" class="space-y-2 text-sm">
      <label class="flex flex-col gap-1">
        Frequency (Hz)
        <input
          type="number"
          min="1"
          v-model.number="wave.freqHz"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @change="emit('change')"
        />
      </label>
      <label class="flex flex-col gap-1">
        Level (rms)
        <div class="flex items-center gap-2">
          <input
            type="number"
            :step="levelStep"
            :value="displayLevel(wave.levelDbv)"
            class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            :class="NO_SPINNER"
            @change="onLevelEdit($event, wave.levelDbv, (v) => (wave.levelDbv = v))"
          />
          <GeneratorLevelUnitSelect @change="emit('change')" />
        </div>
      </label>
      <div v-if="waveReading" class="space-y-1 text-xs text-neutral-500 dark:text-neutral-400">
        <p>
          <span :class="waveReading.quantised ? 'text-amber-600 dark:text-amber-400' : ''">
            Playing {{ waveReading.effectiveFreqHz.toFixed(3) }} Hz
          </span>
          ({{ waveReading.periodFrames }} frames per cycle, {{ waveReading.harmonics }} harmonics below
          Nyquist)
        </p>
        <p>
          {{ waveReading.peakV.toPrecision(4) }} V peak &middot; ideal harmonics
          <span v-for="(h, i) in waveReading.idealHarmonics" :key="h.order">
            <template v-if="i">, </template>H{{ h.order }} {{ h.dbc.toFixed(1) }} dBc
          </span>
        </p>
      </div>
      <p class="text-xs text-neutral-400 dark:text-neutral-600">
        Only whole even sample periods exist, so the frequency is rounded to one. THD+N, SNR and the
        noise tiles read the shape's own harmonics and show n/a.
      </p>
    </div>

    <div v-else-if="kind === 'EchoChirp'" class="space-y-2 text-sm">
      <label class="flex flex-col gap-1">
        Amplitude
        <div class="flex items-center gap-2">
          <input
            type="number"
            :step="levelStep"
            :value="displayLevel(chirp.amplitudeDbv)"
            class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            :class="NO_SPINNER"
            @change="onLevelEdit($event, chirp.amplitudeDbv, (v) => (chirp.amplitudeDbv = v))"
          />
          <GeneratorLevelUnitSelect @change="emit('change')" />
        </div>
      </label>
      <label class="flex flex-col gap-1">
        Start frequency (Hz)
        <input
          type="number"
          min="1"
          v-model.number="chirp.startFreqHz"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @change="emit('change')"
        />
      </label>
      <label class="flex flex-col gap-1">
        End frequency (Hz)
        <input
          type="number"
          v-model.number="chirp.endFreqHz"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @change="emit('change')"
        />
      </label>
      <label class="flex flex-col gap-1">
        Smoothing (octaves)
        <select
          v-model="chirp.smoothing"
          class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @change="emit('change')"
        >
          <option v-for="s in SMOOTHING" :key="s.value" :value="s.value">{{ s.label }}</option>
        </select>
      </label>
    </div>
  </div>
</template>
