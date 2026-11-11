<script setup lang="ts">
import { computed, onUnmounted, reactive, ref, watch } from 'vue'
import type { SoundCardConfig, VoltageEntryUnitName } from '../api/client'
import { useSoundCard } from '../composables/useSoundCard'
import { useManual } from '../manual/useManual'
import {
  buildCards,
  cardsOffering,
  clampChannelOffset,
  devicePairing,
  hostApiNotes,
  keyOf,
  reselectCardKey,
  savedCardNeedsAdvanced,
  splitKey,
} from '../soundcard/cardList'
import { calibrationState } from '../soundcard/calibrationState'
import Modal from './Modal.vue'
import PanelIcon from './PanelIcon.vue'
import HeaderIcon from './HeaderIcon.vue'

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
  calibrationToneHz,
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
  controlPanelOpen,
  showControlPanel,
  devicesLoading,
} = useSoundCard()

const { openManual } = useManual()

/** ARTA's levels, and the server's. */
const OUTPUT_LEVELS = [-3, -6, -9, -12, -20]

const form = reactive({
  /** Which physical card, as "host API\u0000interface". Both device lists are filtered to it. */
  cardKey: '',

  /**
   * Whether the two directions may sit on different cards — off by default, and that default is
   * the protection rather than a preference.
   *
   * <p>One card is one clock. Two are two, and the drift between them is reported as DUT response
   * rather than as a fault, so the one-card picker is what guarantees the common case is right.
   * The escape exists because some benches have no choice: a capture-only ADC (an E1DA Cosmos has
   * only a USB-C out) or a playback-only USB DAC cannot be half of one card, and before the
   * one-direction streams of 2026-11-11 they could not be chosen at all.</p>
   *
   * <p>Not persisted: it is derived on open from whether the two saved devices are in fact on one
   * card, so a saved configuration cannot contradict its own checkbox.</p>
   */
  separateDevices: false,

  /** Only read while {@link separateDevices}; otherwise both sides use <c>cardKey</c>. */
  inputCardKey: '',
  outputCardKey: '',

  inputKey: '',
  outputKey: '',
  inputChannelOffset: 0,
  outputChannelOffset: 0,
  minStimulusFrequencyHz: 20,
  // SoundCardStreamSettle.DefaultMs. Only ever seen before a card has been saved, since loading
  // overwrites the whole form -- but it has to agree with the server's default or the first save
  // of a fresh card writes a figure the server did not choose.
  streamStartSettleMs: 2000,
})

const outputStep = reactive({ dbFs: -6, reading: 1000, unit: 'Rms' as VoltageEntryUnitName })
const inputStep = reactive({
  /**
   * Where the known voltage comes from. 'External' is ARTA's: a second signal generator.
   * 'Loopback' uses the card's own output through a patch cable, which is only possible once the
   * output has been calibrated.
   *
   * <p>Loopback is the default because it is the one that needs nothing the bench does not already
   * have — the cable is already there from step 1 — and because it does both channels in one
   * press. The external route is the more trustworthy of the two (it does not inherit the output
   * calibration's error) and stays one dropdown away.</p>
   */
  source: 'Loopback' as 'External' | 'Loopback',
  known: 500,
  unit: 'Rms' as VoltageEntryUnitName,
  channel: 'Left' as 'Left' | 'Right',

  // The same level step 1 defaults to. -20 dB was chosen for headroom, but a card whose input is
  // much less sensitive than its output then measures 20 dB down into its own noise; -6 dB is
  // clear of that and still 6 dB off clipping, which the guard checks anyway.
  dbFs: -6,
})

const saved = ref(false)
const controlPanelNote = ref<string | null>(null)

/**
 * Whether each calibration step's instructions are showing.
 *
 * <p>Folded away by default and opened by the <b>?</b> beside the step's heading. The dialog is
 * long enough to scroll, and the two instruction panels are the longest things in it — but they
 * are read once, when the bench is being set up for the first time, and are noise on every visit
 * afterwards. They are the step's own instructions rather than a tooltip's worth of detail, so
 * they stay in the dialog rather than moving to the manual; only their default changes.</p>
 *
 * <p>Deliberately not persisted: the state lives as long as this component, so a step opened
 * during one visit is still open on the next, and a reload starts folded again.</p>
 */
const outputHelpOpen = ref(false)
const inputHelpOpen = ref(false)
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

/** The card a saved device belongs to, found in the listing rather than re-derived here. */
function cardKeyFor(hostApi: string, deviceName: string) {
  const match = [...(inventory.value?.inputs ?? []), ...(inventory.value?.outputs ?? [])].find(
    (o) => o.hostApi === hostApi && o.name === deviceName,
  )
  return match ? keyOf(match.hostApi, match.interfaceName) : ''
}

/**
 * TEMPORARY — shows the full card list this picker offered before 2026-10-02: every host API, one
 * entry per (API, interface), 35 entries on this bench for four pieces of hardware. It exists only
 * so the filtered list can be checked against real cards, and comes out once that is done, along
 * with <c>buildCards</c>' own <c>advanced</c> parameter.
 *
 * <p>Not persisted, and off on every open, because the filtered list is the one meant to be used —
 * except where the saved card is only reachable in the full one (see the open watcher).</p>
 */
const advanced = ref(false)

/**
 * The cards the machine has, each a host API and an interface within it.
 *
 * Picking the card first is what guarantees the input and the output are the same piece of
 * hardware — and therefore one clock. Two cards run on two, and the drift between them is not
 * reported as a fault but as DUT response.
 */
const cards = computed(() =>
  buildCards(inventory.value?.inputs ?? [], inventory.value?.outputs ?? [], advanced.value),
)

/** How many entries Advanced would add, so the checkbox can say what it is for. */
const hiddenCardCount = computed(() =>
  Math.max(
    0,
    buildCards(inventory.value?.inputs ?? [], inventory.value?.outputs ?? [], true).length -
      buildCards(inventory.value?.inputs ?? [], inventory.value?.outputs ?? [], false).length,
  ),
)

/**
 * What to say about a host API that offered nothing — see <c>hostApiNotes</c>, which holds the rule
 * and the reasoning. Rendered under the card dropdown rather than at the top of the dialog: it is
 * about the list beside it, not about the dialog.
 */
const apiNotes = computed(() => hostApiNotes(inventory.value?.hostApis ?? [], cards.value.length))

/**
 * Which card each direction is filtered to. The same key both ways in the ordinary mode, which is
 * what makes that path behave exactly as it did before separate devices existed.
 */
const inputCardKey = computed(() => (form.separateDevices ? form.inputCardKey : form.cardKey))
const outputCardKey = computed(() => (form.separateDevices ? form.outputCardKey : form.cardKey))

/** What the two chosen cards amount to — one clock, two clocks, or half a bench. */
const pairing = computed(() => devicePairing(inputCardKey.value, outputCardKey.value))

/**
 * The cards each dropdown offers in separate mode. A card with no inputs cannot be the input card,
 * and offering it would collapse that side into a blank device list with nothing saying why.
 */
const inputCards = computed(() => cardsOffering(cards.value, inventory.value?.inputs ?? []))
const outputCards = computed(() => cardsOffering(cards.value, inventory.value?.outputs ?? []))

/** The card the Control panel button belongs to: the input's, as its own comment explains. */
const selectedCard = computed(() => cards.value.find((c) => c.key === inputCardKey.value) ?? null)

/** Only the chosen card's devices, so a side cannot name hardware its own card does not carry. */
const cardInputs = computed(() =>
  (inventory.value?.inputs ?? []).filter((o) => keyOf(o.hostApi, o.interfaceName) === inputCardKey.value),
)
const cardOutputs = computed(() =>
  (inventory.value?.outputs ?? []).filter((o) => keyOf(o.hostApi, o.interfaceName) === outputCardKey.value),
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
  () => [inputCardKey.value, outputCardKey.value].join('|'),
  () => {
    if (!cardInputs.value.some((o) => keyOf(o.hostApi, o.name) === form.inputKey))
      form.inputKey = cardInputs.value.length > 0 ? keyOf(cardInputs.value[0]!.hostApi, cardInputs.value[0]!.name) : ''

    if (!cardOutputs.value.some((o) => keyOf(o.hostApi, o.name) === form.outputKey))
      form.outputKey = cardOutputs.value.length > 0 ? keyOf(cardOutputs.value[0]!.hostApi, cardOutputs.value[0]!.name) : ''
  },
)

/**
 * Carries the selection across the checkbox, in both directions, so toggling it never empties a
 * dialog that was correctly filled in.
 *
 * <p>On: both sides start from the single card, so the first thing the user sees is the bench they
 * already had, with two dropdowns instead of one. Off: the input's card becomes the single card —
 * the input is the measuring side, so it is the one worth keeping — and the output follows it
 * through the watcher above.</p>
 *
 * <p>A <c>@change</c> handler rather than a watcher on the flag, and that is load-bearing: the
 * open path below derives this flag from the saved devices and sets the two card keys itself, so
 * a watcher — which fires on a programmatic change as readily as a typed one — would overwrite the
 * restored pair with whatever the single card happened to be.</p>
 */
function onSeparateDevicesChanged() {
  if (form.separateDevices) {
    form.inputCardKey = form.cardKey
    form.outputCardKey = form.cardKey
    return
  }

  form.cardKey = form.inputCardKey || form.outputCardKey
}

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

  // Two cards chosen deliberately are already explained by modeNote, in more useful words — it
  // names which measurements survive and what to do instead. This one is for the case the user
  // did *not* ask for: one card whose two endpoints the host API cannot be shown to share, which
  // is every WDM-KS pairing. Both firing at once put two amber paragraphs about clocks on screen
  // saying the same thing, which was reported from a MOTU-in / Realtek-out bench.
  if (pairing.value === 'separate') return null

  return 'These may not be the same interface. Two cards run on two clocks, and the drift between them is reported as DUT response — nothing downstream can tell the difference.'
})

/**
 * DirectSound and MME accept any rate by resampling in software, so their numbers are the mixer's.
 *
 * <p>Core Audio lands here too, and it needs its own sentence rather than the Windows one: there is
 * no second host API on macOS to prefer instead, so "prefer ASIO" names nothing the reader can do.
 * What they <i>can</i> do is set the card's own rate in Audio MIDI Setup to the rate being measured,
 * after which PortAudio's converter is a no-op. Keyed on the kind rather than on the platform,
 * because the trust is a property of the host API and the dialog may be read on a machine that is
 * not the one measuring.</p>
 */
const trustWarning = computed(() => {
  const bad = [selectedInput.value, selectedOutput.value].filter((o) => o?.trust === 'Resamples')
  if (bad.length === 0) return null
  const coreAudio = bad.some((o) => o?.hostApiKind === 'CoreAudio')
  return coreAudio
    ? 'Core Audio resamples rather than retuning the card: it will accept any rate, but what the converter ran at is the rate the device is set to. Set the card to the rate you are measuring in Audio MIDI Setup, or treat the numbers as indicative.'
    : 'This host API resamples in software: it will accept any rate, but what the converter ran at is the Windows mixer’s, not what is asked for. Prefer ASIO where the card has a driver.'
})

// Either side is enough, which mirrors SoundCardSettings.IsUsable: one direction opens a stream,
// and a capture-only ADC or a playback-only DAC is the whole reason that is true.
const canSave = computed(
  () => (!!selectedInput.value || !!selectedOutput.value) && rates.value.length > 0 && !busy.value,
)

/**
 * Whether what this dialog shows is what the connected card is actually running.
 *
 * <p>It is not decoration, and it is the fault reported on 2026-11-11: the calibration steps below
 * drive the <i>connected device</i> through the session, while every control above them is a form
 * that <c>applyDevice</c> writes separately — so the two can disagree, and when they do nothing
 * says so. A pairing that accepts no common sample rate (a G6 on WASAPI against a MOTU on ASIO,
 * which is the natural selection and the one that cannot work) leaves <c>canSave</c> false, so
 * <c>applyDevice</c> returns without a word and <i>none</i> of the fields is applied — including
 * Output channels. Generate sine then played the tone on the previously connected card at offset
 * 0, i.e. channels 1/2, while the dialog read 3/4 and the cable was on 3/4: a loopback that
 * captured nothing but hum, with the one control that explains it showing the right answer.</p>
 *
 * <p>The comparison is <c>applyDevice</c>'s own, so the two cannot disagree about what "applied"
 * means; it is briefly false during the 250 ms debounce, which is correct — a tone started then
 * would play on the configuration being replaced.</p>
 */
const settingsInEffect = computed(() => {
  const next = toConfig()
  if (!next || !canSave.value) return false
  return JSON.stringify(next) === JSON.stringify(config.value)
})

/**
 * What to say about the stored full scales, beside the dropdowns that change them.
 *
 * <p>Two things, and the drift one is the reason this exists: a full scale is the figure that
 * turns samples into volts, measured on one device and one channel pair, so changing either leaves
 * every reading afterwards wrong by whatever the two differ by — and nothing downstream can tell a
 * calibrated card from a mis-calibrated one, which is what makes it worth saying out loud. The
 * rule, including why provenance that was never recorded is <i>not</i> drift, is in
 * <c>calibrationState</c>.</p>
 */
const calibrationNotices = computed<string[]>(() => {
  const state = calibrationState(config.value)
  const notices: string[] = []

  // Drift first: it is about a figure that exists and is wrong, which is worse than one that is
  // honestly absent, and it is the one the user has just caused.
  for (const drift of state.drifted) {
    notices.push(
      `The ${drift.direction} full scale was measured on ${drift.measuredOn}, and the ` +
        `${drift.direction} is now ${drift.nowOn}. The stored figure describes different ` +
        `hardware, so levels on that side are wrong by whatever the two differ by — run the ` +
        `${drift.direction} step again.`,
    )
  }

  if (state.uncalibrated.length === 2) {
    notices.push(
      'This card has not been calibrated: readings are in dBFS rather than volts, and the app ' +
        'does not know what voltage it is playing. Both steps are below.',
    )
  } else if (state.uncalibrated.includes('output')) {
    notices.push(
      'The output has not been calibrated, so the app does not know what voltage it is playing.',
    )
  } else if (state.uncalibrated.includes('input')) {
    notices.push(
      'The input has not been calibrated, so readings are in dBFS rather than volts.',
    )
  }

  return notices
})

/**
 * What the chosen pair costs, said once and beside the dropdowns that caused it.
 *
 * <p>Advisory, never a refusal — the precedent is <c>SoundCardEnumerator.Pairing</c>, which warns
 * about two clocks rather than blocking, because the alternative is making a bench that has no
 * other option unusable. The three cases each say the one thing a reader can act on.</p>
 */
const modeNote = computed(() => {
  switch (pairing.value) {
    case 'separate':
      return 'Two cards run on two clocks. Tone measurements — THD, IMD, stepped sweeps — are unaffected, but the chirp’s phase, group delay and impulse response are not: the clock ratio reads as a frequency-dependent delay. Use the right input as a reference, or the stepped sine, where phase matters.'
    case 'inputOnly':
      return 'Capture only: there is no generator, so this bench measures whatever arrives and cannot drive a sweep. Choose an output as well to run one.'
    case 'outputOnly':
      return 'Playback only: this bench generates but measures nothing. Choose an input as well to take a reading.'
    default:
      return null
  }
})

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
      // A card saved under a host API the default list hides still resolves on the server — the
      // config stores names, not indices — but it would be missing from its own dropdown, which
      // reads as the card having vanished. Show the full list for that case instead.
      advanced.value = savedCardNeedsAdvanced(
        inventory.value?.inputs ?? [],
        inventory.value?.outputs ?? [],
        config.value.inputHostApi,
        config.value.inputDeviceName,
      )
      const savedInputCard = cardKeyFor(config.value.inputHostApi, config.value.inputDeviceName)
      const savedOutputCard = cardKeyFor(config.value.outputHostApi, config.value.outputDeviceName)

      // Derived, never stored, so a saved configuration cannot contradict its own checkbox. Two
      // different cards is separate mode; so is a bench with only one direction saved, which is
      // exactly how a capture-only ADC or a playback-only DAC comes back.
      form.separateDevices = savedInputCard !== savedOutputCard
      form.cardKey = savedInputCard || savedOutputCard
      form.inputCardKey = savedInputCard
      form.outputCardKey = savedOutputCard
      form.inputKey = keyOf(config.value.inputHostApi, config.value.inputDeviceName)
      form.outputKey = keyOf(config.value.outputHostApi, config.value.outputDeviceName)
      form.inputChannelOffset = config.value.inputChannelOffset
      form.outputChannelOffset = config.value.outputChannelOffset
      form.minStimulusFrequencyHz = config.value.minStimulusFrequencyHz
      form.streamStartSettleMs = config.value.streamStartSettleMs
    }
    await reprobe()
  },
  { immediate: true },
)

// Toggling the list must not leave the selection pointing outside it: both device lists filter on
// the card key, so a card that is no longer offered survives as a blank Input and a blank Output.
watch(
  () => advanced.value,
  () => {
    const reselect = (key: string) =>
      reselectCardKey(key, inventory.value?.inputs ?? [], inventory.value?.outputs ?? [], advanced.value)

    // All three, not just the one in use: toggling Advanced twice would otherwise leave whichever
    // pair was idle pointing outside its own dropdown, and the checkbox beside them makes that
    // reachable without reopening the dialog.
    form.cardKey = reselect(form.cardKey)
    form.inputCardKey = reselect(form.inputCardKey)
    form.outputCardKey = reselect(form.outputCardKey)
  },
)

// A channel pair belongs to the device it was chosen on. Carried onto one with fewer channels it
// asks for channels that do not exist, and because the pair dropdowns are rendered only where there
// is more than one pair to pick, a two-channel card then shows an error about channels with no
// channel control on screen to answer it -- see clampChannelOffset. Clamped on the device rather
// than on the card, since a card's devices need not all have the same width.
watch(
  () => [form.inputKey, form.outputKey].join('|'),
  () => {
    form.inputChannelOffset = clampChannelOffset(
      form.inputChannelOffset, selectedInput.value?.maxInputChannels ?? 0)
    form.outputChannelOffset = clampChannelOffset(
      form.outputChannelOffset, selectedOutput.value?.maxOutputChannels ?? 0)
  },
  { flush: 'sync' },
)

// Re-probed on every change of either side or either offset, because the answer depends on all four
// and a rate list left over from the previous card would be a lie.
watch(
  () => [form.inputKey, form.outputKey, form.inputChannelOffset, form.outputChannelOffset].join('|'),
  () => void reprobe(),
)

/**
 * The panel belongs to the input device, which under ASIO is the card itself — there is one
 * device and it carries both directions, which is the only case this button appears in.
 */
async function openControlPanel() {
  const input = splitKey(form.inputKey)
  if (!input.name) return
  controlPanelNote.value = null

  // Reported back by the server rather than guessed at here: only it can time the call, and the
  // timing is the only evidence there is that no window appeared.
  const result = await showControlPanel(input.hostApi, input.name)
  if (result?.nothingAppeared) {
    controlPanelNote.value =
      "The driver accepted it but showed nothing, so this card keeps its settings in its own application — look for the manufacturer's control software."
  }
}

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
    streamStartSettleMs: form.streamStartSettleMs,
  }
}

/**
 * Applies the device selection as soon as it changes, rather than behind a button.
 *
 * <p>There is nothing to confirm here: every one of these controls is a statement about which
 * hardware is in front of the user, and a selection not yet pressed into effect was simply a
 * dialog disagreeing with the app about which card was selected. The write reopens the device, so
 * it is debounced — changing card and then channel pair in quick succession is two statements
 * about one intention, not two reopenings.</p>
 */
let applyHandle: ReturnType<typeof setTimeout> | null = null

function applyDeviceSoon() {
  if (!props.open) return
  if (applyHandle) clearTimeout(applyHandle)
  applyHandle = setTimeout(() => void applyDevice(), 250)
}

async function applyDevice() {
  applyHandle = null
  const next = toConfig()
  if (!next || !canSave.value) return

  // Nothing to do when it has not moved. Reopening the device is cheap but not free, and the
  // watcher below fires for form changes that resolve to the same config.
  //
  // The *whole* config, not just the device fields: compared on those alone, a change to one of
  // the numeric settings below them was silently never saved, because the device had not moved.
  // That is what removing the old "Use this card" button exposed -- the button used to save
  // everything, so nothing else had to.
  if (JSON.stringify(next) === JSON.stringify(config.value)) return

  saved.value = await save(next)
}

watch(
  () =>
    [
      form.cardKey,
      form.inputKey,
      form.outputKey,
      form.inputChannelOffset,
      form.outputChannelOffset,
      form.minStimulusFrequencyHz,
      form.streamStartSettleMs,
    ].join('|'),
  () => applyDeviceSoon(),
)

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
  () =>
    !busy.value &&
    settingsInEffect.value &&
    (inputStep.source === 'External' || outputCalibrated.value),
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

/**
 * The same figure in rms, which is what a voltmeter across that jack would read.
 *
 * <p>Every full scale this dialog stores is <b>peak</b> volts (SoundCardCalibration), and the
 * readouts said only "mV" — so a card whose input full scale is 9232 mV peak was read as 9.2 V rms
 * when it is 6.5 (Joost, 2026-10-14, after measuring 1194 mV rms at −6 dBFS and finding the badges
 * a factor of √2 away from what he expected). The entry field has an rms/peak dropdown precisely
 * because the distinction matters here; the answers were the one place it was left unsaid. Both
 * units are shown rather than one being picked, because the peak figure is the one the app
 * computes with and the rms figure is the one the bench can check.</p>
 */
function mvRms(value: number | null | undefined) {
  return value == null ? '—' : (value / Math.SQRT2).toFixed(1)
}
</script>

<template>
  <!-- Laid out as Joost's "Sound card dialog" mockup of 2026-10-12: the three steps as panels on
       the page's own ground, each with the tinted round glyph its header carries everywhere else,
       and the actions on a strip of their own at the foot. -->
  <Modal :open="open" title="Sound card" size="lg" icon="soundcard" tone="blue" ground flush @close="close">
    <div class="flex flex-col gap-3.5 p-4 text-xs">
      <p
        v-if="inventory && !inventory.available"
        class="rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
      >
        The audio library would not start: {{ inventory.problem }}
      </p>

      <!-- 1. Which card ------------------------------------------------------------------ -->
      <section class="sc-section">
        <div class="sc-head">
          <PanelIcon icon="soundcard" tone="blue" />
          <h3 class="m-0 flex-1 text-sm font-semibold text-neutral-900 dark:text-neutral-100">Device</h3>
          <span class="flex items-center gap-3">
            <!-- A mode switch for this whole section, so it sits with Advanced rather than among
                 the controls it rearranges. Off by default: one card is one clock, and that is
                 the guarantee the single picker exists to make. -->
            <label
              class="flex items-center gap-1.5 text-[11px] text-neutral-600 dark:text-neutral-300"
              title="Lets the input and the output sit on different cards, or on only one of them — which is what a capture-only ADC or a playback-only DAC needs. Two cards run on two clocks, so the chirp's phase and impulse response stop being trustworthy; tone measurements are unaffected."
            >
              <input
                v-model="form.separateDevices"
                type="checkbox"
                class="h-3.5 w-3.5"
                @change="onSeparateDevicesChanged"
              />
              Separate devices
            </label>

            <!-- TEMPORARY, removed once the filtered list has been checked against real cards. -->
            <label
              class="flex items-center gap-1.5 text-[11px] text-neutral-600 dark:text-neutral-300"
              :title="
                `Shows every host API PortAudio enumerates, one entry per interface within each — ${hiddenCardCount} more than the list above. ` +
                'Normally hidden: DirectSound and MME resample in software, so the rate they report is not the rate the converter ran at, and WDM-KS device names carry no interface name, so an input and an output cannot be shown to be the same card.'
              "
            >
              <input v-model="advanced" type="checkbox" class="h-3.5 w-3.5" />
              Advanced<template v-if="hiddenCardCount > 0"> (+{{ hiddenCardCount }})</template>
            </label>
            <button
              type="button"
              class="inline-flex h-[30px] items-center gap-1.5 rounded-lg border border-neutral-200 px-2.5 text-xs hover:bg-neutral-100 disabled:opacity-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="devicesLoading"
              title="Re-read the device list. A newly plugged ASIO interface needs the app restarted — PortAudio reads its list once at startup."
              @click="loadDevices(true)"
            >
              {{ devicesLoading ? 'Refreshing…' : 'Refresh' }}
            </button>
          </span>
        </div>

        <div class="sc-body">
        <!-- The card first, the channels second. Picking the interface is what guarantees both
             directions are one piece of hardware, and therefore one clock: two cards run on two,
             and the drift between them is reported as DUT response rather than as a fault. -->
        <div class="sc-row">
          <span class="sc-label">{{ form.separateDevices ? 'Input card' : 'Sound card' }}</span>
          <span class="flex items-center gap-2">
            <!-- **v-memo, and it is not an optimisation.** This dialog polls the input levels
                 every 120 ms for the calibration bars, so it re-renders about eight times a second
                 the whole time it is open — and Vue's patchDOMProp rewrites `value` on every
                 <option> *unconditionally* (`|| el.tagName === 'OPTION'`, because an option's value
                 can be shadowed by its text). Writing that to an **open** native dropdown resets
                 the popup, which is the flicker reported while hovering the list, 2026-11-11.
                 Measured on this bench: 100 OPTION.value and 25 SELECT.value writes in 3 seconds,
                 with zero option nodes added or removed — so it was never a list being rebuilt.
                 Memoised on its own option list and its own selection, the subtree is not patched
                 between card changes at all.

                 Pre-existing rather than new: the single dropdown measured identically before the
                 separate-devices pair existed. The other selects in this dialog share the
                 mechanism and are left alone for now — their lists are short and static, where
                 these three are the long ones a user hovers through.

                 One select per mode rather than one with a ternary list, so each gets a v-model
                 and a memo key that names exactly what it draws. -->
            <select
              v-if="!form.separateDevices"
              v-model="form.cardKey"
              v-memo="[cards, form.cardKey]"
              class="tb-input min-w-0 flex-1"
            >
              <option value="">—</option>
              <option v-for="c in cards" :key="c.key" :value="c.key">
                {{ c.interfaceName }} — {{ c.hostApi }}{{ c.duplex ? ' (full duplex)' : '' }}
              </option>
            </select>

            <select
              v-else
              v-model="form.inputCardKey"
              v-memo="[inputCards, form.inputCardKey]"
              class="tb-input min-w-0 flex-1"
            >
              <option value="">—</option>
              <option v-for="c in inputCards" :key="c.key" :value="c.key">
                {{ c.interfaceName }} — {{ c.hostApi }}{{ c.duplex ? ' (full duplex)' : '' }}
              </option>
            </select>

            <!-- Only ASIO has one. The other host APIs have nothing equivalent: Windows' own
                 sound panel configures the mixer, which exclusive mode bypasses, so offering it
                 would point at settings that do not apply to anything measured here. -->
            <button
              v-if="selectedCard?.hasControlPanel"
              type="button"
              :disabled="controlPanelOpen || busy"
              :title="
                controlPanelOpen
                  ? 'The panel is open — close it to carry on here.'
                  : 'Opens the driver settings: buffer size, clock source and often the sample rate, none of which this app can set.'
              "
              class="inline-flex h-[34px] shrink-0 items-center gap-1.5 rounded-lg border border-neutral-200 px-3 text-[13px] hover:bg-neutral-100 disabled:opacity-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
              @click="openControlPanel"
            >
              {{ controlPanelOpen ? 'Panel open…' : 'Control panel' }}
            </button>
          </span>
        </div>

        <!-- Only in separate mode: in the ordinary one the output's card is the input's, and a
             second dropdown that can only ever agree with the first is a control with no state. -->
        <label v-if="form.separateDevices" class="sc-row">
          <span class="sc-label">Output card</span>
          <!-- Memoised for the reason the input's is — see the comment there. -->
          <select
            v-model="form.outputCardKey"
            v-memo="[outputCards, form.outputCardKey]"
            class="tb-input"
          >
            <option value="">—</option>
            <option v-for="c in outputCards" :key="c.key" :value="c.key">
              {{ c.interfaceName }} — {{ c.hostApi }}{{ c.duplex ? ' (full duplex)' : '' }}
            </option>
          </select>
        </label>

        <!-- What the chosen pair costs: two clocks, or half a bench. Advisory, never a refusal —
             a bench with no other option must stay usable. See modeNote for the wording and why. -->
        <p
          v-if="modeNote"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          {{ modeNote }}
        </p>

        <!-- The calibration's own state, here rather than beside the two steps: changing a
             dropdown above is what makes a stored full scale stale, so the complaint belongs where
             the cause is. See calibrationNotices. -->
        <p
          v-for="notice in calibrationNotices"
          :key="notice"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          {{ notice }}
        </p>

        <!-- What a host API that offered nothing reported. Under the dropdown because it is about
             that list: an empty one otherwise says only "no cards", which is the same picture
             whether the machine has no interface, the driver declined to start, or the driver
             started and reported no channels. See hostApiNotes for the rule and the reasoning. -->
        <p
          v-for="note in apiNotes"
          :key="note.hostApi"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          {{ note.text }}
          <!-- Its own span, dimmed: the first sentence is what the host API reported and this is
               what to do about it. Not a trailing mustache inside a <template>, which is how this
               was first written — Vue condenses away the leading space of an element's children,
               so the two sentences ran together with no gap. -->
          <span v-if="note.hint" class="opacity-80">{{ note.hint }}</span>
        </p>

        <label v-if="cardInputs.length > 1" class="sc-row">
          <span class="sc-label">Input</span>
          <select
            v-model="form.inputKey"
            v-memo="[cardInputs, form.inputKey]"
            class="tb-input"
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

        <label v-if="inputPairs.length > 1" class="sc-row">
          <span class="sc-label">Input channels</span>
          <select
            v-model.number="form.inputChannelOffset"
            v-memo="[inputPairs, form.inputChannelOffset]"
            class="tb-input"
          >
            <option v-for="pair in inputPairs" :key="pair.offset" :value="pair.offset">{{ pair.label }}</option>
          </select>
        </label>

        <label v-if="cardOutputs.length > 1" class="sc-row">
          <span class="sc-label">Output</span>
          <select
            v-model="form.outputKey"
            v-memo="[cardOutputs, form.outputKey]"
            class="tb-input"
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

        <label v-if="outputPairs.length > 1" class="sc-row">
          <span class="sc-label">Output channels</span>
          <select
            v-model.number="form.outputChannelOffset"
            v-memo="[outputPairs, form.outputChannelOffset]"
            class="tb-input"
          >
            <option v-for="pair in outputPairs" :key="pair.offset" :value="pair.offset">{{ pair.label }}</option>
          </select>
        </label>

<!-- The probed rate list is not shown. The rate a measurement runs at is the measurement's,
             and every screen's rate and span dropdowns are already filtered to it, so a row here
             only told you which spans those dropdowns would be missing. The probe itself still
             runs on every change -- it is what `rates` below, `canSave` and the "accepts none of
             48, 96 or 192 kHz" refusal are made of -- so nothing about the checking changed, only
             its standing readout. -->

        <label
          class="sc-row"
          title="Sound cards are AC-coupled at both ends. Below this a sweep measures the card's own high-pass and reports it as the DUT's."
        >
          <span class="sc-label">Lowest usable frequency</span>
          <span class="flex items-center gap-2">
            <input
              v-model.number="form.minStimulusFrequencyHz"
              type="number"
              min="0"
              max="1000"
              step="1"
              class="tb-input w-28"
            />
            <span class="text-neutral-500">Hz</span>
          </span>
        </label>

        <label
          class="sc-row"
          title="A card re-clocks its hardware when a stream asks for a rate it is not already running at, and it is unsettled for a moment afterwards. This is thrown away before anything is measured. Only paid when a stream is actually opened — changing the sample rate — never on an ordinary RUN."
        >
          <span class="sc-label">Settle after opening</span>
          <span class="flex items-center gap-2">
            <input
              v-model.number="form.streamStartSettleMs"
              type="number"
              min="0"
              max="10000"
              step="100"
              class="tb-input w-28"
            />
            <span class="text-neutral-500">ms</span>
          </span>
        </label>

        <p
          v-if="probe && rates.length === 0 && form.inputKey && form.outputKey"
          class="rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
        >
          {{ probe.problem ?? 'This pairing accepts none of 48, 96 or 192 kHz, which are the rates this analyzer measures at.' }}
        </p>

        <!-- The consequence of whatever is above, stated separately because it is what the user
             actually meets: a setting that reads one thing while the card does another. Its own
             paragraph rather than a sentence appended to the red box, since the box has two
             possible texts and this follows from either. -->
        <p
          v-if="config && !settingsInEffect && !busy"
          class="rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
        >
          These settings are not in effect — none of them has reached a card, so the calibration
          steps below are turned off. They drive whichever card is still connected, which is not
          the one shown here.
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

        <p
          v-if="controlPanelNote"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          {{ controlPanelNote }}
        </p>

        <p v-if="saved" class="text-[11px] text-emerald-700 dark:text-emerald-400">
          Selected — the card is open and ready to calibrate.
        </p>
        </div>
      </section>

      <!-- 2. Output full scale ---------------------------------------------------------- -->
      <section class="sc-section">
        <div class="sc-head">
          <PanelIcon icon="signalOut" tone="orange" />
          <h3 class="m-0 flex-1 text-sm font-semibold text-neutral-900 dark:text-neutral-100">
            Full scale output <span class="font-normal text-neutral-500 dark:text-neutral-400">(mV)</span>
          </h3>
          <button
            type="button"
            class="flex h-7 w-7 shrink-0 items-center justify-center rounded-full border border-neutral-200 text-xs font-semibold text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:border-neutral-700 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
            :aria-expanded="outputHelpOpen"
            aria-label="What this step asks you to do"
            title="What this step asks you to do."
            @click="outputHelpOpen = !outputHelpOpen"
          >
            ?
          </button>
        </div>
        <p
          v-if="outputHelpOpen"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          <strong>Put a voltmeter across the output channel.</strong> Play the tone, read the meter,
          and enter what it says. That fixes what the card's digital full scale is worth in volts.
        </p>

        <div class="sc-body">
        <label class="sc-row">
          <span class="sc-label">Output level</span>
          <span class="flex min-w-0 items-center gap-2">
            <select
              v-model.number="outputStep.dbFs"
              v-memo="[outputStep.dbFs, playingDbFs]"
              class="tb-input min-w-0 flex-1"
              :disabled="playingDbFs !== null"
            >
              <option v-for="level in OUTPUT_LEVELS" :key="level" :value="level">{{ level }} dBFS</option>
            </select>
            <button
              type="button"
              class="btn-chip"
              :style="
                playingDbFs !== null
                  ? { backgroundColor: 'var(--kind-red-chip-bg)', color: 'var(--kind-red-chip-fg)' }
                  : { backgroundColor: 'var(--kind-orange-chip-bg)', color: 'var(--kind-orange-chip-fg)' }
              "
              :disabled="busy || (!settingsInEffect && playingDbFs === null)"
              :title="
                !settingsInEffect && playingDbFs === null
                  ? 'The settings above are not in effect, so this would play on whichever card is still connected.'
                  : undefined
              "
              @click="toggleTone"
            >
              <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                <rect v-if="playingDbFs !== null" x="6" y="6" width="12" height="12" rx="2" />
                <path v-else d="M7 4l13 8-13 8z" />
              </svg>
              {{ playingDbFs !== null ? 'Stop tone' : `Generate sine · ${calibrationToneHz} Hz` }}
            </button>
          </span>
        </label>

        <p v-if="playingDbFs !== null" class="sc-row text-emerald-700 dark:text-emerald-400">
          <span></span>
          <span>Playing at {{ playingDbFs }} dBFS</span>
        </p>

        <label class="sc-row">
          <span class="sc-label">Voltmeter reads</span>
          <span class="flex items-center gap-2">
            <input
              v-model.number="outputStep.reading"
              type="number"
              min="0"
              step="1"
              class="tb-input w-28"
            />
            <select
              v-model="outputStep.unit"
              v-memo="[outputStep.unit]"
              class="tb-input"
            >
              <option value="Rms">mV rms</option>
              <option value="Peak">mV peak</option>
            </select>
          </span>
        </label>

        <div class="sc-row">
          <span class="sc-label">Peak output</span>
          <span class="flex flex-wrap items-center gap-3">
          <button
            type="button"
            class="btn-chip"
            :style="{ backgroundColor: 'var(--kind-orange-chip-bg)', color: 'var(--kind-orange-chip-fg)' }"
            :disabled="busy || playingDbFs === null"
            @click="acceptOutput"
          >
            Estimate peak output
          </button>
          <span class="font-mono text-neutral-600 dark:text-neutral-300">
            current {{ mv(config?.outputFullScalePeakMv) }} mV peak
            <span class="text-neutral-500 dark:text-neutral-400">
              · {{ mvRms(config?.outputFullScalePeakMv) }} mV rms
            </span>
          </span>
          </span>
        </div>
        </div>
      </section>

      <!-- 3. Input full scale ----------------------------------------------------------- -->
      <section class="sc-section">
        <div class="sc-head">
          <PanelIcon icon="signalIn" tone="green" />
          <h3 class="m-0 flex-1 text-sm font-semibold text-neutral-900 dark:text-neutral-100">
            Full scale input <span class="font-normal text-neutral-500 dark:text-neutral-400">(mV)</span>
          </h3>
          <button
            type="button"
            class="flex h-7 w-7 shrink-0 items-center justify-center rounded-full border border-neutral-200 text-xs font-semibold text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:border-neutral-700 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
            :aria-expanded="inputHelpOpen"
            aria-label="What this step asks you to do"
            title="What this step asks you to do."
            @click="inputHelpOpen = !inputHelpOpen"
          >
            ?
          </button>
        </div>
        <div class="sc-body">
        <label class="sc-row">
          <span class="sc-label">Known source</span>
          <select
            v-model="inputStep.source"
            v-memo="[inputStep.source]"
            class="tb-input"
          >
            <option value="External">A generator of known voltage</option>
            <option value="Loopback">This card's output, through a patch cable</option>
          </select>
        </label>

        <!-- Both routes' instructions fold away together, behind the one ? on the heading: they
             are two answers to the same question, and which one is showing is the Known source
             dropdown's business. The red panel below is not help and is never folded. -->
        <p
          v-if="inputHelpOpen && inputStep.source === 'External'"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          <strong>Feed the chosen input from a source whose voltage you know</strong>, and enter that
          voltage. Each channel is measured on its own, so run this once per side. The generator is
          silenced while it measures — anything this app played would be measured along with it.
        </p>
        <p
          v-else-if="inputHelpOpen"
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

        <label v-if="inputStep.source === 'External'" class="sc-row">
          <span class="sc-label">Input channel</span>
          <select
            v-model="inputStep.channel"
            v-memo="[inputStep.channel]"
            class="tb-input"
          >
            <option value="Left">Left</option>
            <option value="Right">Right</option>
          </select>
        </label>

        <label v-if="inputStep.source === 'External'" class="sc-row">
          <span class="sc-label">Source voltage</span>
          <span class="flex items-center gap-2">
            <input
              v-model.number="inputStep.known"
              type="number"
              min="0"
              step="1"
              class="tb-input w-28"
            />
            <select
              v-model="inputStep.unit"
              v-memo="[inputStep.unit]"
              class="tb-input"
            >
              <option value="Rms">mV rms</option>
              <option value="Peak">mV peak</option>
            </select>
          </span>
        </label>

        <label v-else class="sc-row">
          <span
            class="text-neutral-600 dark:text-neutral-300"
            title="Quiet enough that the input cannot clip, loud enough to sit well clear of the card's noise floor."
          >
            Play at
          </span>
          <span class="flex min-w-0 items-center gap-2">
            <select v-model.number="inputStep.dbFs" v-memo="[inputStep.dbFs]" class="tb-input min-w-0 flex-1">
              <option v-for="level in OUTPUT_LEVELS" :key="level" :value="level">{{ level }} dBFS</option>
            </select>
            <button
              type="button"
              class="btn-chip"
              :style="{ backgroundColor: 'var(--kind-green-chip-bg)', color: 'var(--kind-green-chip-fg)' }"
              :disabled="!canMeasureInput"
              @click="acceptInput"
            >
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M12 3v18M8 7l4-4 4 4M8 17l4 4 4-4" />
              </svg>
              Estimate peak input
            </button>
          </span>
        </label>

        <p v-if="calibrationResult?.measuredDbFs != null" class="sc-row text-neutral-500">
          <span></span>
          <span>measured {{ calibrationResult.measuredDbFs.toFixed(2) }} dBFS</span>
        </p>

        <!-- What each input is hearing, live. The scale is −90…0 dBFS, which is the range a
             calibration cares about: the clip point at the top, the card's noise floor off the
             bottom. Red past −0.5 dBFS, because a reading taken at the clip point is not the
             source's level and the step refuses it. -->
        <!-- In a well of its own, as the mockup draws it: the bars are a live reading rather than a
             setting, and a tinted box is what tells them apart from the rows above. -->
        <div
          class="mt-2 flex flex-col gap-2 rounded-[10px] border border-neutral-200 p-3 dark:border-neutral-800"
          :style="{ backgroundColor: 'var(--surface-ground)' }"
        >
          <div v-for="side in (['Left', 'Right'] as const)" :key="side" class="flex items-center gap-2.5">
            <span class="inline-flex w-12 shrink-0 items-center gap-1.5 text-[12px] text-neutral-600 dark:text-neutral-300">
              <span
                class="h-0.5 w-2.5 shrink-0 rounded-full"
                :class="side === 'Left' ? 'bg-blue-600 dark:bg-blue-400' : 'bg-red-600 dark:bg-red-400'"
              ></span>
              {{ side }}
            </span>
            <div class="relative h-2 flex-1 overflow-hidden rounded-full bg-neutral-200 dark:bg-neutral-700">
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
          <div class="flex justify-between pl-14 pr-20 font-mono text-[11px] text-neutral-400">
            <span>−90</span><span>−60</span><span>−30</span><span>0 dBFS</span>
          </div>
          <p v-if="levels && !levels.acquiring" class="text-[11px] text-neutral-500 dark:text-neutral-400">
            Idle — the bars move while a tone is playing or a measurement is running.
          </p>
        </div>

        <!-- The two figures this whole step exists to produce, as a card each (the mockup): what
             the step answers deserves to be read at a glance rather than off a two-column table
             whose header said "Full scale" once for both. The caption carries the channel's own
             colour, as every pair of channel readings in the app does. -->
        <div class="mt-3 grid grid-cols-2 gap-2.5">
          <div
            v-for="side in (['Left', 'Right'] as const)"
            :key="side"
            class="flex flex-col gap-1 rounded-[10px] border border-neutral-200 px-3 py-2.5 dark:border-neutral-800"
          >
            <span class="inline-flex items-center gap-1.5 text-[12px] text-neutral-500 dark:text-neutral-400">
              <span
                class="h-0.5 w-2.5 shrink-0 rounded-full"
                :class="side === 'Left' ? 'bg-blue-600 dark:bg-blue-400' : 'bg-red-600 dark:bg-red-400'"
              ></span>
              {{ side }} · full scale
            </span>
            <span class="font-mono text-[17px] font-medium text-neutral-900 dark:text-neutral-100">
              {{ mv(side === 'Left' ? config?.inputFullScalePeakMvLeft : config?.inputFullScalePeakMvRight) }}
              <span class="text-[12px] text-neutral-500 dark:text-neutral-400">mV peak</span>
            </span>
            <!-- What a voltmeter across that jack would read — see mvRms. -->
            <span class="font-mono text-[12px] text-neutral-500 dark:text-neutral-400">
              {{ mvRms(side === 'Left' ? config?.inputFullScalePeakMvLeft : config?.inputFullScalePeakMvRight) }} mV rms
            </span>
          </div>
        </div>

        <!-- Not in the mockup, and kept: it only appears once a calibration has just run, and it is
             the one line that says whether the two inputs are the pair they are supposed to be. -->
        <p
          v-if="calibrationResult?.channelDifferenceDb != null"
          class="mt-2 text-[12px] text-neutral-500 dark:text-neutral-400"
          title="How far apart the two inputs are. A few tenths is two ordinary channels; several dB is a wiring or channel-pair mistake."
        >
          Channel difference
          <span class="font-mono text-neutral-900 dark:text-neutral-100">
            {{ calibrationResult.channelDifferenceDb.toFixed(4) }}
            <span class="text-neutral-500 dark:text-neutral-400">dB</span>
          </span>
        </p>
        </div>
      </section>

      <p
        v-if="!isCalibrated"
        class="text-[11px] text-neutral-500 dark:text-neutral-400"
      >
        <!-- The headline moved up beside the device controls (calibrationNotices), so this is only
             the half it does not say: what is still trustworthy without a calibration. -->
        Every ratio — THD, IMD, crosstalk, the shape of a frequency response — is exact whether or
        not this card is calibrated, because a consistent full-scale constant cancels out of it.
        What a calibration adds is the <strong>absolute</strong> level: volts rather than dBFS.
      </p>

      <p v-if="error" class="text-red-600 dark:text-red-400">{{ error }}</p>

    </div>

    <!-- The manual rather than more prose in here: this dialog is already the longest in the app,
         and the host API table and the two calibration procedures are reference material that is
         read once. Its own <dialog> opens into the top layer, so it lands over this one and leaves
         the settings behind it untouched — which is the point, since most of what the page explains
         is what to pick here. -->
    <template #footer>
      <button
        type="button"
        class="mr-auto inline-flex h-9 items-center gap-1.5 rounded-lg border border-neutral-200 px-3 text-[13px] font-semibold hover:bg-neutral-100 dark:border-neutral-800 dark:hover:bg-neutral-800"
        title="Using a sound card: host APIs, both calibration steps, and what the figures mean."
        @click="openManual('sound-card')"
      >
        <HeaderIcon name="manual" :size="16" />
        Manual
      </button>
      <button
        type="button"
        class="inline-flex h-9 items-center rounded-lg bg-neutral-900 px-5 text-[13px] font-semibold text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
        @click="close"
      >
        Close
      </button>
    </template>
  </Modal>
</template>
