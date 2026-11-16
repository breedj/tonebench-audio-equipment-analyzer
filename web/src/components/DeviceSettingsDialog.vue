<script setup lang="ts">
import { computed, onUnmounted, reactive, ref, watch } from 'vue'
import type {
  DeviceRanges,
  DeviceStatus,
  SoundCardConfig,
  SoundCardOption,
  SplitDeviceConfig,
  SplitHalfKindName,
  VoltageEntryUnitName,
} from '../api/client'
import { api } from '../api/client'
import { useSoundCard } from '../composables/useSoundCard'
import { useManual } from '../manual/useManual'
import { isPeriodicRatePair } from '../measurements/ratePairing'
import {
  buildCards,
  cardsOffering,
  clampChannelOffset,
  devicePairing,
  hostApiNotes,
  keyOf,
  matchHostApis,
  reselectCardKey,
  savedCardNeedsAdvanced,
  splitKey,
  type SoundCard,
} from '../soundcard/cardList'
import { asioSplitProblem, withAsioAlternatives } from '../soundcard/asioSplit'
import {
  benchHalfOptions,
  benchPairingProblem,
  cardConfigFor,
  documentsTouched,
  halfFromValue,
  halfValueOf,
  splitConfigFor,
  type StagedBench,
} from '../soundcard/benchHalves'
import { calibrationState } from '../soundcard/calibrationState'
import {
  openedSides,
  rateProblem,
  rateRows,
  sharedRateNote,
  type ProbedBench,
} from '../soundcard/probeSides'
import BenchClockDriftPanel from './BenchClockDriftPanel.vue'
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
const props = defineProps<{
  open: boolean
  status: DeviceStatus | null
  /** The bench's rate ladders, for the generating half's own rate. Split-only. */
  ranges: DeviceRanges | null
}>()
const emit = defineEmits<{ close: [] }>()

/**
 * What the dialog is called, which is the one thing a reader uses to tell the two benches apart.
 *
 * <p>One component, two titles: on a split it is the pairing and both cards and both calibrations,
 * and on a plain card it is the card it always was. Three dialogs and three gears until the merge,
 * which is what made "which device captures" and "which card that is" two questions in two places.</p>
 */
const dialogTitle = computed(() => (isSplitBench.value ? 'Split devices' : 'Sound card'))

const {
  config,
  inventory,
  probe,
  inputProbe,
  outputProbe,
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
  measureOutputViaAnalyzer,
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
  /**
   * Which device each direction of the bench is — the capturing half and the generating half.
   *
   * <p><b>In the form rather than read off the session</b>, which is the whole of what made the two
   * device dialogs mergeable. Every rule below turns on which halves are cards; read from
   * <c>props.status</c> they described the bench being <em>replaced</em>, so for a quarter second
   * after every change the dialog disagreed with itself about which pickers and which calibration
   * steps belonged on screen — which is why <c>settingsInEffect</c> had to exist to say so.</p>
   *
   * <p>On a plain sound-card bench both are <c>SoundCard</c> and nothing offers to change them: a
   * QA40x there would have to re-point the whole session, which is the status bar's Analyzer
   * dropdown's job.</p>
   */
  inputKind: 'SoundCard' as SplitHalfKindName,
  outputKind: 'SoundCard' as SplitHalfKindName,

  /**
   * The rate the generating half runs at, or 0 for "follow the capture rate" — which is the
   * default and makes a split behave exactly like a single device. Split-only, and read from the
   * split document rather than the card's.
   */
  generatorSampleRateHz: 0,

  /** Which physical card, as "host API\u0000interface". Both device lists are filtered to it. */
  cardKey: '',

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

const outputStep = reactive({
  /**
   * How the card's output voltage is established.
   *
   * <p>'Voltmeter' is ARTA's and the only route a single card has: play a tone, read a meter, type
   * the figure. 'Analyzer' is the mirror of the input step's Loopback — the capturing half reads
   * it, which is possible exactly when that half is not a card, because then its own volts are
   * already known. Asked for 2026-11-15.</p>
   */
  source: 'Voltmeter' as 'Voltmeter' | 'Analyzer',
  dbFs: -6,
  reading: 1000,
  unit: 'Rms' as VoltageEntryUnitName,
})
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

// NOTE: the Clock drift panel stood in this dialog until 2026-11-15, and it now lives in the
// Split dialog alone. Drift is a property of a bench with two clocks, and after the narrowing of
// 2026-11-15 a sound card is ONE physical interface -- so every bench that has a drift figure to
// measure is a split: a card against a QA40x, or a card on each half. The split dialog's own panel
// is gated on `benchDrift?.twoClocks`, which covers both. Keeping a second copy here meant one
// reading could be refreshed while the other went stale, and the panel was already carrying a note
// (see that dialog's) about answering for the form's pairing rather than the session's.

/**
 * Whether the analyzer this dialog is configuring is the composite one.
 *
 * <p>The <em>session's</em> kind rather than anything staged, because it is what decides whether
 * there is a pairing to choose at all: the two halves are offered on a split and fixed to the card
 * on a plain sound-card bench. Moving between the two is the status bar's Analyzer dropdown.</p>
 */
const isSplitBench = computed(() => props.status?.deviceKind === 'Split')

/**
 * The split document as the server holds it, or null on a bench that has none.
 *
 * <p>Kept so the two halves can be told from what is saved — the same job <c>config</c> does for
 * the card document, and what <c>splitInEffect</c> compares against.</p>
 */
const storedSplit = ref<SplitDeviceConfig | null>(null)

/**
 * Which devices the bench the session has open is made of, which is what the form opens on.
 *
 * <p>A split names its two halves; anything else is one device doing both, so both halves are that
 * device — which on a plain sound-card bench is the card, and on a QA40x or the simulator is
 * neither direction of this document, leaving every card control off screen. That last case is
 * also when this dialog is unreachable.</p>
 */
function benchHalfKinds(split: SplitDeviceConfig | null): {
  input: SplitHalfKindName
  output: SplitHalfKindName
} {
  const kind = props.status?.deviceKind
  if (kind === 'Split' && split) return { input: split.inputKind, output: split.outputKind }
  if (kind === 'Hardware' || kind === 'Simulated') return { input: kind, output: kind }
  return { input: 'SoundCard', output: 'SoundCard' }
}

/**
 * Whether each half is a card, which is what decides the rows under its dropdown.
 *
 * <p><b>Positive tests, and that matters.</b> The two groups were gated on
 * <c>cardDirection !== 'output'</c> and <c>!== 'input'</c>, which worked while <c>cardDirection</c>
 * was never null on a bench this dialog could reach. It is null now — no card on either half is a
 * real pairing, a QA40x against the simulator — and <c>null !== 'output'</c> is true, so both
 * groups rendered every card row they had: a read-only Output device row naming a card nothing was
 * playing through, and the two figures that belong to a card document the bench does not open.
 * Seen in the running app.</p>
 */
const inputIsCard = computed(() => form.inputKind === 'SoundCard')
const outputIsCard = computed(() => form.outputKind === 'SoundCard')

/**
 * Which directions of the card document the staged bench opens.
 *
 * <p><b>A split uses one direction and discards the other.</b> The factory builds its sound-card
 * half through <c>SoundCardSettings.ForDirection</c>, which blanks the other side's device name —
 * so on a split that plays through the card, anything chosen as an <em>input</em> is thrown away,
 * and a picker offering it is offering a choice that cannot take effect. Which device captures is
 * the <c>Captures</c> dropdown above it.</p>
 *
 * <p><b>Read from the form and not from <c>props.status</c></b>, which is the change that made the
 * two device dialogs one — see <c>form.inputKind</c>. Null when neither half is a card, so the two
 * card pickers, the probe, both calibration steps and the two figures below them all disappear
 * together.</p>
 */
const cardDirection = computed<'both' | 'input' | 'output' | null>(() => {
  const input = inputIsCard.value
  const output = outputIsCard.value
  if (input && output) return 'both'
  if (input) return 'input'
  if (output) return 'output'
  return null
})

/**
 * Whether this dialog offers a card per direction rather than one for both.
 *
 * <p><b>Derived, never chosen.</b> It was a "Separate devices" checkbox until 2026-11-15, which is
 * the duplicate this work removed: <c>DeviceKind.SoundCard</c> now means one physical interface, so
 * a bench of two cards is a <c>Split</c> with a card on each half and there is nothing for a
 * checkbox here to decide. Two dropdowns therefore appear in exactly two cases, and both are facts
 * about the bench rather than preferences:</p>
 *
 * <ul>
 *   <li><b>The bench is a split</b> — each half names its own device, so each half's card is its
 *   own answer. Two cards is the case this was written for; a card on one half only still picks
 *   per direction, because the other half is not a card at all and the single picker's premise
 *   ("one interface, both directions") is exactly what a split is not.</li>
 *   <li><b>The stored selections already differ</b>, which is how a one-direction bench comes back:
 *   a capture-only ADC saves an input name and no output, so the two keys are not equal and the
 *   single picker cannot express it. It is also what an unmigrated two-card document looks like for
 *   the moment before config migration 006 moves it, and showing what is stored is the honest
 *   answer there.</li>
 * </ul>
 */
/**
 * Whether the bench in use opens a <b>stream per card</b> rather than one across both.
 *
 * <p>Named for the mechanism and not for the pairing, because that is what every rule below turns
 * on: a card+card split gives each card its own stream on its own transfer thread, and two streams
 * have none of the restrictions one has.</p>
 */
const twoCardBench = computed(
  () => isSplitBench.value && form.inputKind === 'SoundCard' && form.outputKind === 'SoundCard',
)

const separateDevices = computed(
  () => isSplitBench.value || form.inputCardKey !== form.outputCardKey,
)

/**
 * The sentence that replaces the half of this dialog a split does not use — said rather than left
 * to be noticed, because the missing picker is the one a reader would go looking for.
 */
// NOTE: `splitNote` stood here until the three device dialogs became one. It said which device the
// half this dialog did *not* configure was, and sent the reader to the split settings for it --
// both of which have stopped being true: each direction's own dropdown names its device, and there
// is no second dialog to be sent to. `benchSummary` is what replaced it, and it says the whole
// bench rather than the half that was missing. `otherHalfName` went with it.

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
      // The default list as the dropdowns offer it, which on a two-card bench already carries each
      // ASIO card's alternative: counting the bare collapse would promise entries Advanced no
      // longer adds.
      offerAsioAlternatives(
        buildCards(inventory.value?.inputs ?? [], inventory.value?.outputs ?? [], false),
      ).length,
  ),
)

/**
 * What to say about a host API that offered nothing — see <c>hostApiNotes</c>, which holds the rule
 * and the reasoning. Rendered under the card dropdown rather than at the top of the dialog: it is
 * about the list beside it, not about the dialog.
 */
const apiNotes = computed(() => hostApiNotes(inventory.value?.hostApis ?? [], cards.value.length))

/**
 * A stored <em>render loopback</em> endpoint that the device list no longer offers.
 *
 * <p>Windows offers one beside every playback device so an app can capture what is being played
 * to it, and PortAudio lists it with input channels and no output channels -- which is what an ADC
 * looks like, so the picker offered a DAC as an input card. It cannot hear anything the analyzer
 * plays, and an acquisition through one does not fail: it delivers no first buffer at all and the
 * pump waits for it, so the app reads Acquiring for ever with nothing arriving. The device is gone
 * from the list and the server refuses to open it, so the only thing left is to say where the
 * empty dropdown came from -- without which the selection simply vanishes.</p>
 */
const storedLoopbackInput = ref<string | null>(null)

/** The suffix PortAudio's WASAPI backend appends -- SoundCardEnumerator.RenderLoopbackSuffix. */
const RENDER_LOOPBACK_SUFFIX = ' [loopback]'


/**
 * Which card each direction is filtered to. The same key both ways in the ordinary mode, which is
 * what makes that path behave exactly as it did before separate devices existed.
 */
const inputCardKey = computed(() => (separateDevices.value ? form.inputCardKey : form.cardKey))
const outputCardKey = computed(() => (separateDevices.value ? form.outputCardKey : form.cardKey))

/** What the two chosen cards amount to — one clock, two clocks, or half a bench. */
const pairing = computed(() => devicePairing(inputCardKey.value, outputCardKey.value))

/**
 * The cards each dropdown offers in separate mode. A card with no inputs cannot be the input card,
 * and offering it would collapse that side into a blank device list with nothing saying why.
 *
 * <p>Each list is collapsed towards the <i>other</i> side's host API, which is what stops the
 * picker offering a pair PortAudio cannot open. A G6 chosen as the input makes the MOTU offer its
 * WASAPI entry rather than its ASIO one; with nothing chosen on the other side both fall back to
 * the ordinary best-API collapse. The reasoning, and why the API is ours to choose rather than the
 * user's in this mode, is in <c>collapseToBestApi</c> and <c>matchHostApis</c>.</p>
 */
function cardsFor(
  devices: readonly SoundCardOption[],
  otherCardKey: string,
): ReturnType<typeof cardsOffering> {
  const prefer = otherCardKey ? splitKey(otherCardKey).hostApi : undefined
  const list = prefer
    ? buildCards(inventory.value?.inputs ?? [], inventory.value?.outputs ?? [], advanced.value, prefer)
    : cards.value
  return cardsOffering(offerAsioAlternatives(list), devices)
}

/**
 * The best non-ASIO entry for every ASIO card, on a two-card bench and nowhere else — the rule and
 * the reasoning are in <c>withAsioAlternatives</c>.
 *
 * <p>Here because a split may have at most one ASIO card, and the ordinary one-entry-per-interface
 * collapse hides the alternative it would have to be moved to. Reported 2026-11-15 on a bench whose
 * two cards both carry ASIO drivers: the pair the picker offered could not be opened, and the only
 * route to the pair that could was the Advanced checkbox and thirty further entries.</p>
 */
function offerAsioAlternatives(list: readonly SoundCard[]): SoundCard[] {
  if (!twoCardBench.value) return [...list]
  return withAsioAlternatives(
    list,
    inventory.value?.inputs ?? [],
    inventory.value?.outputs ?? [],
    advanced.value,
  )
}

/**
 * <b>The other side's host API is only worth matching when one stream has to carry both
 * directions.</b>
 *
 * <p>Collapsing each list towards the other side exists because PortAudio cannot open a single
 * stream across two host APIs — measured 2026-11-11, a MOTU under ASIO against a G6 under WASAPI
 * refused at every rate — so the picker's natural selection was the one combination that could not
 * work. On a <see cref="twoCardBench"/> that premise is gone: each card is opened on a stream of
 * its own, and the pair above was connected on this bench on 2026-11-15 as
 * <i>ASIO MOTU -> WASAPI Topping D50 III</i>.</p>
 *
 * <p>So a two-card bench collapses each side to <i>its own</i> best host API, which is the point
 * of the whole feature: the better converter keeps its better driver. Matching it to the other
 * card would quietly give that up — seen doing exactly that, dragging the MOTU off ASIO onto
 * WASAPI to follow a DAC that has no ASIO entry worth having.</p>
 */
const inputCards = computed(() =>
  cardsFor(
    inventory.value?.inputs ?? [],
    separateDevices.value && !isSplitBench.value ? form.outputCardKey : '',
  ),
)
const outputCards = computed(() =>
  cardsFor(
    inventory.value?.outputs ?? [],
    separateDevices.value && !isSplitBench.value ? form.inputCardKey : '',
  ),
)

/**
 * Brings a mismatched pair onto one host API — the safety net the two lists above cannot be.
 *
 * <p>They stop the dropdowns producing a cross-API pair; they cannot repair one that arrives from
 * somewhere else. Three ways it does: a configuration saved before this rule existed, Advanced
 * being turned off over a deliberate cross-API selection, and Separate devices being ticked over a
 * one-card choice whose API the other side does not have.</p>
 *
 * <p>Writing to what it watches is safe because <c>matchHostApis</c> is idempotent — once the two
 * agree it returns them untouched — so the second pass stops. It cannot fight the user either:
 * with the lists collapsed as they are, a mismatched pair is unreachable from the dropdowns.</p>
 */
watch(
  () => [separateDevices.value, form.inputCardKey, form.outputCardKey, advanced.value].join('|'),
  () => {
    // Same rule as the two lists above, and kept for the same one case they keep it for: a stored
    // document whose two endpoints name one interface under *different* host APIs still opens as a
    // single duplex stream, and that is the pair this repairs. A split opens a stream per half and
    // must be left alone -- matching there is not a safety net, it is the loss of ASIO on whichever
    // card had it, and on a half that is a QA40x it would drag the *stored* card of a direction
    // this bench does not even open.
    if (!separateDevices.value || isSplitBench.value) return
    const matched = matchHostApis(
      form.inputCardKey,
      form.outputCardKey,
      inventory.value?.inputs ?? [],
      inventory.value?.outputs ?? [],
      advanced.value,
    )
    if (matched.inputKey !== form.inputCardKey) form.inputCardKey = matched.inputKey
    if (matched.outputKey !== form.outputCardKey) form.outputCardKey = matched.outputKey
  },
)

/**
 * One dropdown per direction, naming the device: <b>QA40x, then the cards, then Simulator</b>.
 *
 * <p>Offered only on a split, which is the one bench where there is a pairing to choose. On a plain
 * sound card the two halves are the card both ways and the picker below is the card list it always
 * was — a QA40x entry there would have to re-point the whole session, which is the status bar's
 * Analyzer dropdown's job. <c>benchHalves.ts</c> holds the list, the labels and the refusals.</p>
 */
const inputHalfOptions = computed(() =>
  benchHalfOptions(offerAsioAlternatives(cards.value), inventory.value?.inputs ?? [], true),
)
const outputHalfOptions = computed(() =>
  benchHalfOptions(offerAsioAlternatives(cards.value), inventory.value?.outputs ?? [], true),
)

/**
 * The two dropdowns' values, which write a kind <em>and</em> a card from one pick.
 *
 * <p>Writable computeds rather than a field, because the half is already two pieces of state that
 * other things read — <c>form.inputKind</c> drives every narrowing above, and
 * <c>form.inputCardKey</c> is what the device list, the channel pairs and the probe are filtered
 * by. A third field holding the selection would be a third thing to keep in step with them.</p>
 *
 * <p>A card's value <i>is</i> its card key, so picking one writes exactly what the old card
 * dropdown wrote. Picking the QA40x or the simulator writes the kind and <b>leaves the card key
 * alone</b>: that is what keeps the stored card of a direction this bench stops opening — see
 * <c>cardConfigFor</c>, which makes the same promise about the document.</p>
 */
const inputHalfValue = computed({
  get: () => halfValueOf({ kind: form.inputKind, cardKey: inputCardKey.value }),
  set: (value: string) => {
    const half = halfFromValue(value)
    form.inputKind = half.kind
    if (half.kind === 'SoundCard') form.inputCardKey = half.cardKey
  },
})

const outputHalfValue = computed({
  get: () => halfValueOf({ kind: form.outputKind, cardKey: outputCardKey.value }),
  set: (value: string) => {
    const half = halfFromValue(value)
    form.outputKind = half.kind
    if (half.kind === 'SoundCard') form.outputCardKey = half.cardKey
  },
})

/** The staged bench, in the shape the two document builders want. */
function stagedBench(): StagedBench {
  return {
    input: { kind: form.inputKind, cardKey: inputCardKey.value },
    output: { kind: form.outputKind, cardKey: outputCardKey.value },
    inputKey: form.inputKey,
    outputKey: form.outputKey,
    inputChannelOffset: form.inputChannelOffset,
    outputChannelOffset: form.outputChannelOffset,
    minStimulusFrequencyHz: form.minStimulusFrequencyHz,
    streamStartSettleMs: form.streamStartSettleMs,
  }
}

/**
 * Why the staged pairing cannot be a bench, or null — the message, never the guard.
 *
 * <p>The server refuses it again whatever this says. It is here because a refusal arriving at
 * Connect, minutes later, names a bench the user has stopped thinking about.</p>
 */
const pairingProblem = computed(() => {
  if (!isSplitBench.value) return null

  // Two cards, both chosen, is {@link asioRefusal}'s branch -- it has a line of its own above the
  // rate rows, which is where it belongs because those rows read perfectly healthy on a pair that
  // cannot be opened. benchPairingProblem answers it too, by delegating to the same function, so
  // judging it here as well would print one sentence twice.
  if (
    form.inputKind === 'SoundCard'
    && form.outputKind === 'SoundCard'
    && inputCardKey.value
    && outputCardKey.value
  ) {
    return null
  }

  return benchPairingProblem(
    { kind: form.inputKind, cardKey: inputCardKey.value },
    { kind: form.outputKind, cardKey: outputCardKey.value },
  )
})

/**
 * The rates the generating half can be driven at.
 *
 * <p>On a split these are deliberately not the capture ladder — that is what makes a QA403
 * capturing at 384 kHz against a 192 kHz DAC reachable — so they come from the server rather than
 * being assumed here.</p>
 */
const generatorRates = computed(
  () => props.ranges?.outputSampleRatesHz ?? props.ranges?.sampleRatesHz ?? [],
)

/** What each half is called in the sentence below the dropdowns. */
function halfLabel(kind: SplitHalfKindName, cardKey: string): string {
  if (kind === 'Hardware') return 'the QA40x'
  if (kind === 'Simulated') return 'the simulator'
  return cardKey ? splitKey(cardKey).name || 'a sound card' : 'a sound card'
}

/** What the bench is, in a sentence, so the two dropdowns cannot be read the wrong way round. */
const benchSummary = computed(() => {
  const generator = halfLabel(form.outputKind, outputCardKey.value)
  const capture = halfLabel(form.inputKind, inputCardKey.value)
  const rate =
    form.generatorSampleRateHz > 0
      ? ` at ${(form.generatorSampleRateHz / 1000).toFixed(form.generatorSampleRateHz % 1000 === 0 ? 0 : 1)} kHz`
      : ''
  return `${generator} generates${rate}; ${capture} captures.`
})

/**
 * What a generator in the <i>other rate family</i> costs, which is a bigger thing than the dead
 * band below and is said first.
 *
 * <p>A stimulus carrying its own period needs the two rates to be an integer ratio, which holds
 * within a family and never across one — so a 44.1 kHz generator against a 48 kHz family capture
 * cannot run a chirp or periodic noise at <b>any</b> span, and the three screens that only have
 * those stimuli refuse the run. Everything tone-based is unaffected, which is most of what a
 * converter is judged by. See measurements/ratePairing.ts for the arithmetic.</p>
 *
 * <p>Checked against every capture rate this analyzer offers rather than one, because the capture
 * rate is a property of the measurement rather than of the bench: this dialog cannot know which
 * span a screen will be set to, and "none of them" is the honest answer to give here.</p>
 */
const familyNote = computed(() => {
  const generatorRateHz = form.generatorSampleRateHz
  if (!isSplitBench.value || !(generatorRateHz > 0)) return null

  const captureRates = props.ranges?.sampleRatesHz ?? []
  if (captureRates.length === 0) return null
  if (captureRates.some((rate) => isPeriodicRatePair(generatorRateHz, rate))) return null

  return (
    `A generator at ${(generatorRateHz / 1000).toFixed(generatorRateHz % 1000 === 0 ? 0 : 1)} kHz is in a `
    + 'different rate family from every rate this analyzer captures at, so no stimulus with a period '
    + 'of its own can line up with the analysis record — the chirp, periodic noise and so the '
    + 'frequency-response, impulse-response and broadband impedance measurements are refused. Tones, '
    + 'IMD, the multitone comb and every stepped-sine sweep are unaffected, which is most of what a '
    + 'converter is measured by.'
  )
})

/**
 * What a slower generator costs, said before the run rather than after it. A sweep has to stop
 * inside the generator's own Nyquist, so the band above roughly 0.4 of the generator's rate is a
 * dead band however fast the capture is; and a *faster* generator hides nothing — whatever it does
 * above the capture's Nyquist aliases into the measurement.
 */
const rateNote = computed(() => {
  if (!isSplitBench.value || form.generatorSampleRateHz <= 0) return null
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

/**
 * Whether a half of this bench has an input ranger with a step-down delay to set.
 *
 * <p>A QA40x half's own setting, which until the merge was behind a second gear of its own. One row
 * and not two: <c>SplitAnalyzerDevice.AutoRangeStepDownDelayMs</c> forwards the <i>input</i> half's
 * figure and the session reports a single value, so there is one ranger on a split whatever the
 * other half is. Null where no half has one, which is every bench of two cards.</p>
 */
const hasHardwareRanger = computed(() => props.status?.autoRangeStepDownDelayMs != null)

const stepDownDelayMs = ref(1000)

watch(
  () => props.status?.autoRangeStepDownDelayMs,
  (ms) => {
    if (ms != null) stepDownDelayMs.value = ms
  },
  { immediate: true },
)

async function applyStepDownDelay() {
  try {
    await api.setAutoRangeStepDownDelay(stepDownDelayMs.value)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}

/** The card the Control panel button belongs to: the input's, as its own comment explains. */
/**
 * The card the Control panel button belongs to, or null where this half is not a card at all.
 *
 * <p>The <c>inputIsCard</c> half is not belt and braces: <c>inputCardKey</c> is the <em>stored</em>
 * card, which a half that is not a card deliberately leaves alone, so without it the button sat
 * beside an Input card dropdown reading <b>QA40x</b> and would have opened the ASIO panel of a card
 * nothing in this bench opens. Seen in the running app.</p>
 */
const selectedCard = computed(() =>
  inputIsCard.value ? (cards.value.find((c) => c.key === inputCardKey.value) ?? null) : null,
)

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

// NOTE: `collapsedOutputCard` and `onSeparateDevicesChanged` stood here until 2026-11-15. Both
// existed only to carry the selection across the Separate devices checkbox and to say out loud
// which output card unticking it had thrown away (reported 2026-11-11, after an output calibration
// measured the wrong interface in silence). With the checkbox gone -- a sound card is one interface
// now, and two cards are a Split -- nothing in this dialog collapses two cards into one, so there
// is no discarded side to announce. The analyzer picker can still move a bench from a card+card
// split to a plain sound card, and that path keeps both stored names and is refused at connect by
// name, which is a better answer than a silent discard ever was.

/** Channel pairs the chosen device can actually offer, as offsets. */
const inputPairs = computed(() => pairsFor(selectedInput.value?.maxInputChannels ?? 0))
const outputPairs = computed(() => pairsFor(selectedOutput.value?.maxOutputChannels ?? 0))

function pairsFor(channels: number) {
  const pairs: { offset: number; label: string }[] = []
  for (let offset = 0; offset + 2 <= channels; offset += 2)
    pairs.push({ offset, label: `${offset + 1}/${offset + 2}` })
  return pairs
}

/**
 * Which streams this bench opens, which is what decides every rate question below.
 *
 * <p>Read from the bench rather than from what is selected, because a split leaves the half it
 * does not use <b>stored</b>: see `openedSides`, and `probeSides.ts` for what asking the wrong
 * question cost.</p>
 */
const opens = computed(() => openedSides(cardDirection.value, twoCardBench.value))

/**
 * The probe's answers, arranged as the bench opens them: one pairing, or one card at a time.
 *
 * <p>A side is <c>null</c> where this bench does not open it at all — which is not the same thing
 * as a side that was asked and refused, and the difference is what keeps a stale stored input from
 * blocking a bench that only plays.</p>
 */
const probedBench = computed<ProbedBench>(() => {
  if (probe.value) {
    const pairing = { name: '', rates: probe.value.sampleRatesHz, problem: probe.value.problem }
    return { input: pairing, output: null, oneStream: true }
  }

  const side = (
    answer: typeof inputProbe.value,
    device: SoundCardOption | null,
    carried: boolean,
  ) =>
    carried && device
      ? { name: device.name, rates: answer?.sampleRatesHz ?? [], problem: answer?.problem ?? null }
      : null

  return {
    input: side(inputProbe.value, selectedInput.value, opens.value.input),
    output: side(outputProbe.value, selectedOutput.value, opens.value.output),
    oneStream: false,
  }
})

/** The rates each side accepted, for the readout and for {@link canSave}. */
const inputRates = computed(() =>
  probe.value ? probe.value.sampleRatesHz : (probedBench.value.input?.rates ?? []),
)
const outputRates = computed(() =>
  probe.value ? probe.value.sampleRatesHz : (probedBench.value.output?.rates ?? []),
)

/**
 * The rates a pairing is <em>asked</em> about, from the server, which is the app's list and not a
 * card's. Held separately from {@link rates} because the difference is the whole point: a rate
 * missing from the probe was refused by the driver, and one missing from here was never asked for.
 */
const candidateRates = computed(() => inventory.value?.candidateRatesHz ?? [])

/** "48, 96, 192, 384 and 768 kHz" — one spelling, wherever a list of rates is printed. */
function ratesInKhz(values: number[]): string {
  const khz = values.map((r) => r / 1000)
  if (khz.length === 0) return ''
  if (khz.length === 1) return `${khz[0]} kHz`
  return `${khz.slice(0, -1).join(', ')} and ${khz[khz.length - 1]} kHz`
}

/**
 * The probed rates as the Device section prints them — "48, 96 and 192 kHz".
 *
 * This readout was taken out once, on the reasoning that the rate a measurement runs at is the
 * measurement's and every screen's rate dropdown is already filtered to this list, so a row here
 * only said which entries those dropdowns would be missing. That holds for one card. It is much
 * weaker for **two**, which is where the list stops being a property of a card and becomes the
 * intersection of a pairing you are in the middle of choosing: the dialog otherwise answers only
 * pass or fail, and a pair that offers 48 kHz alone is a materially worse bench than one offering
 * all three, discovered later and somewhere else. Asked for from the bench 2026-11-12.
 */
const rateSummary = computed(() =>
  rateRows(probedBench.value).map((row) => ({ label: row.label, text: ratesInKhz(row.rates) })),
)

/**
 * What the Sample rates row says on hover, and the correction to what that row used to imply.
 *
 * <p>It prints the probe's answer, which is the <em>subset of this app's candidates</em> the pairing
 * accepted — not the card's own ceiling. A Topping D50 III goes to 768 kHz and read "48, 96 and
 * 192 kHz" here for as long as those were the only three asked about (reported 2026-11-13). The
 * row cannot say which it is, so the tooltip does.</p>
 */
const rateSummaryHint = computed(() => {
  const asked = candidateRates.value
  if (asked.length === 0) return ''
  const rows = rateRows(probedBench.value)
  const accepted = rows.length > 0 && rows.every((row) => row.rates.length === asked.length)
    ? asked.length
    : -1
  return accepted === asked.length
    ? `Every rate this analyzer asks about — ${ratesInKhz(asked)} — opened on this pairing.`
    : `Of ${ratesInKhz(asked)}, which is what this analyzer asks a card about, these are the ones `
      + 'the driver accepted in exclusive mode. A rate missing here was refused by the driver; a '
      + 'rate outside that list was never asked for.'
})

/**
 * The one warning that matters: two endpoints of different interfaces run on different clocks, and
 * the drift is reported as DUT response rather than as a fault. Advisory because it has to be —
 * WDM-KS device names carry no interface name at all, so a correct pairing there is indistinguishable
 * from a wrong one.
 */
const pairingWarning = computed(() => {
  // `probe` is the pairing's, so this is already silent on a two-card split and on a one-direction
  // bench -- which is right: there the two cards are the bench the user asked for, said so by the
  // split dialog, rather than a pairing they may have made by accident.
  if (!probe.value || !selectedInput.value || !selectedOutput.value) return null
  if (probe.value.pairing !== 'Unknown') return null

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

/**
 * Why the two cards chosen cannot be the two halves of a split — the ASIO rule, said here because
 * this is where the pair is chosen and because nothing else on this dialog can say it.
 *
 * <p>Each card is probed on a stream of its own and <i>both answer</i>, so the rate rows read
 * healthy and nothing else here has any reason to object: without this the only sign that the
 * bench cannot be opened at all arrives later, at Connect. <c>asioSplitProblem</c> holds the rule
 * and <c>SoundCardSettings.SplitCombinationProblem</c> is the refusal it mirrors.</p>
 */
const asioRefusal = computed(() =>
  // Staged, through twoCardBench, rather than read off the session: judged on the bench being
  // replaced it is silent on exactly the change that creates the pair, for as long as it takes the
  // write to land.
  twoCardBench.value ? asioSplitProblem(inputCardKey.value, outputCardKey.value) : null,
)

/**
 * Whether either half of this bench is a card, i.e. whether the card document is in the path.
 *
 * <p>False on a split of a QA40x against the simulator, which is a bench with no card settings, no
 * calibration steps and no rates to probe — and still a bench, with a pairing of its own to write.</p>
 */
const usesAnyCard = computed(() => inputIsCard.value || outputIsCard.value)


/**
 * Either side is enough, which mirrors <c>SoundCardSettings.IsUsable</c>: one direction opens a
 * stream, and a capture-only ADC or a playback-only DAC is the whole reason that is true.
 *
 * <p><b>Per side, and only the sides this bench opens.</b> A single `rates.length > 0` was the
 * other half of the probe fault: it asked for a rate list that a one-direction bench never
 * produced and that a two-card bench produced only by accident, and `applyDevice` returns without
 * a word when this is false — so a perfectly openable DAC could not be saved, and a split whose
 * other half is a stale stored device could be blocked by a device it does not use.</p>
 */
const canSave = computed(() => {
  if (busy.value) return false

  // **Only where this bench has a card at all.** The three tests below are about a card opening,
  // and a split of a QA40x against the simulator has none — so judged unconditionally they were
  // false for ever on that pairing, which meant its own document was never written and the drift
  // button never came alive. Found in the running app: picking QA40x against Simulator changed
  // nothing at all, with no message, because this gates both writes.
  if (usesAnyCard.value) {
    const usesInput = !!probedBench.value.input
    const usesOutput = !!probedBench.value.output
    if (!usesInput && !usesOutput) return false
    if (usesInput && inputRates.value.length === 0) return false
    if (usesOutput && outputRates.value.length === 0) return false
  }

  // A pair of ASIO cards probes perfectly one card at a time and cannot be opened together, so
  // this is the one refusal the rate lists above cannot reach. Without it the dialog saved the
  // pair, said the card was open and ready to calibrate, and left the analyzer unopenable.
  if (asioRefusal.value) return false

  // And the pairing's own refusals, which the rate lists cannot reach either: a QA40x on both
  // halves probes nothing at all, so every test above it is silent.
  if (pairingProblem.value) return false
  return true
})

/** Why nothing here can open, named for the question that was actually asked. */
const rateRefusal = computed(() => rateProblem(probedBench.value, ratesInKhz(candidateRates.value)))

/** The one thing two separately probed cards can still get wrong between them. */
const noSharedRate = computed(() => sharedRateNote(probedBench.value))

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
 * Whether the settings are refused outright, rather than merely not applied yet.
 *
 * <p>This is what the notice is gated on, and the difference from <c>!settingsInEffect</c> is the
 * debounce: for 250 ms after every change of a dropdown the settings really are not in effect, and
 * a red box that appears for a quarter second each time one is touched is alarming about something
 * that is about to be fine. Disabling two buttons for that long costs nothing, so the buttons use
 * the stricter test and the notice uses this one.</p>
 *
 * <p><b>Gated on the refusal itself</b>, not merely on a side being selected. This notice ends
 * "for the reason just above", and the reason above is {@link rateRefusal} — which used to be
 * rendered only when both device keys were set, so a playback-only DAC printed the consequence
 * with nothing above it saying what the cause was. Keyed on the message, the sentence cannot be
 * on screen alone.</p>
 */
const settingsRefused = computed(
  () =>
    !!config.value
    && !busy.value
    && !canSave.value
    && (!!rateRefusal.value || !!asioRefusal.value || !!pairingProblem.value),
)

/**
 * Whether the staged <em>pairing</em> is the one the session has open.
 *
 * <p>The split half of {@link settingsInEffect}, and the same question: the two documents are
 * written on one debounce, so for 250 ms after a pick the bench on screen is not yet the bench in
 * the room. True on any bench that has no pairing, so the whole of it collapses to the card rule
 * there.</p>
 *
 * <p>This is what replaced the old split dialog's <c>pairingPending</c>. The meaning is unchanged —
 * the drift run measures the bench the session has open, so it cannot be let loose on a staged
 * pairing — but it is now true within a quarter second instead of until a Save press.</p>
 */
const splitInEffect = computed(() => {
  if (!isSplitBench.value) return true
  const stored = storedSplit.value
  if (!stored) return false

  const staged = splitConfigFor(stagedBench(), form.generatorSampleRateHz)
  return (
    staged.inputKind === stored.inputKind
    && staged.outputKind === stored.outputKind
    && staged.generatorSampleRateHz === stored.generatorSampleRateHz
  )
})

/** Both documents applied: what anything that drives the live bench has to wait for. */
const benchInEffect = computed(() => settingsInEffect.value && splitInEffect.value)

/** Whether this card carries the direction at all, which is what each calibration step needs. */
const capturesWithCard = computed(() => opens.value.input && !!selectedInput.value)
const playsThroughCard = computed(() => opens.value.output && !!selectedOutput.value)

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
interface CalibrationNotice {
  /** What to do, as its own line at the top. */
  action: string
  /** Why, underneath it. */
  detail: string
}

const calibrationNotices = computed<CalibrationNotice[]>(() => {
  const state = calibrationState(config.value)
  const notices: CalibrationNotice[] = []

  // Only the direction this bench opens. A split that captures with the card has no output
  // calibration in its path and no output step on screen, so a stale or missing one there is
  // an instruction the user cannot carry out -- seen in the running app, where a figure taken
  // through a different DAC months ago read "Run the output step again" beside a dialog with
  // no output step in it. The figure is left alone, because the Sound card analyzer still
  // needs it and will complain about it there.
  //
  // Read through the very computeds that render the two steps, rather than through
  // `cardDirection` again, so the notice and the step it names cannot come apart. They did: on a
  // playback-only DAC `cardDirection` is 'both' -- it answers which half of the *bench* the card
  // serves, and a plain sound card serves both -- so a stale input calibration printed "Run the
  // input step again ... the input is now nothing" over a dialog with no input step and a card
  // that has no input to run it on (2026-11-15).
  const inUse = (direction: 'input' | 'output') =>
    direction === 'input' ? capturesWithCard.value : playsThroughCard.value

  // Drift first: it is about a figure that exists and is wrong, which is worse than one that is
  // honestly absent, and it is the one the user has just caused.
  for (const drift of state.drifted.filter((d) => inUse(d.direction))) {
    notices.push({
      action: `Run the ${drift.direction} step again.`,
      detail:
        `The ${drift.direction} full scale was measured on ${drift.measuredOn}, and the ` +
        `${drift.direction} is now ${drift.nowOn}. The stored figure describes different ` +
        `hardware, so levels on that side are wrong by whatever the two differ by.`,
    })
  }

  // "again" is the drift wording and is deliberately absent here: there is no stored figure to
  // re-take, so a reader told to run it "again" would go looking for one they never made.
  const uncalibrated = state.uncalibrated.filter(inUse)
  if (uncalibrated.length === 2) {
    notices.push({
      action: 'Run both calibration steps.',
      detail:
        'This card has not been calibrated: readings are in dBFS rather than volts, and the app ' +
        'does not know what voltage it is playing.',
    })
  } else if (uncalibrated.includes('output')) {
    notices.push({
      action: 'Run the output step.',
      detail:
        'The output has not been calibrated, so the app does not know what voltage it is playing.',
    })
  } else if (uncalibrated.includes('input')) {
    notices.push({
      action: 'Run the input step.',
      detail: 'The input has not been calibrated, so readings are in dBFS rather than volts.',
    })
  }

  return notices
})

/**
 * What the chosen pair costs, said once and beside the dropdowns that caused it.
 *
 * <p>Advisory, never a refusal — the precedent is <c>SoundCardEnumerator.Pairing</c>, which warns
 * about two clocks rather than blocking, because the alternative is making a bench that has no
 * other option unusable.</p>
 *
 * <p><b>The two-clock sentence left on 2026-11-15</b> along with this dialog's ability to express a
 * two-card bench: that is a split now, and the split dialog says what its own pairing costs. What
 * is left is the half a bench is <i>missing</i>, which a one-interface card can still be.</p>
 *
 * <p>Both remaining cases were unreachable until that day, and not because they were wrong — the
 * only thing rendering this was a paragraph inside the Clock drift panel, whose own
 * <c>v-if</c> was <c>pairing === 'separate'</c>, so the two sentences about a half-finished bench
 * could never be the pairing that showed them. They are rendered beside the dropdowns now, which
 * is where this comment always said they were.</p>
 */
const modeNote = computed(() => {
  switch (pairing.value) {
    case 'inputOnly':
      return 'Capture only: there is no generator, so this bench measures whatever arrives and cannot drive a sweep. Choose an output as well to run one.'
    case 'outputOnly':
      return 'Playback only: this bench generates but measures nothing. Choose an input as well to take a reading.'
    default:
      return null
  }
})

/**
 * Brings the form into line with the device list that is in hand, and re-probes.
 *
 * <p>Shared by opening the dialog and by <b>Refresh</b>, which is the whole point: until
 * 2026-11-12 Refresh replaced the inventory and did nothing else, so the card dropdown picked up a
 * card that had appeared while every <i>derived</i> piece of state stayed as it was — Advanced,
 * the three card keys, the two device keys, and the probe. A device that had gone left
 * <c>form.inputKey</c> pointing at nothing, which renders as a blank Input row; a device that had
 * arrived was not selected; and the sample rates and any refusal still described the previous
 * list. Reopening the dialog ran all of this and so appeared to be the only thing that worked,
 * which is exactly how it was reported.</p>
 *
 * <p>It reads <c>config</c> rather than the form, so it is idempotent: run twice with nothing
 * plugged or unplugged it writes the same values, and Refresh therefore cannot move a selection
 * the user has just made away from what was saved any more than reopening does.</p>
 */
/**
 * The split document, where the bench has one. A failure leaves it null, which reads as "not a
 * split" — the same answer every setting here gives for a document it cannot read, and the one
 * that keeps the card half of this dialog working on a bench whose pairing cannot be fetched.
 */
async function loadSplit() {
  if (!isSplitBench.value) {
    storedSplit.value = null
    return
  }
  try {
    storedSplit.value = await api.getSplitDeviceConfig()
  } catch {
    storedSplit.value = null
  }
}

async function reconcileWithDevices() {
  const halves = benchHalfKinds(storedSplit.value)
  form.inputKind = halves.input
  form.outputKind = halves.output
  form.generatorSampleRateHz = storedSplit.value?.generatorSampleRateHz ?? 0

  if (config.value) {
    // A card saved under a host API the default list hides still resolves on the server — the
    // config stores names, not indices — but it would be missing from its own dropdown, which
    // reads as the card having vanished. Show the full list for that case instead.
    advanced.value = savedCardNeedsAdvanced(
      inventory.value?.inputs ?? [],
      inventory.value?.outputs ?? [],
      config.value.inputHostApi,
      config.value.inputDeviceName,
      config.value.outputHostApi,
      config.value.outputDeviceName,
    )
    const savedInputCard = cardKeyFor(config.value.inputHostApi, config.value.inputDeviceName)
    const savedOutputCard = cardKeyFor(config.value.outputHostApi, config.value.outputDeviceName)

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
}

/** Re-read the device list, then put the form back in step with it. */
async function refreshDevices() {
  await loadDevices(true)
  await reconcileWithDevices()
}

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    saved.value = false
    // The tone state comes from the server, not from what this tab last did: another tab, or this
    // one before a reload, may have left one playing.
    //
    // The split document comes with them, and only on a bench that has one: it is what the two
    // half dropdowns open on, and `splitInEffect` compares against it.
    await Promise.all([
      load(true),
      loadDevices(),
      syncCalibrationState(),
      loadSplit(),
    ])
    stopLevels?.()
    stopLevels = watchLevels()
    // Captured *before* the reconcile, which is the thing that drops it: a stored device that is
    // no longer in the list is cleared and autosaved, so by the time anything renders there is
    // nothing left to read and the selection has simply vanished.
    const storedInput = config.value?.inputDeviceName ?? ''
    storedLoopbackInput.value = storedInput.toLowerCase().endsWith(RENDER_LOOPBACK_SUFFIX)
      ? storedInput
      : null

    await reconcileWithDevices()

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
// The two kinds are in the list because `opens` is derived from them, and `opens` is an argument
// of the probe: which sides are asked about, and whether they are asked as one stream or two. A
// half changed from a card to a QA40x moves neither device key -- that direction's stored card is
// deliberately left alone -- so without them the probe's answer and the bench it describes could
// come apart. They agree today whichever way round, through `probedBench` reading `opens` live, but
// that is a coincidence of two rules rather than a thing either of them says.
watch(
  () =>
    [
      form.inputKey,
      form.outputKey,
      form.inputChannelOffset,
      form.outputChannelOffset,
      form.inputKind,
      form.outputKind,
    ].join('|'),
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

  await probePairing(draft, opens.value)
}

/**
 * The card document the form writes.
 *
 * <p><c>cardConfigFor</c> is the rule, and the half of it worth knowing here is that <b>a direction
 * whose half is not a card is left exactly as stored</b>: choosing the QA40x as the capturing half
 * must not erase the input card underneath it, because the Sound card analyzer opens both
 * directions of this same document and the manual promises that bench survives the visit.</p>
 */
function toConfig(): SoundCardConfig | null {
  if (!config.value) return null
  return cardConfigFor(config.value, stagedBench())
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

/**
 * Applies the staged bench: <b>the card document first, then the split's, and only the one that
 * moved.</b>
 *
 * <p><b>The order is the rule.</b> <c>PUT /config/soundcard</c> reopens the device whenever the
 * bench uses a card, and <c>PUT /config/split-device</c> reopens it on a split — which here it
 * always is. Written the other way round, the split's reopen builds both halves against the *old*
 * card names and the card write then reopens a second time, so the first open claims hardware the
 * user is in the middle of leaving.</p>
 *
 * <p><b>Only what moved</b>, or every nudge of a channel offset pays two writes, two 409 windows
 * and two reopenings of a stream. A pick that changes a half's kind <i>and</i> brings a card in
 * legitimately moves both and pays two reopens; it is a deliberate, rare act, and the alternative
 * — a card write that skips its own reopen — would leave the document applied while the live
 * device still held the old card, which is the one state <c>settingsInEffect</c> exists to catch
 * and would then report as "in effect".</p>
 *
 * <p>Nothing is written at all while the pairing or the pair of cards is refused, so a bench the
 * server would turn down never reaches it.</p>
 */
async function applyDevice() {
  applyHandle = null
  const next = toConfig()
  if (!next || !canSave.value) return

  const staged = { card: next, split: splitConfigFor(stagedBench(), form.generatorSampleRateHz) }
  // Nothing to do when it has not moved. Reopening the device is cheap but not free, and the
  // watcher below fires for form changes that resolve to the same pair of documents.
  //
  // The *whole* card config, not just the device fields: compared on those alone, a change to one
  // of the numeric settings below them was silently never saved, because the device had not moved.
  // That is what removing the old "Use this card" button exposed -- the button used to save
  // everything, so nothing else had to.
  const moved = documentsTouched(staged, { card: config.value, split: storedSplit.value })

  if (moved.card) saved.value = await save(next)
  if (moved.split) saved.value = (await saveSplit(staged.split)) || saved.value
}

/**
 * Writes the split document, and keeps {@link storedSplit} describing the server either way.
 *
 * <p>On a failure it re-reads rather than keeping the attempt, for the reason <c>useSoundCard.save</c>
 * does the same for the card: <c>splitInEffect</c> compares against this, so holding a value the
 * server rejected would report a bench as applied that is not.</p>
 */
async function saveSplit(next: SplitDeviceConfig): Promise<boolean> {
  try {
    storedSplit.value = await api.setSplitDeviceConfig(next)
    return true
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
    await loadSplit()
    return false
  }
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
      // The split document's three fields ride the same debounce, so one pick that moves a half's
      // kind and its card is one write of each rather than two of either.
      form.inputKind,
      form.outputKind,
      form.generatorSampleRateHz,
    ].join('|'),
  () => applyDeviceSoon(),
)

async function toggleTone() {
  if (playingDbFs.value !== null) await stopTone()
  else await startTone(outputStep.dbFs)
}

async function acceptOutput() {
  if (outputRoute.value === 'Analyzer') await measureOutputViaAnalyzer(outputStep.dbFs)
  else await applyOutput(outputStep.reading, outputStep.unit)
}

async function acceptInput() {
  if (inputStep.source === 'Loopback') await measureInputViaLoopback(inputStep.dbFs)
  else await measureInput(inputStep.known, inputStep.unit, inputStep.channel)
}

/** The loopback source needs the output's own calibration to know what it is playing. */
const outputCalibrated = computed(() => config.value?.outputFullScalePeakMv != null)

/**
 * Whether the thing that plays the loopback tone is this card.
 *
 * <p>On a split it may not be, and that is the whole point of the pairing: a QA40x's output level
 * is factory calibrated, so it is a source of known voltage with no calibration step of its own —
 * which is how a capture-only converter gets an input calibration at all.</p>
 */
const generatorIsCard = computed(() => cardDirection.value !== 'input')

/** What plays the loopback tone, for the wording of the step that uses it. */
const generatorName = computed(() => {
  // On two cards it is a card, but it is not *this* one -- the input being calibrated is on the
  // other, which is the whole shape of that bench. Naming it "this card's output" beside an Input
  // card dropdown reads as a loopback within one interface, which is the one thing it is not.
  if (twoCardBench.value) return "the output card's output"
  if (generatorIsCard.value) return "this card's output"
  return form.outputKind === 'Simulated' ? "the simulator's output" : "the QA40x's output"
})

/**
 * The loopback route's one refusal, and it belongs to an uncalibrated <em>card</em> rather than to
 * every bench: keyed on the bench as a whole it refused the one pairing this route is most useful
 * on, telling the user to calibrate an output that has no calibration step.
 */
/**
 * The Loopback option's own label. Capitalised, because an option is read as the head of a list
 * entry where {@link generatorName} is read mid-sentence -- derived from the one name rather than
 * spelled a second time, so the two cannot come to disagree about what is playing.
 */
const loopbackSourceLabel = computed(
  () =>
    `${generatorName.value.charAt(0).toUpperCase()}${generatorName.value.slice(1)}, through a patch cable`,
)

const loopbackNeedsOutputCalibration = computed(() => generatorIsCard.value && !outputCalibrated.value)

/**
 * Whether the capturing half can stand in for the voltmeter.
 *
 * <p>Only where it is <em>not</em> a card: a QA40x's input is factory calibrated, so what it reads
 * is volts, while a card's input full scale is the very thing that is not known yet — measuring one
 * unknown with another. On a plain sound-card bench both halves are that card, so the route is not
 * offered there at all and the step is the voltmeter one it has always been.</p>
 */
const analyzerCanMeasureOutput = computed(() => playsThroughCard.value && !capturesWithCard.value)

/** What is doing the reading, for the wording of the route that uses it. */
const analyzerName = computed(() =>
  form.inputKind === 'Simulated' ? "the simulator's input" : "the QA40x's input",
)

/** The route actually in force: the voltmeter wherever the analyzer one cannot be offered. */
const outputRoute = computed(() =>
  analyzerCanMeasureOutput.value ? outputStep.source : 'Voltmeter',
)

/**
 * Leaving the voltmeter route silences whatever it was playing.
 *
 * <p>The analyzer route plays its own tone inside the one request, and a tone already running
 * makes this runner <c>Running</c> — which is what <c>MeasurementRunGuard</c> answers with a 409,
 * so a tone carried across the switch does not merely sit there, it refuses the button the user
 * has just switched to. Stopped here rather than hidden: the Stop button is rendered until the
 * tone is really off, so this can never be the thing that strands it.</p>
 *
 * <p>It fires on the route <em>in force</em> rather than on the dropdown, so putting a card on the
 * capturing half — which takes the analyzer route away — leaves the tone alone, that fallback
 * being back to the route the tone belongs to.</p>
 */
watch(
  () => outputRoute.value,
  async (route) => {
    if (route === 'Analyzer' && playingDbFs.value !== null) await stopTone()
  },
)

const canMeasureInput = computed(
  () =>
    !busy.value &&
    settingsInEffect.value &&
    (inputStep.source === 'External' || !loopbackNeedsOutputCalibration.value),
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
  <Modal
    :open="open"
    :title="dialogTitle"
    size="lg"
    icon="soundcard"
    :tone="isSplitBench ? 'violet' : 'blue'"
    ground
    flush
    @close="close"
  >
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
          <span v-if="usesAnyCard || isSplitBench" class="flex items-center gap-3">
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
              class="inline-flex h-[30px] items-center gap-1.5 rounded-lg border border-neutral-200 bg-neutral-100 px-2.5 text-xs font-medium hover:bg-neutral-200 disabled:opacity-50 dark:border-neutral-700 dark:bg-neutral-800 dark:hover:bg-neutral-700"
              :disabled="devicesLoading"
              title="Re-read the device list. A newly plugged ASIO interface needs the app restarted — PortAudio reads its list once at startup."
              @click="refreshDevices"
            >
              {{ devicesLoading ? 'Refreshing…' : 'Refresh' }}
            </button>
          </span>
        </div>

        <div class="sc-body">
        <!-- NOTE: `splitNote` stood here until the three device dialogs became one. It was the
             sentence that replaced the half of this dialog a split did not use -- "which device
             captures is in the split settings" -- and there is no such half and no such dialog
             now: each direction's own dropdown names its device, and a half that is not a card
             simply has no channel rows under it. What took its place is the summary at the foot of
             this group, which says the bench in one sentence so the two dropdowns cannot be read
             the wrong way round. -->

        <p
          v-if="modeNote"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          {{ modeNote }}
        </p>

        <!-- Grouped by direction. The card, the device on it and the channel pair within
             that device are three answers about one side of the bench, and on a two-card bench
             they are checked one side at a time -- which the old order made impossible, since the
             two card dropdowns sat together at the top and the two device rows four rows below
             them. Asked for 2026-11-12. The rule-and-caption is the app's own group idiom
             (.tb-rule / .tb-group), already used by every settings panel.

             In one-card mode the card row keeps its "Sound card" label and stays in the Input
             group: there is no second card row to pair it with, and that card really is the
             input's. -->
        <!-- Hidden where a split captures with something else: the factory masks this direction
             out of the card's settings, so anything chosen here would be thrown away. Its stored
             values are left alone, so the Sound card analyzer's own bench survives the visit. -->
        <div v-if="isSplitBench || inputIsCard" class="sc-group">
          <div class="tb-rule sc-group-head"><span class="tb-group">Input</span></div>

        <!-- The card first, the channels second. Picking the interface is what guarantees both
             directions are one piece of hardware, and therefore one clock: two cards run on two,
             and the drift between them is reported as DUT response rather than as a fault. -->
        <div class="sc-row">
          <span class="sc-label">{{ isSplitBench ? 'Input card' : separateDevices ? 'Input card' : 'Sound card' }}</span>
          <span class="flex items-center gap-2">
            <!-- On a split the dropdown names the *device*: QA40x first, then the cards that carry
                 this direction, then Simulator. One control for what used to be two in two
                 dialogs — a kind here and a card there, with a prose link between them.

                 Its own select rather than a ternary list, like the two below it, so each has a
                 binding and a memo key naming exactly what it draws. Memoised for the reason given
                 below, which applies to this one just as much: the level poll re-renders this
                 dialog about eight times a second. -->
            <select
              v-if="isSplitBench"
              v-model="inputHalfValue"
              v-memo="[inputHalfOptions, inputHalfValue]"
              class="tb-input min-w-0 flex-1"
              title="The device the measurement is taken with. Its input range, its ranging and its drop counters are the bench's."
            >
              <option v-for="o in inputHalfOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
            </select>

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
              v-else-if="!separateDevices"
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

        <!-- One device is not a choice, so it is not a dropdown -- but it is still the answer to
             "which hardware is being driven", and hiding the row entirely left that answer
             nowhere on screen. Asked on 2026-11-11, with a G6 selected whose single WASAPI output
             endpoint made the row vanish while the Input row stayed: the only thing naming the
             output was the calibration-drift warning above it. That matters more than usual here,
             because an output calibration names an interface and a reader has to be able to see
             which one. Shown read-only instead. -->
        <!-- A card with no device in this direction is a statement about the hardware, not an
             empty row: a playback-only DAC has no input and never will, and before this said so
             the Input group simply stopped after the card dropdown. The reader is then left to
             infer from an absence, which is the one thing an absence cannot support. -->
        <p v-if="inputIsCard && cardInputs.length === 0 && inputCardKey" class="sc-note text-[11px] text-neutral-500 dark:text-neutral-400">
          This card has no input — it plays only, so there is no capture and no input calibration.
        </p>

        <div v-if="inputIsCard && cardInputs.length === 1" class="sc-row">
          <span class="sc-label">Input</span>
          <span class="text-neutral-600 dark:text-neutral-300">{{ cardInputs[0]!.name }}</span>
        </div>

        <label v-else-if="inputIsCard && cardInputs.length > 1" class="sc-row">
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

        <label v-if="inputIsCard && inputPairs.length > 1" class="sc-row">
          <span class="sc-label">Input channels</span>
          <select
            v-model.number="form.inputChannelOffset"
            v-memo="[inputPairs, form.inputChannelOffset]"
            class="tb-input"
          >
            <option v-for="pair in inputPairs" :key="pair.offset" :value="pair.offset">{{ pair.label }}</option>
          </select>
        </label>

        </div>

        <!-- Hidden where a split plays through something else, for the reason above. -->
        <div v-if="isSplitBench || outputIsCard" class="sc-group">
          <div class="tb-rule sc-group-head"><span class="tb-group">Output</span></div>

        <!-- Only in separate mode: in the ordinary one the output's card is the input's, and a
             second dropdown that can only ever agree with the first is a control with no state. -->
        <label v-if="isSplitBench || separateDevices" class="sc-row">
          <span class="sc-label">Output card</span>
          <!-- Memoised for the reason the input's is — see the comment there. -->
          <!-- On a split this names the device, as the input's does. -->
          <select
            v-if="isSplitBench"
            v-model="outputHalfValue"
            v-memo="[outputHalfOptions, outputHalfValue]"
            class="tb-input"
            title="The device the stimulus is played from."
          >
            <option v-for="o in outputHalfOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
          </select>

          <select
            v-else
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

        <!-- The generating half's own rate, in the group that chooses that half: it is a property
             of the device that plays, and following the capture rate makes a split behave exactly
             like a single device. -->
        <label v-if="isSplitBench" class="sc-row">
          <span class="sc-label">Generator rate</span>
          <select
            v-model.number="form.generatorSampleRateHz"
            class="tb-input"
            title="The rate the generating half runs at. Following the capture rate makes a split behave exactly like a single device. A different rate has to be an integer ratio of the capture rate, which every rate here is."
          >
            <option :value="0">Follow the capture rate</option>
            <option v-for="rate in generatorRates" :key="rate" :value="rate">
              {{ (rate / 1000).toFixed(rate % 1000 === 0 ? 0 : 1) }} kHz
            </option>
          </select>
        </label>

        <p v-if="outputIsCard && cardOutputs.length === 0 && outputCardKey" class="sc-note text-[11px] text-neutral-500 dark:text-neutral-400">
          This card has no output — it captures only, so it generates nothing and has no output
          calibration.
        </p>

        <div v-if="outputIsCard && cardOutputs.length === 1" class="sc-row">
          <span class="sc-label">Output</span>
          <span class="text-neutral-600 dark:text-neutral-300">{{ cardOutputs[0]!.name }}</span>
        </div>

        <label v-else-if="outputIsCard && cardOutputs.length > 1" class="sc-row">
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

        <label v-if="outputIsCard && outputPairs.length > 1" class="sc-row">
          <span class="sc-label">Output channels</span>
          <select
            v-model.number="form.outputChannelOffset"
            v-memo="[outputPairs, form.outputChannelOffset]"
            class="tb-input"
          >
            <option v-for="pair in outputPairs" :key="pair.offset" :value="pair.offset">{{ pair.label }}</option>
          </select>
        </label>

        </div>

        <!-- The calibration's own state, here rather than beside the two steps: changing a
             dropdown above is what makes a stored full scale stale, so the complaint belongs where
             the cause is. See calibrationNotices. -->
        <p
          v-for="notice in calibrationNotices"
          :key="notice.action + notice.detail"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          <!-- What to do first, on its own line: asked for 2026-11-11, and it is the right way
               round. The reason is two clauses of hardware names, and a reader who has already
               decided to act should not have to read to the end of them to find out what. -->
          <strong class="block">{{ notice.action }}</strong>
          {{ notice.detail }}
        </p>

        <!-- What a host API that offered nothing reported. Under the dropdown because it is about
             that list: an empty one otherwise says only "no cards", which is the same picture
             whether the machine has no interface, the driver declined to start, or the driver
             started and reported no channels. See hostApiNotes for the rule and the reasoning. -->
        <p
          v-if="storedLoopbackInput"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          <strong class="block">Pick the card's own input.</strong>
          &lsquo;{{ storedLoopbackInput }}&rsquo; was chosen, and it is not a recording device:
          Windows offers it beside every playback device so an app can capture what is being played
          to it, after the mixer and before the converter. There is no analog input in it, so it
          cannot hear what the analyzer plays. It is no longer offered in the list above.
        </p>

        <p
          v-for="note in usesAnyCard ? apiNotes : []"
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

<!-- The probed rates -- see rateSummary for why this row came back. It sits here, immediately
             above the refusal that replaces it when a list is empty, so one place always says what
             can be opened.
             <p>One row per stream the bench opens, which on two cards is two: their rates are
             merged and not intersected (SplitAnalyzerDevice.Capabilities says so in its own
             remarks), so a single row would have to invent a shared list the bench does not
             require. A lone row is labelled "Sample rates" however it was probed, because with one
             stream there is nothing for it to be distinguished from.</p> -->
        <div v-for="row in usesAnyCard ? rateSummary : []" :key="row.label" class="sc-row">
          <span class="sc-label">{{ row.label }}</span>
          <span class="text-neutral-600 dark:text-neutral-300" :title="rateSummaryHint">{{ row.text }}</span>
        </div>

        <label
          v-if="usesAnyCard"
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
          v-if="usesAnyCard"
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

        <!-- The bench in one sentence, so the two dropdowns above cannot be read the wrong way
             round. It replaced the note that used to say which half this dialog did not set. -->
        <p v-if="isSplitBench" class="sc-note text-[11px] text-neutral-500 dark:text-neutral-400">
          {{ benchSummary }}
        </p>

        <!-- The pairing's own refusals: a QA40x on both halves, or a half that is a card with no
             card chosen. Above the rate rows for the same reason the ASIO one is — neither probes
             anything, so every rate test below is silent on them. -->
        <p
          v-if="pairingProblem"
          class="rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
        >
          {{ pairingProblem }}
        </p>

        <p
          v-if="familyNote"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          {{ familyNote }}
        </p>

        <p
          v-if="rateNote"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          {{ rateNote }}
        </p>

        <!-- Above the rates on purpose: this is a refusal the rate rows cannot show. Both cards
             are probed on streams of their own and both answer, so a reader with two healthy rate
             lists in front of them has no reason to suspect the pair. -->
        <p
          v-if="asioRefusal"
          class="rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
        >
          {{ asioRefusal }}
        </p>

        <!-- Gated on the message rather than on both device keys being set: the old condition
             could not fire for a one-direction bench, which is exactly the bench that most needed
             an explanation -- a playback-only DAC showed the consequence below with nothing above
             it saying what the cause was. -->
        <p
          v-if="rateRefusal"
          class="rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
        >
          {{ rateRefusal }}
        </p>

        <!-- Not a refusal: two cards each open at their own rate, so neither list rules the other
             out. What it rules out is the split's own default, and that setting is in the other
             dialog. -->
        <p
          v-if="noSharedRate"
          class="rounded border border-amber-300 px-3 py-2 text-[11px] text-amber-900 dark:border-amber-700/60 dark:text-amber-200"
        >
          {{ noSharedRate }}
        </p>

        <!-- The consequence of the refusal above, stated separately because it is what the user
             actually meets: a dialog that reads one thing while the card does another.
             <p>Gated on `settingsRefused` and NOT on `!settingsInEffect`, although the buttons
             below are gated on the latter. The two differ for the 250 ms of the debounce and the
             save that follows it, when the settings are genuinely not yet in effect but are about
             to be -- turning two buttons off for a quarter second is nothing, and flashing a red
             box on every ordinary change of a dropdown is alarming and wrong.</p>
             <p>It states the consequence and suggests no fix, which it used to: "putting both
             cards on a host API the other also offers is usually what fixes it" was a guess
             standing beside the real reason, and on 2026-11-12 it sent a bench round in a circle
             — both cards already <em>were</em> on one host API, ASIO, which is the one host API
             where that is not enough (see SoundCardEnumerator.DuplexCombinationProblem). The
             specific message is directly above; a second, vaguer one underneath can only
             contradict it.</p> -->
        <p
          v-if="settingsRefused"
          class="rounded border border-red-300 px-3 py-2 text-[11px] text-red-800 dark:border-red-700/60 dark:text-red-300"
        >
          Nothing here is in effect. These settings apply themselves as you change them, but only
          once the pairing can be opened — and it cannot, for the reason just above. The analyzer
          is still using the card it had before, so the calibration steps are turned off: they
          would measure <em>that</em> card while this dialog describes a different one.
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
      <!-- Only where this card is what plays. A split that captures with the card has no output
           calibration to take, and the server refuses the step by direction — so offering it would
           be offering a button whose only answer is a refusal.
           <p>And only where the card has an output at all: `cardDirection` answers which half of
           the bench the card serves, which on a plain sound card is always "both" — so a
           capture-only ADC was offered a Generate sine button with nothing to generate from.</p> -->
      <section v-if="playsThroughCard" class="sc-section">
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
          <template v-if="analyzerCanMeasureOutput">
            On this bench there is a second route: {{ analyzerName }} is calibrated, so it can read
            the tone instead — connect the card's output to it and pick it under
            <strong>Measured by</strong>.
          </template>
        </p>

        <div class="sc-body">
        <!-- Offered only where the capturing half is not a card -- see analyzerCanMeasureOutput.
             The mirror of the input step's Known source, and in the same place in its own step. -->
        <label v-if="analyzerCanMeasureOutput" class="sc-row">
          <span class="sc-label">Measured by</span>
          <select
            v-model="outputStep.source"
            v-memo="[outputStep.source, analyzerName]"
            class="tb-input"
          >
            <option value="Voltmeter">A voltmeter on the output</option>
            <option value="Analyzer">{{ analyzerName.charAt(0).toUpperCase() + analyzerName.slice(1) }}, through a cable</option>
          </select>
        </label>

        <p
          v-if="outputRoute === 'Analyzer'"
          class="sc-note text-[11px] text-neutral-500 dark:text-neutral-400"
        >
          Connect the card's output straight to the analyzer's input — both channels — and press
          Measure. The analyzer's own level is a factory figure, so what it reads is volts, and the
          card's full scale follows from the level being played. Nothing in between: an attenuator
          would be measured as if it were the card, unless it is declared as the external input
          gain.
        </p>

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
            <!-- Hidden on the analyzer route, where it is not merely redundant but a trap: that
                 route plays its own tone inside the one request, and a tone started here makes the
                 runner Running, so `MeasurementRunGuard` answers Measure peak output with a 409.
                 A button whose only effect is to break the button beside it (Joost, 2026-11-16).
                 Still rendered while a tone really is playing, whatever the route, or switching the
                 dropdown mid-tone would take away the only thing that can stop it -- the rule the
                 `settingsInEffect` gate below is written to the same way. -->
            <button
              v-if="outputRoute === 'Voltmeter' || playingDbFs !== null"
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

        <label v-if="outputRoute === 'Voltmeter'" class="sc-row">
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
          <!-- The analyzer route plays its own tone and reads it back in one request, so it has
               neither the "start the tone first" precondition nor its explanation. The voltmeter
               route keeps both: there the user is reading a meter while it plays. -->
          <button
            type="button"
            class="btn-chip"
            :style="{ backgroundColor: 'var(--kind-orange-chip-bg)', color: 'var(--kind-orange-chip-fg)' }"
            :disabled="busy || (outputRoute === 'Analyzer' ? !settingsInEffect : playingDbFs === null)"
            :title="
              outputRoute === 'Analyzer'
                ? (settingsInEffect
                    ? 'Plays the tone, reads it back on the analyzer and stores what the card’s full scale is worth in volts.'
                    : 'The settings above are not in effect, so this would measure whichever card is still connected.')
                : (playingDbFs === null
                    ? 'Start the tone first. The full scale is worked out from the level actually being played, not from the one in the dropdown — which is why that dropdown locks while the tone runs.'
                    : undefined)
            "
            @click="acceptOutput"
          >
            {{ outputRoute === 'Analyzer' ? 'Measure peak output' : 'Estimate peak output' }}
          </button>
          <!-- Said out loud rather than left to the greyed button, which is the fault this dialog
               has now had twice: a control that is off for a good reason and does not give it.
               The reason is not that the level is unknown -- the dropdown holds it -- but that
               the server will only attribute a reading to a level it actually played, and the
               dropdown is locked while it is playing. Stop the tone and the two can diverge. -->
          <span
            v-if="outputRoute === 'Voltmeter' && playingDbFs === null && settingsInEffect"
            class="text-neutral-500 dark:text-neutral-400"
          >
            Start the tone to enable this.
          </span>
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
      <!-- Only where this card is what captures, and only where it has an input to capture
           with, for the two reasons above. -->
      <section v-if="capturesWithCard" class="sc-section">
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
            <!-- generatorName is in the memo because the Loopback option's text reads it. A memo
                 lists what its subtree depends on, and this one was written when that text was a
                 literal: left out, the option stayed frozen at "this card's output" on a split
                 the QA40x plays -- measured in the running app, where the note above it had
                 already updated and only the dropdown had not. -->
          <select
            v-model="inputStep.source"
            v-memo="[inputStep.source, loopbackSourceLabel]"
            class="tb-input"
          >
            <option value="External">A generator of known voltage</option>
            <!-- Named rather than fixed as "this card's": on a split it is whatever plays, and a
                 QA40x is the better source of the two — factory calibrated, so nothing of this
                 bench's own has to be measured first. -->
            <option value="Loopback">{{ loopbackSourceLabel }}</option>
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
          is available immediately.
          <template v-if="generatorIsCard">
            Once the output is calibrated the app knows what it is playing in volts, so the cables
            are a source of known voltage and no second generator is needed. Note the figures are
            linked: any error in the output calibration lands directly in these, where a separate
            generator would be independent of it.
          </template>
          <template v-else>
            On this bench the tone comes from {{ generatorName }}, whose level is calibrated at the
            factory — so there is nothing of this card's to measure first, and nothing of its error
            to carry into these figures.
          </template>
        </p>
        <p
          v-if="inputStep.source === 'Loopback' && loopbackNeedsOutputCalibration"
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

      <!-- 4. The pairing's own settings, after the two calibration steps because the drift test
              needs both halves working before it means anything. ------------------------------- -->
      <template v-if="isSplitBench">
        <!-- Said here rather than left to a trace: the chirp's failure mode is a plausible-looking
             delay, which this codebase has already paid for once. -->
        <section class="sc-section">
          <div class="sc-head">
            <PanelIcon icon="soundcard" tone="violet" />
            <h3 class="m-0 flex-1 text-sm font-semibold text-neutral-900 dark:text-neutral-100">
              Two devices are two clocks
            </h3>
          </div>
          <div class="sc-body">
            <p class="sc-note border-t-0 text-[11px] text-neutral-600 dark:text-neutral-400">
              Tone measurements — level, THD, IMD, SNR, the stepped sweeps — are unaffected, because
              every one of them finds its tone by searching for it. A chirp's phase, group delay and
              impulse response are not: the clock ratio reads as a frequency-dependent delay.
              Measure it below and the two chirp screens will offer to correct it, or use
              <em>Right channel is reference</em>, where the ratio of two channels of one capture
              cancels it exactly.
            </p>
          </div>
        </section>

        <BenchClockDriftPanel
          :open="props.open"
          :generates="form.outputKind"
          :captures="form.inputKind"
          :input-host-api="splitKey(inputCardKey).hostApi"
          :input-device="splitKey(form.inputKey).name"
          :output-host-api="splitKey(outputCardKey).hostApi"
          :output-device="splitKey(form.outputKey).name"
          :pairing-note="benchSummary"
          :pending="!benchInEffect"
          pending-reason="This is the bench above, which is still being applied — the test measures the one the app has open."
        />

        <!-- A QA40x half's own setting, which had a gear and a dialog of its own until the merge.
             One row and not two: the session reports a single ranger, the input half's. -->
        <section v-if="hasHardwareRanger" class="sc-section">
          <div class="sc-head">
            <PanelIcon icon="soundcard" tone="green" />
            <h3 class="m-0 flex-1 text-sm font-semibold text-neutral-900 dark:text-neutral-100">QA40x</h3>
          </div>
          <div class="sc-body">
            <label
              class="sc-row border-t-0"
              title="How long a signal has to stay quiet before the input range steps down. Up is immediate — it is the clip backstop. Longer rides out a quiet passage without re-ranging; shorter follows a falling level sooner."
            >
              <span class="sc-label">Auto-range step-down delay</span>
              <span class="flex items-center gap-2">
                <input
                  v-model.number="stepDownDelayMs"
                  type="number"
                  min="0"
                  max="10000"
                  step="100"
                  class="tb-input w-28"
                  @change="applyStepDownDelay"
                />
                <span class="text-neutral-500">ms</span>
              </span>
            </label>
            <!-- Said rather than left out: the opt-in a standalone QA403 has for this rate is not
                 here because a split always has it. The generator is still refused above 192 kHz,
                 which is what protects the output relay, and on a split that ceiling is the
                 generating half's own. -->
            <p class="sc-note text-[11px] text-neutral-500 dark:text-neutral-400">
              A QA403 capturing half runs at 384 kHz on a split without being asked — it is the one
              bench the rate exists for, since the generator is a different device. What plays is
              still capped at its own fastest rate.
            </p>
          </div>
        </section>
      </template>

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
        :title="
          isSplitBench
            ? 'Two devices as one analyzer: which two, what two clocks cost, and running the halves at different rates.'
            : 'Using a sound card: host APIs, both calibration steps, and what the figures mean.'
        "
        @click="openManual(isSplitBench ? 'split-device' : 'sound-card')"
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
