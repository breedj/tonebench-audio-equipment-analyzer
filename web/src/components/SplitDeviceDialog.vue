<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import type { BenchClockDrift, DeviceRanges, DeviceStatus, SplitDeviceConfig, SplitHalfKindName } from '../api/client'
import { api } from '../api/client'
import ClockDriftDialog from './ClockDriftDialog.vue'
import Modal from './Modal.vue'

/**
 * Which two devices a split analyzer is made of, reached from the gear beside the status bar's
 * Analyzer picker — the same place and the same button the QA40x's and the sound card's settings
 * have.
 *
 * <p>The benches it exists for are a USB DAC playing while a QA403 captures (DAC measurements) and a
 * QA403 playing into a capture-only ADC. It is <b>not a quality step</b> over either device alone
 * and must not read as one: two devices are two clocks, and what that costs is set out below the
 * controls rather than left to be discovered on a trace.</p>
 *
 * <p><b>Which card</b> each half is, and its calibration, stay in the sound-card dialog: there is
 * one card document and a split simply uses one direction of it. So this dialog chooses the two
 * <i>kinds</i> and the generator's rate, and points at the other gear for the rest.</p>
 */
const props = defineProps<{ open: boolean; status: DeviceStatus | null; ranges: DeviceRanges | null }>()
const emit = defineEmits<{ close: []; soundCard: [] }>()

const HALVES: { value: SplitHalfKindName; label: string }[] = [
  { value: 'Hardware', label: 'QA40x' },
  { value: 'SoundCard', label: 'Sound card' },
  { value: 'Simulated', label: 'Simulator' },
]

/** Staged, and applied on Save: a pairing is two choices plus a rate, and half of one is not a bench. */
const form = reactive<SplitDeviceConfig>({
  inputKind: 'Hardware',
  outputKind: 'SoundCard',
  generatorSampleRateHz: 0,
})

const error = ref<string | null>(null)
const saving = ref(false)
const loaded = ref(false)
const clockDriftOpen = ref(false)

/**
 * What this bench's two clocks were last measured to differ by. Read from the server rather than
 * from any document here: the figure is keyed by the *pairing*, and only the session knows what
 * the bench is — which is the whole reason it could not live on the sound card's settings.
 */
const benchDrift = ref<BenchClockDrift | null>(null)

/** The pairing the server has, so a staged one can be told from it. */
const storedPairing = ref<{ generates: SplitHalfKindName; captures: SplitHalfKindName } | null>(null)

/**
 * Whether the two dropdowns have been turned away from what is saved.
 *
 * <p>The panel follows them, but the <em>run</em> cannot: it measures the bench the session has
 * open, which is the saved one. So the button is held until Save, rather than letting a press
 * measure one pairing and store it against the figure shown for another.</p>
 */
const pairingPending = computed(
  () =>
    storedPairing.value !== null
    && (storedPairing.value.generates !== form.outputKind || storedPairing.value.captures !== form.inputKind),
)

/**
 * Read for the pairing the dropdowns are <em>staging</em>, not the one the session has open.
 *
 * <p>This dialog applies on Save, so answered for the session's own bench the panel shows the
 * figure for the pairing being replaced — which is what it did: a reading taken with the card
 * generating stayed on screen, under a "Measured" summary, after the dropdowns were turned round
 * (reported 2026-11-13). The server resolves it, so BenchClockKey stays the one thing that decides
 * what a pairing is keyed by.</p>
 */
async function loadBenchDrift() {
  try {
    benchDrift.value = await api.getBenchClockDrift({
      generates: form.outputKind,
      captures: form.inputKind,
    })
  } catch {
    benchDrift.value = null
  }
}

const driftSummary = computed(() => {
  const d = benchDrift.value
  if (!d?.twoClocks) return null
  if (d.ppm === null || d.ppm === undefined) return 'Not measured on this pairing yet.'

  const when = d.measuredUtc ? new Date(d.measuredUtc).toLocaleString() : 'date not recorded'
  const spread = d.spreadPpm === null || d.spreadPpm === undefined ? '' : `, spread ${d.spreadPpm.toFixed(2)} ppm`
  return `${d.ppm.toFixed(2)} ppm${spread} — measured ${when}.`
})

/**
 * The run stores its figure the moment it ends — there is no Keep button, by design — so the panel
 * behind this dialog is a run out of date the moment it closes.
 */
async function onClockDriftClosed() {
  clockDriftOpen.value = false
  await loadBenchDrift()
}

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    error.value = null
    try {
      const stored = await api.getSplitDeviceConfig()
      form.inputKind = stored.inputKind
      form.outputKind = stored.outputKind
      form.generatorSampleRateHz = stored.generatorSampleRateHz
      storedPairing.value = { generates: stored.outputKind, captures: stored.inputKind }
      loaded.value = true
      await loadBenchDrift()
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    }
  },
  { immediate: true },
)

// The panel is the pairing's, so it has to be re-read when the pairing changes -- not only when
// the dialog opens. The generator rate is deliberately not in this list: a clock difference is a
// property of two crystals, and the rate one of them is clocked at does not change which two.
watch(
  () => [form.outputKind, form.inputKind],
  async () => {
    if (props.open && loaded.value) await loadBenchDrift()
  },
)

/**
 * The rates the generating half can be driven at. On a split these are deliberately not the
 * capture ladder — that is what makes a QA403 capturing at 384 kHz against a 192 kHz DAC reachable
 * — so they come from the server rather than being assumed here.
 */
const generatorRates = computed(() => props.ranges?.outputSampleRatesHz ?? props.ranges?.sampleRatesHz ?? [])

/**
 * The three pairings the server refuses, checked here so the reason is on screen before Save rather
 * than after it. The server refuses them again regardless — this is the message, not the guard.
 */
const problem = computed(() => {
  if (form.inputKind === 'SoundCard' && form.outputKind === 'SoundCard') {
    return 'A sound card cannot be on both sides: everything PortAudio does runs on one thread, so the second card would never be serviced. Choose Sound card as the analyzer instead — it opens both directions of a two-card bench in one stream.'
  }
  if (form.inputKind === 'Hardware' && form.outputKind === 'Hardware') {
    return 'A QA40x cannot be on both sides: that is one device opened twice. A QA40x already generates and captures on its own clock.'
  }
  return null
})

const usesSoundCard = computed(() => form.inputKind === 'SoundCard' || form.outputKind === 'SoundCard')

/** What the bench is, in a sentence, so the two dropdowns cannot be read the wrong way round. */
const summary = computed(() => {
  const generator = HALVES.find((h) => h.value === form.outputKind)?.label ?? form.outputKind
  const capture = HALVES.find((h) => h.value === form.inputKind)?.label ?? form.inputKind
  const rate =
    form.generatorSampleRateHz > 0
      ? ` at ${(form.generatorSampleRateHz / 1000).toFixed(form.generatorSampleRateHz % 1000 === 0 ? 0 : 1)} kHz`
      : ''
  return `${generator} generates${rate}; ${capture} captures.`
})

/**
 * What a slower generator costs, said before the run rather than after it. A sweep has to stop
 * inside the generator's own Nyquist, so the band above roughly 0.4 of the generator's rate is a
 * dead band however fast the capture is; and a *faster* generator hides nothing — whatever it does
 * above the capture's Nyquist aliases into the measurement.
 */
const rateNote = computed(() => {
  if (form.generatorSampleRateHz <= 0) return null
  const captureRates = props.ranges?.sampleRatesHz ?? []
  const fastest = captureRates.length > 0 ? Math.max(...captureRates) : 0
  if (fastest > 0 && form.generatorSampleRateHz < fastest) {
    const reach = (0.4 * form.generatorSampleRateHz) / 1000
    return `A sweep has to stop inside the generator's own Nyquist, so the stimulus reaches about ${reach.toFixed(1)} kHz and everything above that is a dead band — which is the point when the DAC is what is being tested.`
  }
  if (fastest > 0 && form.generatorSampleRateHz > fastest) {
    return 'Whatever the DAC does above the capture’s Nyquist is invisible — and worse, it aliases into the measurement. For that the answer is a faster ADC and nothing else.'
  }
  return null
})

async function save() {
  if (problem.value) return
  saving.value = true
  try {
    await api.setSplitDeviceConfig({ ...form })
    error.value = null
    emit('close')
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Modal
    :open="props.open"
    title="Split device settings"
    icon="soundcard"
    tone="violet"
    ground
    flush
    @close="emit('close')"
  >
    <div class="flex flex-col gap-3.5 p-4">
      <p class="m-0 text-[12.5px] leading-relaxed text-neutral-600 dark:text-neutral-400">
        Two devices as one analyzer: one supplies the DAC, the other the ADC. For measuring a USB DAC
        with the QA40x, or for driving a capture-only ADC from the QA40x's generator.
      </p>

      <section class="dialog-card">
        <div class="sc-row">
          <span class="sc-label">Generates</span>
          <select v-model="form.outputKind" class="tb-input w-64" title="The device the stimulus is played from.">
            <option v-for="half in HALVES" :key="half.value" :value="half.value">{{ half.label }}</option>
          </select>
        </div>
        <div class="sc-row">
          <span class="sc-label">Captures</span>
          <select v-model="form.inputKind" class="tb-input w-64" title="The device the measurement is taken with. Its input range, its ranging and its drop counters are the bench's.">
            <option v-for="half in HALVES" :key="half.value" :value="half.value">{{ half.label }}</option>
          </select>
        </div>
        <div class="sc-row">
          <span class="sc-label">Generator rate</span>
          <select
            v-model.number="form.generatorSampleRateHz"
            class="tb-input w-64"
            title="The rate the generating half runs at. Following the capture rate makes a split behave exactly like a single device. A different rate has to be an integer ratio of the capture rate, which every rate here is."
          >
            <option :value="0">Follow the capture rate</option>
            <option v-for="rate in generatorRates" :key="rate" :value="rate">
              {{ (rate / 1000).toFixed(rate % 1000 === 0 ? 0 : 1) }} kHz
            </option>
          </select>
        </div>
        <p class="m-0 border-t border-neutral-100 px-3.5 py-3 text-[12.5px] leading-relaxed text-neutral-600 dark:border-neutral-800 dark:text-neutral-400">
          {{ summary }}
        </p>
      </section>

      <p v-if="problem" class="m-0 rounded-md bg-red-50 px-3 py-2 text-[12.5px] leading-relaxed text-red-800 dark:bg-red-950/40 dark:text-red-300">
        {{ problem }}
      </p>

      <p v-if="rateNote" class="m-0 rounded-md bg-amber-50 px-3 py-2 text-[12.5px] leading-relaxed text-amber-900 dark:bg-amber-950/40 dark:text-amber-300">
        {{ rateNote }}
      </p>

      <!-- Said here rather than left to a trace: the chirp's failure mode is a plausible-looking
           delay, which this codebase has already paid for once. -->
      <section class="dialog-card">
        <p class="m-0 p-3.5 text-[12.5px] leading-relaxed text-neutral-600 dark:text-neutral-400">
          <span class="font-semibold text-neutral-800 dark:text-neutral-200">Two devices are two clocks.</span>
          Tone measurements — level, THD, IMD, SNR, the stepped sweeps — are unaffected, because every
          one of them finds its tone by searching for it. A chirp's phase, group delay and impulse
          response are not: the clock ratio reads as a frequency-dependent delay. Measure the drift in
          the sound-card settings and the two chirp screens will offer to correct it, or use
          <em>Right channel is reference</em>, where the ratio of two channels of one capture cancels
          it exactly.
        </p>
      </section>

      <p v-if="usesSoundCard" class="m-0 text-[12.5px] leading-relaxed text-neutral-600 dark:text-neutral-400">
        Which card, its channel pair and its calibration are in
        <button type="button" class="font-semibold text-blue-700 underline underline-offset-2 dark:text-blue-300" @click="emit('soundCard')">
          Sound card settings</button>. A split uses one direction of that one document, so only the
        side it needs has to be chosen there.
      </p>

      <!-- Rendered only once the pairing really is two devices. On a bench whose two halves are one
           device there is one clock, nothing to measure, and the test would read zero by
           construction -- a panel saying so would be a section about a problem this bench does not
           have. -->
      <section v-if="benchDrift?.twoClocks" class="dialog-card">
        <div class="flex items-center gap-2 border-b border-neutral-100 p-3.5 dark:border-neutral-800">
          <span class="flex-1 text-sm font-semibold">Clock drift</span>
          <button
            type="button"
            class="btn-chip"
            :style="{ backgroundColor: 'var(--kind-violet-chip-bg)', color: 'var(--kind-violet-chip-fg)' }"
            :disabled="pairingPending"
            :title="
              pairingPending
                ? 'Save the pairing first: the test measures the bench the app has open, which is still the saved one.'
                : `Plays a tone on the generating device and reads it back on the capturing one. It measures through whatever is connected, because a DUT changes a tone's level and phase but not its frequency.`
            "
            @click="clockDriftOpen = true"
          >
            {{ benchDrift?.ppm === null || benchDrift?.ppm === undefined ? 'Measure the drift' : 'Measure again' }}
          </button>
        </div>
        <p class="m-0 p-3.5 text-[12.5px] leading-relaxed text-neutral-600 dark:text-neutral-400">
          {{ driftSummary }}
          Measured, the two chirp screens offer to take the clock's delay ramp off a sweep.
          <template v-if="pairingPending">
            This is the pairing above, not the one the app has open — press Save before measuring it.
          </template>
        </p>
      </section>

      <p v-if="error" class="m-0 rounded-md bg-red-50 px-3 py-2 text-[12.5px] text-red-800 dark:bg-red-950/40 dark:text-red-300">
        {{ error }}
      </p>
    </div>

    <ClockDriftDialog :open="clockDriftOpen" :pairing-note="summary" @close="onClockDriftClosed" />

    <template #footer>
      <button type="button" class="btn-secondary" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" :disabled="saving || problem !== null || !loaded" @click="save">
        Save
      </button>
    </template>
  </Modal>
</template>
