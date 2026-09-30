<script setup lang="ts">
import { computed, onUnmounted, reactive, ref, watch } from 'vue'
import type { SoundCardConfig, VoltageEntryUnitName } from '../api/client'
import { useSoundCard } from '../composables/useSoundCard'
import Modal from './Modal.vue'

/**
 * Picks the card and calibrates it.
 *
 * <p>A dialog rather than a panel because both calibration steps have a <b>physical
 * precondition</b> — a voltmeter on the output for one, a source of known voltage on the input for
 * the other — and stating those is most of what this component is for. The sequencing is
 * server-side (see SoundCardCalibrationRunner): a tone playing is a measurement in progress, and a
 * sweep started over it would take the generator away mid-step.</p>
 */
const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const {
  config,
  inventory,
  probe,
  calibrationResult,
  playingDbFs,
  busy,
  error,
  isCalibrated,
  load,
  loadDevices,
  probePairing,
  save,
  startTone,
  stopTone,
  syncCalibrationState,
  applyOutput,
  measureInput,
  measureInputViaLoopback,
  levels,
  watchLevels,
} = useSoundCard()

/** ARTA's levels, and the server's. */
const OUTPUT_LEVELS = [-3, -6, -9, -12, -20]

const form = reactive({
  /** Which physical card, as "host API\u0000interface". Both device lists are filtered to it. */
  cardKey: '',
  inputKey: '',
  outputKey: '',
  inputChannelOffset: 0,
  outputChannelOffset: 0,
  minStimulusFrequencyHz: 20,
})

const outputStep = reactive({ dbFs: -6, reading: 1000, unit: 'Rms' as VoltageEntryUnitName })
const inputStep = reactive({
  /**
   * Where the known voltage comes from. 'External' is ARTA's: a second signal generator. 'Loopback'
   * uses the card's own output through a patch cable, which is only possible once the output has
   * been calibrated — and is the easier of the two on a bench that has a cable.
   */
  source: 'External' as 'External' | 'Loopback',
  known: 500,
  unit: 'Rms' as VoltageEntryUnitName,
  channel: 'Left' as 'Left' | 'Right',
  dbFs: -20,
})

const saved = ref(false)
let stopLevels: (() => void) | null = null

onUnmounted(() => stopLevels?.())

/** Where a level sits on the bar, as a fraction of a −90…0 dBFS scale. */
function barFraction(dbFs: number | null | undefined) {
  if (dbFs == null || !Number.isFinite(dbFs)) return 0
  return Math.max(0, Math.min(1, (dbFs + 90) / 90))
}

function barLabel(dbFs: number | null | undefined) {
  return dbFs == null || !Number.isFinite(dbFs) ? '—' : `${dbFs.toFixed(1)} dBFS`
}

/** Host API and name together: the same device name exists under several host APIs, and they are different devices. */
function keyOf(hostApi: string, name: string) {
  return `${hostApi}\u0000${name}`
}

/** The card a saved device belongs to, found in the listing rather than re-derived here. */
function cardKeyFor(hostApi: string, deviceName: string) {
  const match = [...(inventory.value?.inputs ?? []), ...(inventory.value?.outputs ?? [])].find(
    (o) => o.hostApi === hostApi && o.name === deviceName,
  )
  return match ? keyOf(match.hostApi, match.interfaceName) : ''
}

function splitKey(key: string) {
  const [hostApi = '', name = ''] = key.split('\u0000')
  return { hostApi, name }
}

/**
 * The cards the machine has, each a host API and an interface within it.
 *
 * Picking the card first is what guarantees the input and the output are the same piece of
 * hardware — and therefore one clock. Two cards run on two, and the drift between them is not
 * reported as a fault but as DUT response.
 */
const cards = computed(() => {
  const seen = new Map<string, { key: string; hostApi: string; interfaceName: string; trust: string; duplex: boolean; preference: number }>()

  for (const o of [...(inventory.value?.inputs ?? []), ...(inventory.value?.outputs ?? [])]) {
    const key = keyOf(o.hostApi, o.interfaceName)
    const existing = seen.get(key)
    if (existing) {
      existing.duplex ||= o.isFullDuplex
      continue
    }

    seen.set(key, {
      key,
      hostApi: o.hostApi,
      interfaceName: o.interfaceName,
      trust: o.trust,
      duplex: o.isFullDuplex,
      preference: o.preference,
    })
  }

  return [...seen.values()].sort(
    (a, b) => a.preference - b.preference || a.interfaceName.localeCompare(b.interfaceName),
  )
})

/** Only this card's devices, so the two sides cannot be put on different hardware. */
const cardInputs = computed(() =>
  (inventory.value?.inputs ?? []).filter((o) => keyOf(o.hostApi, o.interfaceName) === form.cardKey),
)
const cardOutputs = computed(() =>
  (inventory.value?.outputs ?? []).filter((o) => keyOf(o.hostApi, o.interfaceName) === form.cardKey),
)

const selectedInput = computed(() =>
  cardInputs.value.find((o) => keyOf(o.hostApi, o.name) === form.inputKey) ?? null,
)
const selectedOutput = computed(() =>
  cardOutputs.value.find((o) => keyOf(o.hostApi, o.name) === form.outputKey) ?? null,
)

// Choosing a card picks its only input and output for you, which on an ASIO card is the whole job:
// there is one device and it carries both directions.
watch(
  () => form.cardKey,
  () => {
    if (!cardInputs.value.some((o) => keyOf(o.hostApi, o.name) === form.inputKey))
      form.inputKey = cardInputs.value.length > 0 ? keyOf(cardInputs.value[0]!.hostApi, cardInputs.value[0]!.name) : ''

    if (!cardOutputs.value.some((o) => keyOf(o.hostApi, o.name) === form.outputKey))
      form.outputKey = cardOutputs.value.length > 0 ? keyOf(cardOutputs.value[0]!.hostApi, cardOutputs.value[0]!.name) : ''
  },
)

/** Channel pairs the chosen device can actually offer, as offsets. */
const inputPairs = computed(() => pairsFor(selectedInput.value?.maxInputChannels ?? 0))
const outputPairs = computed(() => pairsFor(selectedOutput.value?.maxOutputChannels ?? 0))

function pairsFor(channels: number) {
  const pairs: { offset: number; label: string }[] = []
  for (let offset = 0; offset + 2 <= channels; offset += 2)
    pairs.push({ offset, label: `${offset + 1}/${offset + 2}` })
  return pairs
}

const rates = computed(() => probe.value?.sampleRatesHz ?? [])

/**
 * The one warning that matters: two endpoints of different interfaces run on different clocks, and
 * the drift is reported as DUT response rather than as a fault. Advisory because it has to be —
 * WDM-KS device names carry no interface name at all, so a correct pairing there is indistinguishable
 * from a wrong one.
 */
const pairingWarning = computed(() => {
  if (!probe.value || !selectedInput.value || !selectedOutput.value) return null
  if (probe.value.pairing !== 'Unknown') return null
  return 'These may not be the same interface. Two cards run on two clocks, and the drift between them is reported as DUT response — nothing downstream can tell the difference.'
})

/** DirectSound and MME accept any rate by resampling in software, so their numbers are the mixer's. */
const trustWarning = computed(() => {
  const bad = [selectedInput.value, selectedOutput.value].filter((o) => o?.trust === 'Resamples')
  if (bad.length === 0) return null
  return 'This host API resamples in software: it will accept any rate, but what the converter ran at is the Windows mixer’s, not what is asked for. Prefer ASIO where the card has a driver.'
})

const canSave = computed(
  () => !!selectedInput.value && !!selectedOutput.value && rates.value.length > 0 && !busy.value,
)

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    saved.value = false
    // The tone state comes from the server, not from what this tab last did: another tab, or this
    // one before a reload, may have left one playing.
    await Promise.all([load(true), loadDevices(), syncCalibrationState()])
    stopLevels?.()
    stopLevels = watchLevels()
    if (config.value) {
      form.cardKey = cardKeyFor(config.value.inputHostApi, config.value.inputDeviceName)
      form.inputKey = keyOf(config.value.inputHostApi, config.value.inputDeviceName)
      form.outputKey = keyOf(config.value.outputHostApi, config.value.outputDeviceName)
      form.inputChannelOffset = config.value.inputChannelOffset
      form.outputChannelOffset = config.value.outputChannelOffset
      form.minStimulusFrequencyHz = config.value.minStimulusFrequencyHz
    }
    await reprobe()
  },
  { immediate: true },
)

// Re-probed on every change of either side or either offset, because the answer depends on all four
// and a rate list left over from the previous card would be a lie.
watch(
  () => [form.inputKey, form.outputKey, form.inputChannelOffset, form.outputChannelOffset].join('|'),
  () => void reprobe(),
)

async function reprobe() {
  const draft = toConfig()
  if (!draft) {
    return
  }

  await probePairing(draft)
}

function toConfig(): SoundCardConfig | null {
  if (!config.value) return null
  const input = splitKey(form.inputKey)
  const output = splitKey(form.outputKey)
  return {
    ...config.value,
    inputHostApi: input.hostApi,
    inputDeviceName: input.name,
    outputHostApi: output.hostApi,
    outputDeviceName: output.name,
    inputChannelOffset: form.inputChannelOffset,
    outputChannelOffset: form.outputChannelOffset,
    minStimulusFrequencyHz: form.minStimulusFrequencyHz,
  }
}

async function applyDevice() {
  const next = toConfig()
  if (!next) return
  saved.value = await save(next)
}

async function toggleTone() {
  if (playingDbFs.value !== null) await stopTone()
  else await startTone(outputStep.dbFs)
}

async function acceptOutput() {
  await applyOutput(outputStep.reading, outputStep.unit)
}

async function acceptInput() {
  if (inputStep.source === 'Loopback') await measureInputViaLoopback(inputStep.dbFs)
  else await measureInput(inputStep.known, inputStep.unit, inputStep.channel)
}

/** The loopback source needs the output's own calibration to know what it is playing. */
const outputCalibrated = computed(() => config.value?.outputFullScalePeakMv != null)

const canMeasureInput = computed(
  () => !busy.value && (inputStep.source === 'External' || outputCalibrated.value),
)

// The tone must never outlive the dialog: it is the generator, and the user has walked away.
async function close() {
  stopLevels?.()
  stopLevels = null
  if (playingDbFs.value !== null) await stopTone()
  emit('close')
}

function mv(value: number | null | undefined) {
  return value == null ? '—' : value.toFixed(1)
}
</script>

<template>
  <Modal :open="open" title="Sound card" wide @close="close">
    <div class="flex flex-col gap-5 text-xs">
      <p
        v-if="inventory && !inventory.available"
        class="rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
      >
        The audio library would not start: {{ inventory.problem }}
      </p>

      <!-- 1. Which card ------------------------------------------------------------------ -->
      <section class="flex flex-col gap-2">
        <div class="flex items-center justify-between">
          <h3 class="font-medium text-neutral-700 dark:text-neutral-200">Device</h3>
          <button
            type="button"
            class="rounded border border-neutral-300 px-2 py-0.5 text-[11px] hover:bg-neutral-100 disabled:opacity-50 dark:border-neutral-700"
            :disabled="busy"
            title="Re-read the device list. A newly plugged ASIO interface needs the app restarted — PortAudio reads its list once at startup."
            @click="loadDevices(true)"
          >
            Refresh
          </button>
        </div>

        <!-- The card first, the channels second. Picking the interface is what guarantees both
             directions are one piece of hardware, and therefore one clock: two cards run on two,
             and the drift between them is reported as DUT response rather than as a fault. -->
        <label class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Sound card</span>
          <select
            v-model="form.cardKey"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option value="">—</option>
            <option v-for="c in cards" :key="c.key" :value="c.key">
              {{ c.interfaceName }} — {{ c.hostApi }}{{ c.duplex ? ' (full duplex)' : '' }}
            </option>
          </select>
        </label>

        <label v-if="cardInputs.length > 1" class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Input</span>
          <select
            v-model="form.inputKey"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option
              v-for="o in cardInputs"
              :key="keyOf(o.hostApi, o.name)"
              :value="keyOf(o.hostApi, o.name)"
            >
              {{ o.name }}
            </option>
          </select>
        </label>

        <label v-if="inputPairs.length > 1" class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Input channels</span>
          <select
            v-model.number="form.inputChannelOffset"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option v-for="pair in inputPairs" :key="pair.offset" :value="pair.offset">{{ pair.label }}</option>
          </select>
        </label>

        <label v-if="cardOutputs.length > 1" class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Output</span>
          <select
            v-model="form.outputKey"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option
              v-for="o in cardOutputs"
              :key="keyOf(o.hostApi, o.name)"
              :value="keyOf(o.hostApi, o.name)"
            >
              {{ o.name }}
            </option>
          </select>
        </label>

        <label v-if="outputPairs.length > 1" class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Output channels</span>
          <select
            v-model.number="form.outputChannelOffset"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option v-for="pair in outputPairs" :key="pair.offset" :value="pair.offset">{{ pair.label }}</option>
          </select>
        </label>

<!-- Read-only, deliberately. The rate a measurement runs at is the measurement's, and every
             screen's rate and span dropdowns are already filtered to this list -- so a selector here
             would be a second place to set one thing, and the one that nothing read. What the probe
             is for is telling you what the card can do before you commit to it. -->
        <div class="flex items-start justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Sample rates</span>
          <span class="w-96">
            <span class="font-mono">{{
              rates.length > 0 ? rates.map((r) => `${r / 1000}`).join(', ') + ' kHz' : '—'
            }}</span>
            <span class="mt-0.5 block text-xs text-neutral-500">
              {{
                rates.length > 0
                  ? 'Chosen per measurement — the screens offer only these.'
                  : 'Pick a card to probe what it accepts.'
              }}
            </span>
          </span>
        </div>

        <label
          class="flex items-center justify-between gap-3"
          title="Sound cards are AC-coupled at both ends. Below this a sweep measures the card's own high-pass and reports it as the DUT's."
        >
          <span class="text-neutral-600 dark:text-neutral-300">Lowest usable frequency</span>
          <span class="flex w-96 items-center gap-2">
            <input
              v-model.number="form.minStimulusFrequencyHz"
              type="number"
              min="0"
              max="1000"
              step="1"
              class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            />
            <span class="text-neutral-500">Hz</span>
          </span>
        </label>

        <p
          v-if="probe && rates.length === 0 && form.inputKey && form.outputKey"
          class="rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
        >
          {{ probe.problem ?? 'This pairing accepts none of 48, 96 or 192 kHz, which are the rates this analyzer measures at.' }}
        </p>

        <p
          v-if="pairingWarning"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          {{ pairingWarning }}
        </p>

        <p
          v-if="trustWarning"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          {{ trustWarning }}
        </p>

        <div class="flex items-center gap-3">
          <button
            type="button"
            class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-50 dark:bg-neutral-100 dark:text-neutral-900"
            :disabled="!canSave"
            @click="applyDevice"
          >
            Use this card
          </button>
          <span v-if="saved" class="text-emerald-700 dark:text-emerald-400">Saved.</span>
        </div>
      </section>

      <!-- 2. Output full scale ---------------------------------------------------------- -->
      <section class="flex flex-col gap-2 border-t border-neutral-200 pt-4 dark:border-neutral-800">
        <h3 class="font-medium text-neutral-700 dark:text-neutral-200">Full scale output (mV)</h3>
        <p class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200">
          <strong>Put a voltmeter across the output channel.</strong> Play the tone, read the meter,
          and enter what it says. That fixes what the card's digital full scale is worth in volts.
        </p>

        <label class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Output level</span>
          <select
            v-model.number="outputStep.dbFs"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            :disabled="playingDbFs !== null"
          >
            <option v-for="level in OUTPUT_LEVELS" :key="level" :value="level">{{ level }} dB</option>
          </select>
        </label>

        <div class="flex items-center gap-3">
          <button
            type="button"
            class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-100 disabled:opacity-50 dark:border-neutral-700"
            :disabled="busy"
            @click="toggleTone"
          >
            {{ playingDbFs !== null ? 'Stop tone' : 'Generate sine (400 Hz)' }}
          </button>
          <span v-if="playingDbFs !== null" class="text-emerald-700 dark:text-emerald-400">
            Playing at {{ playingDbFs }} dBFS
          </span>
        </div>

        <label class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Voltmeter reads</span>
          <span class="flex w-96 items-center gap-2">
            <input
              v-model.number="outputStep.reading"
              type="number"
              min="0"
              step="1"
              class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            />
            <select
              v-model="outputStep.unit"
              class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            >
              <option value="Rms">mV rms</option>
              <option value="Peak">mV peak</option>
            </select>
          </span>
        </label>

        <div class="flex items-center gap-3">
          <button
            type="button"
            class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-50 dark:bg-neutral-100 dark:text-neutral-900"
            :disabled="busy || playingDbFs === null"
            @click="acceptOutput"
          >
            Estimate peak output mV
          </button>
          <span class="font-mono text-neutral-600 dark:text-neutral-300">
            current {{ mv(config?.outputFullScalePeakMv) }} mV
          </span>
        </div>
      </section>

      <!-- 3. Input full scale ----------------------------------------------------------- -->
      <section class="flex flex-col gap-2 border-t border-neutral-200 pt-4 dark:border-neutral-800">
        <h3 class="font-medium text-neutral-700 dark:text-neutral-200">Full scale input (mV)</h3>
        <label class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Known source</span>
          <select
            v-model="inputStep.source"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option value="External">A generator of known voltage</option>
            <option value="Loopback">This card's output, through a patch cable</option>
          </select>
        </label>

        <p
          v-if="inputStep.source === 'External'"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          <strong>Feed the chosen input from a source whose voltage you know</strong>, and enter that
          voltage. Each channel is measured on its own, so run this once per side. The generator is
          silenced while it measures — anything this app played would be measured along with it.
        </p>
        <p
          v-else
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          <strong>Patch both outputs straight back to their own inputs</strong>, then measure.
          Both channels are done in one press: the two figures are then measured under identical
          conditions, and the difference between them — the reading that catches a wiring mistake —
          is available immediately. Once the output is calibrated the app knows what it is playing in
          volts, so the cables are a source of known voltage and no second generator is needed. Note
          the figures are linked: any error in the output calibration lands directly in these, where
          a separate generator would be independent of it.
        </p>
        <p
          v-if="inputStep.source === 'Loopback' && !outputCalibrated"
          class="rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
        >
          Calibrate the output first — until then the app does not know what voltage it is playing,
          and the result would look measured without being it.
        </p>

        <label v-if="inputStep.source === 'External'" class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Input channel</span>
          <select
            v-model="inputStep.channel"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option value="Left">Left</option>
            <option value="Right">Right</option>
          </select>
        </label>

        <label v-if="inputStep.source === 'External'" class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Source voltage</span>
          <span class="flex w-96 items-center gap-2">
            <input
              v-model.number="inputStep.known"
              type="number"
              min="0"
              step="1"
              class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            />
            <select
              v-model="inputStep.unit"
              class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            >
              <option value="Rms">mV rms</option>
              <option value="Peak">mV peak</option>
            </select>
          </span>
        </label>

        <label v-else class="flex items-center justify-between gap-3">
          <span
            class="text-neutral-600 dark:text-neutral-300"
            title="Quiet enough that the input cannot clip, loud enough to sit well clear of the card's noise floor."
          >
            Play at
          </span>
          <select
            v-model.number="inputStep.dbFs"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option v-for="level in OUTPUT_LEVELS" :key="level" :value="level">{{ level }} dB</option>
          </select>
        </label>

        <div class="flex items-center gap-3">
          <button
            type="button"
            class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-50 dark:bg-neutral-100 dark:text-neutral-900"
            :disabled="!canMeasureInput"
            @click="acceptInput"
          >
            Estimate peak input mV
          </button>
          <span v-if="calibrationResult?.measuredDbFs != null" class="text-neutral-500">
            measured {{ calibrationResult.measuredDbFs.toFixed(2) }} dBFS
          </span>
        </div>

        <!-- What each input is hearing, live. The scale is −90…0 dBFS, which is the range a
             calibration cares about: the clip point at the top, the card's noise floor off the
             bottom. Red past −0.5 dBFS, because a reading taken at the clip point is not the
             source's level and the step refuses it. -->
        <div class="mt-2 flex flex-col gap-1">
          <div v-for="side in (['Left', 'Right'] as const)" :key="side" class="flex items-center gap-2">
            <span class="w-10 text-[11px] uppercase tracking-wide text-neutral-500">{{ side }}</span>
            <div class="relative h-3 flex-1 overflow-hidden rounded border border-neutral-300 dark:border-neutral-700">
              <div
                class="h-full transition-[width] duration-75"
                :class="
                  (side === 'Left' ? levels?.leftDbFs : levels?.rightDbFs) != null &&
                  (side === 'Left' ? levels!.leftDbFs! : levels!.rightDbFs!) > -0.5
                    ? 'bg-red-500'
                    : 'bg-emerald-500'
                "
                :style="{
                  width: `${barFraction(side === 'Left' ? levels?.leftDbFs : levels?.rightDbFs) * 100}%`,
                }"
              />
            </div>
            <span class="w-20 text-right font-mono text-[11px] text-neutral-600 dark:text-neutral-300">
              {{ barLabel(side === 'Left' ? levels?.leftDbFs : levels?.rightDbFs) }}
            </span>
          </div>
          <div class="flex justify-between pl-12 pr-20 text-[10px] text-neutral-400">
            <span>−90</span><span>−60</span><span>−30</span><span>0 dBFS</span>
          </div>
          <p v-if="levels && !levels.acquiring" class="pl-12 text-[10px] text-neutral-400">
            Idle — the bars move while a tone is playing or a measurement is running.
          </p>
        </div>

        <table class="mt-1 w-full max-w-md text-left font-mono">
          <thead class="text-neutral-500">
            <tr>
              <th class="font-normal"></th>
              <th class="font-normal">Full scale</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td class="pr-4 text-neutral-600 dark:text-neutral-300">Left</td>
              <td>{{ mv(config?.inputFullScalePeakMvLeft) }} mV</td>
            </tr>
            <tr>
              <td class="pr-4 text-neutral-600 dark:text-neutral-300">Right</td>
              <td>{{ mv(config?.inputFullScalePeakMvRight) }} mV</td>
            </tr>
            <tr v-if="calibrationResult?.channelDifferenceDb != null">
              <td
                class="pr-4 text-neutral-600 dark:text-neutral-300"
                title="How far apart the two inputs are. A few tenths is two ordinary channels; several dB is a wiring or channel-pair mistake."
              >
                Diff
              </td>
              <td>{{ calibrationResult.channelDifferenceDb.toFixed(4) }} dB</td>
            </tr>
          </tbody>
        </table>
      </section>

      <p
        v-if="!isCalibrated"
        class="text-[11px] text-neutral-500 dark:text-neutral-400"
      >
        Until both steps are done the analyzer reports <strong>dBFS rather than volts</strong>. Every
        ratio — THD, IMD, crosstalk, the shape of a frequency response — is exact regardless, because
        a consistent full-scale constant cancels out of it.
      </p>

      <p v-if="error" class="text-red-600 dark:text-red-400">{{ error }}</p>

      <div class="flex justify-end">
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-100 dark:border-neutral-700"
          @click="close"
        >
          Close
        </button>
      </div>
    </div>
  </Modal>
</template>
