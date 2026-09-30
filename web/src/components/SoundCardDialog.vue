<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
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
} = useSoundCard()

/** ARTA's levels, and the server's. */
const OUTPUT_LEVELS = [-3, -6, -9, -12, -20]

const form = reactive({
  inputKey: '',
  outputKey: '',
  inputChannelOffset: 0,
  outputChannelOffset: 0,
  sampleRateHz: 48000,
  minStimulusFrequencyHz: 20,
})

const outputStep = reactive({ dbFs: -6, reading: 1000, unit: 'Rms' as VoltageEntryUnitName })
const inputStep = reactive({ known: 500, unit: 'Rms' as VoltageEntryUnitName, channel: 'Left' as 'Left' | 'Right' })

const saved = ref(false)

/** Host API and name together: the same device name exists under several host APIs, and they are different devices. */
function keyOf(hostApi: string, name: string) {
  return `${hostApi}\u0000${name}`
}

function splitKey(key: string) {
  const [hostApi = '', name = ''] = key.split('\u0000')
  return { hostApi, name }
}

const selectedInput = computed(() =>
  inventory.value?.inputs.find((o) => keyOf(o.hostApi, o.name) === form.inputKey) ?? null,
)
const selectedOutput = computed(() =>
  inventory.value?.outputs.find((o) => keyOf(o.hostApi, o.name) === form.outputKey) ?? null,
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
    if (config.value) {
      form.inputKey = keyOf(config.value.inputHostApi, config.value.inputDeviceName)
      form.outputKey = keyOf(config.value.outputHostApi, config.value.outputDeviceName)
      form.inputChannelOffset = config.value.inputChannelOffset
      form.outputChannelOffset = config.value.outputChannelOffset
      form.sampleRateHz = config.value.sampleRateHz
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
  if (rates.value.length > 0 && !rates.value.includes(form.sampleRateHz))
    form.sampleRateHz = rates.value[0]!
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
    sampleRateHz: form.sampleRateHz,
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
  await measureInput(inputStep.known, inputStep.unit, inputStep.channel)
}

// The tone must never outlive the dialog: it is the generator, and the user has walked away.
async function close() {
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

        <label class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Input</span>
          <select
            v-model="form.inputKey"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option value="">—</option>
            <option
              v-for="o in inventory?.inputs ?? []"
              :key="keyOf(o.hostApi, o.name)"
              :value="keyOf(o.hostApi, o.name)"
            >
              {{ o.hostApi }} — {{ o.name }}{{ o.isFullDuplex ? ' (full duplex)' : '' }}
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

        <label class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Output</span>
          <select
            v-model="form.outputKey"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option value="">—</option>
            <option
              v-for="o in inventory?.outputs ?? []"
              :key="keyOf(o.hostApi, o.name)"
              :value="keyOf(o.hostApi, o.name)"
            >
              {{ o.hostApi }} — {{ o.name }}{{ o.isFullDuplex ? ' (full duplex)' : '' }}
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

        <label class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Sample rate</span>
          <select
            v-model.number="form.sampleRateHz"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option v-if="rates.length === 0" :value="form.sampleRateHz">—</option>
            <option v-for="rate in rates" :key="rate" :value="rate">{{ rate / 1000 }} kHz</option>
          </select>
        </label>

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
        <p class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200">
          <strong>Feed the chosen input from a source whose voltage you know</strong>, and enter that
          voltage. Each channel is measured on its own, so run this once per side. The generator is
          silenced while it measures — anything this app played would be measured along with it.
        </p>

        <label class="flex items-center justify-between gap-3">
          <span class="text-neutral-600 dark:text-neutral-300">Input channel</span>
          <select
            v-model="inputStep.channel"
            class="w-96 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          >
            <option value="Left">Left</option>
            <option value="Right">Right</option>
          </select>
        </label>

        <label class="flex items-center justify-between gap-3">
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

        <div class="flex items-center gap-3">
          <button
            type="button"
            class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-50 dark:bg-neutral-100 dark:text-neutral-900"
            :disabled="busy"
            @click="acceptInput"
          >
            Estimate peak input mV
          </button>
          <span v-if="calibrationResult?.measuredDbFs != null" class="text-neutral-500">
            measured {{ calibrationResult.measuredDbFs.toFixed(2) }} dBFS
          </span>
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
