<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import type { DeviceKindName, DeviceRanges, DeviceStatus, ProgrammableLoadStatus, RangingModeName, SimulatedDutModel, SimulatedLoad, SimulatedOutputStageModel, SimulatedProfileRequest } from '../api/client'
import { useProjects } from '../composables/useProjects'
import { DEVICE_DOT_CLASS, devicePill, telemetryRows } from '../device/statusReadout'
import { LOAD_TONE_CLASS, loadReadout } from '../programmableLoad/readout'
import { DISTORTING_DUTS } from '../simulator/hints'
import { allSimulatorKnobsVisible, visibleSimulatorKnobs } from '../simulator/knobs'
import { formatGain, type GainUnit } from '../soundcard/gainUnits'
import ExternalGainPopover from './ExternalGainPopover.vue'

// Real instruments first, the synthetic one last: the two entries above it are things on the
// bench, and the simulator is the odd one out rather than the middle of a scale.
//
// The first entry is spelled QA40x rather than QA403 because it names the *backend*, not a model:
// `Qa40xTransport.Open` tries every product id in `Qa40xModel.All`, so this one option opens a
// QA402 as readily as a QA403 and the user never picks between them. It read "QA403" with a QA402
// connected and the pill beside it reading "QA402 connected" (reported 2026-11-05), and the same
// label is what `devicePill` prints while nothing is attached — so a removed QA402 said "QA403
// removed". The same reasoning renamed `Qa403VendorId` to `Qa40xVendorId`; what keeps the 403 is
// whatever names the one model (`Qa403ProductId`, `DeviceCapabilities.Qa403`), and a connected
// analyzer's own `productName` still reaches the pill, so the model is never guessed at.
const DEVICE_KINDS: { value: DeviceKindName; label: string }[] = [
  { value: 'Hardware', label: 'QA40x' },
  { value: 'SoundCard', label: 'Sound card' },
  { value: 'Simulated', label: 'Simulator' },
]

type DutGroup =
  | 'Linear — response, no distortion'
  | 'Filters — low-pass at 2 kHz, from first order to the four classic 5th-order alignments'
  | 'Distorting — for THD and IMD'
  | 'Impedance fixtures — for the impedance screen'

/**
 * What the simulator can pretend is patched in. A straight wire has no response and no memory,
 * which is enough for a spectrum and shows nothing at all on the measurements whose subject is
 * what a DUT does over time. Four groups, because two dozen flat entries are unreadable: the
 * linear models (a shape and a memory, unity gain), the filters (the same again, all at one corner —
 * first and second order, where a pole-zero fit has an exact answer to be checked against, then the
 * four classic fifth-order alignments to compare against each other), the nonlinear ones (distortion
 * that depends on level or frequency — the THD sweeps' models), and the impedance fixtures
 * (dividers, so never unity, wired bench-style with the load junction on the right input).
 */
const DUT_MODELS: { value: SimulatedDutModel; label: string; title: string; group: DutGroup }[] = [
  { value: 'StraightWire', label: 'Straight wire', title: 'No DUT: the generator reaches the input unaltered.', group: 'Linear — response, no distortion' },
  {
    value: 'TubeAmplifier',
    label: 'Tube amp',
    title:
      'A push-pull valve amplifier: rolled off at both ends, with the output transformer’s resonance just above the audio band. Most of that sits above 24 kHz, so set the span to 96 kHz to see it.',
    group: 'Linear — response, no distortion',
  },
  {
    value: 'Loudspeaker',
    label: 'Loudspeaker',
    title:
      'A two-way loudspeaker in a room: a cabinet panel resonance, a cone breakup peak, and a wall reflection 3 ms behind the direct sound. The model to use when demonstrating the impulse-response screen.',
    group: 'Linear — response, no distortion',
  },
  {
    value: 'BandpassFilter',
    label: 'Bandpass',
    title:
      'A single second-order bandpass at 1 kHz, Q 20 — 50 Hz wide, and nothing else in the path. The one model whose decay you can work out on paper: 27.29/Q = 1.36 dB per period, which is the slope a burst decay must draw. Use it to check the analysis rather than to demonstrate a device.',
    group: 'Linear — response, no distortion',
  },
  {
    value: 'MarginallyStable',
    label: 'Marginally stable',
    title:
      'An amplifier almost out of phase margin: flat, with one Q-500 resonance at 1 kHz that rings for a third of a second. Stable — but on a burst decay it falls only 1.6 dB over thirty periods, so the ridge refuses to die. On a magnitude plot it is just a peak, which is the argument for having decay charts.',
    group: 'Linear — response, no distortion',
  },
  {
    value: 'Oscillating',
    label: 'Oscillating',
    title:
      'An amplifier that has gone the rest of the way: poles outside the unit circle, clipping against its own ±0.5 V rail at about 1010 Hz. The only DUT whose output is not a response to its input — it wrecks coherence, the level probe and the deconvolution, which is exactly what it is for.',
    group: 'Linear — response, no distortion',
  },
  {
    value: 'PhonoRiaa',
    label: 'Phono stage (RIAA)',
    title:
      'A phono preamplifier: the RIAA playback curve, 0 dB at 1 kHz, +19.3 dB at 20 Hz and −19.6 dB at 20 kHz. The one DUT that is not unity across the band, so a loud tone at the bottom of it comes back nearly ten times larger and can meet the input range’s clip. The model for the frequency-response and stepped-sine screens.',
    group: 'Linear — response, no distortion',
  },
  {
    value: 'DigitalProcessor',
    label: 'DSP processor (12 ms)',
    title:
      'A digital processor: a fourth-order low-pass at 20 kHz and 12 ms of latency — 576 samples at 48 kHz. The only model with a real delay, so the frequency response’s “removed N sample delay” readout and the impulse response’s position finally mean something.',
    group: 'Linear — response, no distortion',
  },
  {
    value: 'FirstOrderLowPass',
    label: 'First order LP (2 kHz)',
    title:
      'One real pole at 2 kHz: −3 dB at the corner, 20 dB per decade, phase heading for −90°. The simplest DUT with a response at all, and the one a pole-zero fit has an exact answer for — one pole, no zeros. Point the s-plane views at this when they disagree with a measurement.',
    group: 'Filters — low-pass at 2 kHz, from first order to the four classic 5th-order alignments',
  },
  {
    value: 'SecondOrderLowPass',
    label: 'Second order LP (2 kHz)',
    title:
      'One conjugate pole pair at 2 kHz, Q = 0.707 (a second-order Butterworth): −3 dB at the corner, 40 dB per decade, phase through −90° at the corner to −180°. The Q is a number that was chosen rather than measured, so the Poles and zeros panel and the 45° line on the s-plane map can both be checked against it.',
    group: 'Filters — low-pass at 2 kHz, from first order to the four classic 5th-order alignments',
  },
  {
    value: 'ButterworthLowPass',
    label: 'Butterworth LP (2 kHz)',
    title:
      'A fifth-order Butterworth low-pass at 2 kHz: maximally flat, −3 dB at the corner, then 100 dB per decade. The reference the other three filters are read against — same order, same corner, a different trade each.',
    group: 'Filters — low-pass at 2 kHz, from first order to the four classic 5th-order alignments',
  },
  {
    value: 'ChebyshevLowPass',
    label: 'Chebyshev LP (2 kHz)',
    title:
      'A fifth-order Chebyshev low-pass, 1 dB ripple: the passband ripples between 0 and −1 dB, the corner is the ripple edge at −1 dB, and the transition just above it is twice as steep as the Butterworth’s. Its group delay peaks at the corner — see the delay column on the response screens.',
    group: 'Filters — low-pass at 2 kHz, from first order to the four classic 5th-order alignments',
  },
  {
    value: 'BesselLowPass',
    label: 'Bessel LP (2 kHz)',
    title:
      'A fifth-order Bessel low-pass, −3 dB at 2 kHz: the flattest group delay of the four — 193 µs, constant across the passband — at the price of the gentlest roll-off, 28 dB down where the Butterworth is 48. The model for the group-delay and phase-delay columns.',
    group: 'Filters — low-pass at 2 kHz, from first order to the four classic 5th-order alignments',
  },
  {
    value: 'EllipticLowPass',
    label: 'Elliptic LP (2 kHz)',
    title:
      'A fifth-order elliptic (Cauer) low-pass, 1 dB passband ripple, 60 dB stopband: the only one with zeros — two notches at 3.5 and 5.3 kHz — and the steepest transition anything of this order can have, −60 dB by 4 kHz. Between and above the notches the stopband comes back up to −60 dB and stays there, which is what the zeros cost.',
    group: 'Filters — low-pass at 2 kHz, from first order to the four classic 5th-order alignments',
  },
  {
    value: 'TriodeSingleEnded',
    label: 'SE triode',
    title:
      'A single-ended triode stage: soft, asymmetric, second-harmonic distortion that climbs exactly 1 dB per dB of drive — 0.5 % H2 at 1 V peak. The model for a THD-vs-amplitude sweep, and one whose answer is on paper: H2 = k2·A/2 with k2 = 0.01. Selecting any nonlinear model switches off the simulator’s fixed harmonic stack, so the right channel is a clean generator tap.',
    group: 'Distorting — for THD and IMD',
  },
  {
    value: 'ClassAbCrossover',
    label: 'Class AB (crossover)',
    title:
      'A class-AB output stage biased cold: a 20 mV-wide gain notch at zero, so distortion gets worse as the level falls, peaking at a few percent near −33 dBV before the whole signal fits inside the notch and it falls again. A THD-vs-amplitude sweep from 1 mV to 1 V draws the hump. Odd harmonics only; unity gain at ordinary levels.',
    group: 'Distorting — for THD and IMD',
  },
  {
    value: 'SolidStateClipping',
    label: 'Solid state (clips at −3 dBV)',
    title:
      'A solid-state amplifier: nearly distortionless (about −100 dBc at 1 V) until it meets a ±1 V rail and clips. The classic THD-versus-power knee, placed at the top of the default 1 mV–1 V amplitude sweep.',
    group: 'Distorting — for THD and IMD',
  },
  {
    value: 'TransformerCoreSaturation',
    label: 'Output transformer (saturating)',
    title:
      'An output transformer whose core saturates: flat, but third-harmonic distortion rises 18 dB per octave towards the bottom of the band — about 3 % at 20 Hz for 1 V peak, gone by 200 Hz. The model for a THD-vs-frequency sweep, and what every transformer-coupled amplifier draws in real life.',
    group: 'Distorting — for THD and IMD',
  },
  {
    value: 'ImpedanceResistive',
    label: 'Fixture: 8 Ω',
    title:
      'An impedance fixture rather than a DUT: a 100 Ω reference resistor with a flat 8 Ω load. The one model in this list whose answer is arithmetic — a correct impedance run reads 8.000 Ω at 0.00° at every frequency, through either stimulus. Wired bench-style: generator side on the left input, load junction on the right, so the reference channel is Left.',
    group: 'Impedance fixtures — for the impedance screen',
  },
  {
    value: 'ImpedanceReactive',
    label: 'Fixture: 6 Ω + 1 mH',
    title:
      'The same 100 Ω fixture with a series 6 Ω and 1 mH, which puts the 45° point near 955 Hz. The model to use when checking that phase is measured rather than assumed.',
    group: 'Impedance fixtures — for the impedance screen',
  },
  {
    value: 'ImpedanceCapacitive',
    label: 'Fixture: 47 µF',
    title:
      'The same fixture with a 47 µF capacitor and 0.3 Ω of ESR: |Z| falling 6 dB per octave — 68 Ω at 50 Hz, 3.4 Ω at 1 kHz — at a phase heading for −90°. The mirror of the inductor, and the check that the phase axis has its sign right.',
    group: 'Impedance fixtures — for the impedance screen',
  },
  {
    value: 'ImpedanceLoudspeaker',
    label: 'Fixture: loudspeaker',
    title:
      'The same fixture with a driver on it: 6.4 Ω voice coil and a 45 Hz resonance peaking at 60 Ω, Q 2.5. The shape an impedance sweep is normally run to see.',
    group: 'Impedance fixtures — for the impedance screen',
  },
  {
    value: 'ImpedanceShort',
    label: 'Fixture: short (leads)',
    title:
      'The fixture with its leads shorted: 0.12 Ω and 0.6 µH, the figures a real lead set measured on the bench. Select it for the short calibration step.',
    group: 'Impedance fixtures — for the impedance screen',
  },
  {
    value: 'ImpedanceOpen',
    label: 'Fixture: open',
    title:
      'The fixture with nothing across it but 100 pF of stray capacitance. Select it for the open calibration step — a straight wire works too, but its ratio is exactly one, and inverting exactly one is a division by zero on a noise-free measurement.',
    group: 'Impedance fixtures — for the impedance screen',
  },
]

const DUT_GROUPS: DutGroup[] = [
  'Linear — response, no distortion',
  'Filters — low-pass at 2 kHz, from first order to the four classic 5th-order alignments',
  'Distorting — for THD and IMD',
  'Impedance fixtures — for the impedance screen',
]

const props = defineProps<{
  status: DeviceStatus | null
  error: string | null
  /** True while a connect attempt is in flight — the emit is fire-and-forget, so the button can't tell on its own. */
  connecting: boolean
  /** The device's own range ladders, for the manual dropdowns. Null until the first /device/ranges read returns. */
  ranges: DeviceRanges | null
  /** True while any measurement runner owns the session — see App.vue's runnerActive. */
  runnerActive: boolean
  /**
   * The programmable load, or null before the one startup read has returned. **Nothing about it
   * renders until a load has been chosen in Configuration ▸ Acquisition** — few benches own one, so
   * the default is a bar that looks exactly as it did before this existed.
   */
  load: ProgrammableLoadStatus | null
}>()

/**
 * Everything the load box shows, including its tooltip and its tone. All of it is in
 * `programmableLoad/readout.ts` rather than here, because nothing in a `.vue` is testable and every
 * line of it is a rule the hardware forced — two decimals of amps, "?" rather than "Open" when the
 * relays have not been read back, and a latched fault outranking every other note.
 */
const loadBox = computed(() => loadReadout(props.load))

/** The gate. Few benches own a QA451, so the box exists only once one has been chosen. */
const showLoadBox = computed(() => props.load !== null && props.load.kind !== 'None')

const emit = defineEmits<{
  setSimulatedProfile: [patch: SimulatedProfileRequest]
  setDeviceKind: [kind: DeviceKindName]
  setRangingMode: [mode: RangingModeName]
  setInputRange: [dbv: number]
  openSoundCard: []
  openQa40x: []
  openLoad: []
  setExternalGain: [gain: { leftDb: number; rightDb: number; unit: GainUnit }]
  setOutputRange: [dbv: number]
  connect: []
}>()

function onDeviceKindChange(e: Event) {
  emit('setDeviceKind', (e.target as HTMLSelectElement).value as DeviceKindName)
}

const RANGING_MODES: { value: RangingModeName; label: string }[] = [
  { value: 'Auto', label: 'Auto' },
  { value: 'Manual', label: 'Manual' },
]

const manual = computed(() => props.status?.rangingMode === 'Manual')

/**
 * Both ranges belong to whatever measurement is running — every runner probes, predicts or ratchets
 * its own, and the server refuses these three calls with a 409 while one is. Disabled rather than
 * left live to be refused, the same reasoning as the analyzer switch: a control that answers with an
 * error line is a worse way to state a rule than one that is visibly unavailable and says why.
 */
const rangesLocked = computed(() => props.runnerActive)

const rangingTitle = computed(() => {
  if (props.runnerActive)
    return 'Stop the measurement first — it is choosing the ranges itself while it runs.'
  return manual.value
    ? 'Both ranges are pinned to what you pick here. A measurement run is refused while this is Manual: every sweep needs to choose its own ranges.'
    : 'The input range tracks the signal and the output range follows the generator amplitude. Switch to Manual to pin both — for reproducing another analyzer, or for holding one operating point still.'
})

const attenuationTitle = computed(() =>
  manual.value
    ? 'The input range the ADC is on, pinned. It sets both the clipping point and the input-referred noise floor, and a hotter range distorts more: on this QA403 an 8.6 dBV tone reads H3 at -122 dBc on the 12 dBV range and -127 dBc on the 18 dBV one.'
    : "Automatically kept matched to the input signal — by the device's own auto-ranging where available, otherwise by continuously monitoring the peak level.",
)

const outputRangeTitle = computed(() => {
  if (props.status?.outputRangeClipping)
    return 'The generator is asking for more than this range’s full scale, so what reaches the DUT is clipped. Pick a wider range, or lower the generator level.'
  return manual.value
    ? 'The output attenuator, pinned. It sets the DAC’s noise floor, so wider is quieter at the DUT but noisier in the measurement.'
    : 'Selected automatically from the generator amplitude — the lowest range with enough headroom, for the lowest DAC noise.'
})

const outputRangeClass = computed(() =>
  props.status?.outputRangeClipping
    ? 'text-red-600 dark:text-red-400'
    : 'text-neutral-500 dark:text-neutral-400',
)

/**
 * The two glyphs beside the range readouts, from the mockup: a narrowing stack of rules for the
 * input attenuator, a rising trace into a corner for the output range.
 */
const RANGE_ICON = {
  attenuation: 'M4 6h16M7 12h10M10 18h4',
  output: 'M4 18 10 12l4 4 6-8M14 8h6v6',
}

/**
 * Whether a retry of the failed request means "open the analyzer". The error line is shared by the
 * 2 s poll and every action in App.vue, so a Retry button on it would be wrong for most of what can
 * land there — but a disconnected analyzer is exactly the state the mockup draws it in, and it is
 * also the one state where the message and the Connect button would otherwise both be on the bar
 * saying the same thing. One affordance at a time: Retry while there is something to explain, the
 * plain Connect button while there is not.
 */
const errorRetryable = computed(() => props.status != null && !props.status.connected)

/**
 * Every control on the bar is 28 px tall on a 7 px radius, and every caption is the same muted
 * grey — the mockup's own measurements. One string each rather than seven copies, which is what
 * let the old bar's dropdowns drift apart from its readouts. Selects take no background of their
 * own: `style.css`'s base rule already gives every `select` the control tone.
 */
const BAR_SELECT = 'h-7 rounded-md border border-neutral-300 px-1.5 text-xs disabled:opacity-50 dark:border-neutral-700'
const BAR_READOUT =
  'inline-flex h-7 items-center gap-1.5 rounded-md border border-neutral-200 bg-[var(--surface-control)] px-2 font-mono text-neutral-900 dark:border-neutral-700 dark:text-neutral-100'
const BAR_LABEL = 'text-neutral-500 dark:text-neutral-400'
/** The indigo "auto" chip inside a range readout: the range is the device's answer, not a choice. */
const BAR_AUTO_CHIP =
  'rounded px-1.5 py-px font-sans text-[10px] font-semibold uppercase tracking-wide bg-indigo-100 text-indigo-700 dark:bg-indigo-950 dark:text-indigo-200'
/** A hairline between the segments inside one box — neutral whatever tone the box itself carries. */
const BAR_SEGMENT = 'flex items-center border-l border-neutral-200 px-2.5 dark:border-neutral-700'

/**
 * Falls back to the standard QA403 ladders so the dropdowns are never empty in the window between
 * mount and the first /device/ranges response — the same ladders DeviceCapabilities.Qa403 declares.
 */
const inputRanges = computed(() => props.ranges?.inputRangesDbv ?? [0, 6, 12, 18, 24, 30, 36, 42])
const outputRanges = computed(() => props.ranges?.outputRangesDbv ?? [-12, -2, 8, 18])

function onRangingModeChange(e: Event) {
  emit('setRangingMode', (e.target as HTMLSelectElement).value as RangingModeName)
}

function onInputRangeChange(e: Event) {
  emit('setInputRange', Number((e.target as HTMLSelectElement).value))
}

function onOutputRangeChange(e: Event) {
  emit('setOutputRange', Number((e.target as HTMLSelectElement).value))
}

/**
 * Disabled while measuring rather than left live to be refused. The server enforces the rule either
 * way (409), but a control that answers with an error line is a worse way to state it than one that
 * is visibly unavailable and says why.
 */
const analyzerSwitchTitle = computed(() =>
  props.status?.acquiring
    ? 'Stop the measurement first — the analyzer cannot be switched while one is running.'
    : 'Which analyzer to use. Switching tears the current one down — pump joined and streaming stopped — before standing the other up.',
)

function onSimulatedNoiseChange(e: Event) {
  emit('setSimulatedProfile', { noiseEnabled: (e.target as HTMLInputElement).checked })
}

function onSimulatedHumChange(e: Event) {
  emit('setSimulatedProfile', { humEnabled: (e.target as HTMLInputElement).checked })
}

function onSimulatedHarmonicsChange(e: Event) {
  emit('setSimulatedProfile', { harmonicsEnabled: (e.target as HTMLInputElement).checked })
}

function onSimulatedCrosstalkChange(e: Event) {
  emit('setSimulatedProfile', { crosstalkEnabled: (e.target as HTMLInputElement).checked })
}

/**
 * The simulated amplifier's output impedance and what hangs on it — the two knobs the load-step
 * measurement is demonstrated with. The options are named by their figures, not by an amplifier
 * type, so nothing here duplicates a DUT model's name: the DUT dropdown says what kind of device it
 * is, this one says what impedance its terminals present. The enum values keep the type names.
 */
const OUTPUT_STAGES: { value: SimulatedOutputStageModel; label: string; title: string }[] = [
  { value: 'None', label: 'None', title: 'An ideal voltage source: no output impedance, so the load makes no difference. The default — every other measurement is untouched.' },
  { value: 'SolidState', label: '0.03 Ω + 1.5 µH', title: 'A solid-state amplifier: 0.028 Ω in series with a 1.5 µH output coil — the tutorial’s BC-1. DF 286 at low frequencies, the coil dominating by 20 kHz.' },
  { value: 'TubeAmp', label: '2 Ω + 0.3 mH', title: 'A tube amplifier: 2 Ω in series with 0.3 mH of output-transformer leakage inductance. DF 4 at low frequencies.' },
]

const LOADS: { value: SimulatedLoad; label: string }[] = [
  { value: 'Open', label: 'Open' },
  { value: 'EightOhms', label: '8 Ω' },
  { value: 'FourOhms', label: '4 Ω' },
]

function onSimulatedOutputStageChange(e: Event) {
  emit('setSimulatedProfile', { outputStage: (e.target as HTMLSelectElement).value as SimulatedOutputStageModel })
}

function onSimulatedLoadChange(e: Event) {
  emit('setSimulatedProfile', { load: (e.target as HTMLSelectElement).value as SimulatedLoad })
}

/**
 * Which of the knobs above to show, from the open measurement and the knobs' own values — the rules
 * and their reasoning are in simulator/knobs.ts. The open measurement comes from `useProjects()`
 * rather than a prop, as SimulatorHint.vue already takes it: those refs are module-level singletons,
 * and this component's props are deliberately device-only.
 */
const { types, currentTypeId } = useProjects()

const currentKind = computed(() => types.value.find((t) => t.id === currentTypeId.value)?.kind ?? null)

/**
 * The escape hatch. Without it a knob the bar has hidden cannot be switched on from the screen that
 * hides it — turning Crosstalk on to watch what it does to a THD sweep would mean leaving the
 * sweep — so one button reveals the lot. Session-local and deliberately not persisted: the point is
 * a glance at the full set, not a preference about it.
 */
const showAllKnobs = ref(false)

const knobs = computed(() =>
  showAllKnobs.value ? allSimulatorKnobsVisible() : visibleSimulatorKnobs(currentKind.value, props.status),
)

const anyKnobHidden = computed(() => Object.values(knobs.value).some((shown) => !shown))

/**
 * The four nonlinear amplifier models carry their own distortion and switch the generator's
 * harmonic stack off whatever the profile says, so the checkbox is shown but disabled under one of
 * them — a live checkbox that changes nothing reads as a bug. It is shown rather than hidden on
 * purpose, unlike the knobs `visibleSimulatorKnobs` rules on: "off because this DUT distorts on its
 * own" is worth reading.
 *
 * From `DISTORTING_DUTS` rather than by matching the group label, so this and the hint above the
 * chart cannot drift apart or out of step with the server's `SuppliesOwnDistortion`.
 */
const harmonicsSwitchInert = computed(() => {
  const dut = props.status?.simulatedDut
  return dut != null && DISTORTING_DUTS.includes(dut)
})

const harmonicsSwitchTitle = computed(() =>
  harmonicsSwitchInert.value
    ? "This DUT model distorts on its own, so the generator's harmonic stack is off regardless."
    : 'Fixed harmonic stack on each generator tone: H2 at −46 dBc, H3 at −60 dBc, then 12 dB per order — 0.51 % THD at any level.',
)

function onSimulatedDutChange(e: Event) {
  emit('setSimulatedProfile', { dut: (e.target as HTMLSelectElement).value as SimulatedDutModel })
}

/**
 * The analyzer's identity and its own housekeeping sensors, in the vendor software's order. Which
 * rows exist in which state, the glyphs and the warm-board threshold are all in
 * `device/statusReadout.ts` — nothing in a `.vue` is testable here, and the gating is a rule rather
 * than a formatting choice.
 */
const telemetry = computed(() => telemetryRows(props.status))

/** The dot, the name and the one word at the left-hand end. */
const pill = computed(() => devicePill(props.status, selectedKindLabel.value))

/** What the Analyzer dropdown calls the *selected* analyzer — the pill's name before one answers. */
const selectedKindLabel = computed(
  () => DEVICE_KINDS.find((kind) => kind.value === props.status?.deviceKind)?.label ?? 'Analyzer',
)

/**
 * Reading these costs three register exchanges on the control endpoint, which is the one thing that
 * must not happen with transfers in flight — so the server stops refreshing them for the length of
 * an acquisition. Dimmed rather than hidden while that is true: the numbers are still the last real
 * reading, and a row that disappears whenever a measurement starts reads as a fault.
 */
const telemetryStale = computed(() => props.status?.acquiring === true)

/**
 * Replaces each row's own tooltip while the readings are frozen, rather than sitting on the group:
 * a title on the wrapper is unreachable, since every row inside it carries one of its own.
 */
const telemetryStaleTitle =
  "The analyzer's own sensors — last read before this acquisition started. Reading them needs the control endpoint, which is left alone while samples are streaming."
/**
 * The sound card's controls stand in for the QA403's two attenuators, because a card has neither:
 * one fixed full scale on each side. What varies on that bench is the front-end in front of it, so
 * that is what the bar offers instead.
 */
const isSoundCard = computed(() => props.status?.deviceKind === 'SoundCard')

/**
 * Whose gear button is the QA40x's.
 *
 * <p>The simulator is included deliberately: it models a QA403 and mirrors its generator rate
 * ceiling, so the experimental 384 kHz opt-in is read on that bench too — and it is the one setting
 * in that dialog which is not about hardware being present. The ranging delay hides itself there,
 * as it always did. The same reasoning already makes the external gain popover call that bench "the
 * QA40x bench".</p>
 *
 * <p><b>QA40x rather than QA403</b>, like the picker entry beside it: this is keyed on the
 * <em>kind</em>, so it is the gear a QA402 bench gets too. The dialog's 384 kHz opt-in is in fact
 * QA403-only — <c>Qa40xModel.Capabilities</c> ignores the flag for a QA402 — but the row says so
 * itself, and a title naming one model while opening on either is the fault this spelling exists
 * to remove.</p>
 */
const isQa40x = computed(() => props.status?.deviceKind === 'Hardware' || props.status?.deviceKind === 'Simulated')

const qa40xTitle = computed(() =>
  props.status?.deviceKind === 'Simulated'
    ? "The QA40x's own settings — the experimental 384 kHz rate, which the simulator mirrors. The auto-ranging delay needs a real analyzer."
    : "The QA40x's own settings: the experimental 384 kHz sample rate (a QA403 only) and the auto-ranging step-down delay.",
)

/**
 * dB or a plain ratio, for whichever bench is live. The stored value is always dB — see
 * soundcard/gainUnits.
 */
const gainUnit = computed<GainUnit>(() => (props.status?.externalGainUnit === 'Times' ? 'Times' : 'Db'))

const gainLeftDb = computed(() => props.status?.externalGainLeftDb ?? 0)
const gainRightDb = computed(() => props.status?.externalGainRightDb ?? 0)

/**
 * Whether anything is in front of either input. The badge is shown either way — a gain of 0 is a
 * statement worth having on screen, and it is where the control is — but it is only *amber* when
 * there is a correction being applied. Grey means "nothing declared, every level is as measured".
 */
const gainDeclared = computed(() => gainLeftDb.value !== 0 || gainRightDb.value !== 0)

/**
 * Which document the popover writes to, named so a bench can tell where its figure went. A bare
 * name, no article: since the 2026-10-12 redesign the popover sets it in a badge ("Stored with
 * [QA40x]"), and "the QA40x bench" inside a pill read as "the QA40x bench bench".
 *
 * The simulator and the real analyzer share one name because they share one document — the
 * simulator models a QA403 — so naming it separately would promise a second figure that does not
 * exist. QA40x for that same reason one model further along: a QA402 bench writes to that one
 * document too, so a badge reading "Stored with QA403" there named a file the figure had not gone
 * into.
 */
const gainBenchLabel = computed(() => (isSoundCard.value ? 'Sound card' : 'QA40x'))

const gainOpen = ref(false)

/**
 * The badge, the pencil and the panel together — what counts as "inside" for the press below.
 *
 * <p>All three share one element deliberately. Were the guard the panel alone, a press on the badge
 * would be outside it: this handler would close the panel, and the button's own click would then
 * toggle it straight back open, so the badge would appear not to close it at all. Covering the
 * triggers too is what makes that press the plain toggle it looks like.</p>
 */
const gainAnchor = ref<HTMLElement | null>(null)

/**
 * Closes the panel on a press anywhere outside it — the same `pointerdown`-on-document idiom the
 * project menu uses, rather than a click, so the panel is gone before whatever was pressed acts on
 * it. It stays registered for the component's life instead of being added and removed with the
 * panel: the handler's first line is a no-op while closed, which is cheaper than a watcher and has
 * no window in which an open panel is missing its listener.
 */
function onDocumentPointerDown(event: PointerEvent) {
  if (!gainOpen.value) return
  if (!gainAnchor.value?.contains(event.target as Node)) gainOpen.value = false
}

onMounted(() => document.addEventListener('pointerdown', onDocumentPointerDown))
onBeforeUnmount(() => document.removeEventListener('pointerdown', onDocumentPointerDown))

const soundCardTitle = computed(() => {
  const s = props.status
  if (!s) return ''
  if (!s.soundCardInputDevice) return 'No sound card chosen yet — open the settings to pick one.'
  return s.soundCardCalibrated
    ? `${s.soundCardInputDevice} — calibrated, so every level is a voltage.`
    : `${s.soundCardInputDevice} — not calibrated, so levels are dBFS rather than volts. Ratios (THD, IMD, response shape) are exact either way.`
})

function applyGain(gain: { leftDb: number; rightDb: number; unit: GainUnit }) {
  emit('setExternalGain', gain)
}
</script>

<template>
  <!--
    One 44 px row, the height of the mockup's own artboard, laid out as: the analyzer and what it
    reports about itself on the left, everything that is set on the bench on the right, and a
    hairline between each group of them. It still *wraps* rather than clipping, unlike the mockup's
    `overflow: hidden` — the mockup is 1920 px wide and this app folds its rails at 1024, where a
    clipped bar would put the external gain and both ranges out of reach.
  -->
  <footer
    class="flex min-h-11 flex-wrap items-center gap-x-2.5 gap-y-2 border-t border-neutral-200 bg-[var(--surface-panel)] px-3 py-1.5 text-xs dark:border-neutral-800"
  >
    <!-- The analyzer, as one pill: a dot, what it calls itself, and the one word that says whether
         it is answering. "Removed" is its own word rather than a badge beside it — unplugged
         mid-session is a state of the device, not a second thing to read. -->
    <span
      class="inline-flex h-7 shrink-0 items-center gap-[7px] rounded-full border border-neutral-200 bg-[var(--surface-control)] py-0 pl-2 pr-2.5 font-semibold text-neutral-900 dark:border-neutral-700 dark:text-neutral-100"
      :title="pill.title"
    >
      <span class="h-2 w-2 shrink-0 rounded-full ring-[3px]" :class="DEVICE_DOT_CLASS[pill.tone]" />
      {{ pill.name }}
      <span class="font-normal" :class="BAR_LABEL">{{ pill.state }}</span>
    </span>
    <span
      v-if="status?.isSimulated"
      class="rounded bg-amber-100 px-1.5 py-0.5 font-medium text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"
    >
      Simulated
    </span>
    <!-- Said out loud for the same reason Uncalibrated is, below: the measurement may be perfectly
         good, and nobody here has ever checked. Driven by a server flag rather than by matching the
         product name, so which models are verified stays one fact in Qa40xModel. -->
    <span
      v-if="status?.connected && status.modelUnverified"
      class="rounded bg-amber-100 px-1.5 py-0.5 font-medium text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"
      :title="`Tonebench has never been run against a ${status.productName}. Its USB protocol is taken from QuantAsylum's own PyQa40x and bare-metal examples and from the ASIO40x driver, all of which treat it as identical to the QA403 — but nothing here has been verified against this model. Measurements are probably right; treat them as unconfirmed, and please report what you find.`"
    >
      Unverified model
    </span>
    <!-- Which analyzer the session is talking to. Always offered, and independent of whether it is
         connected: switching to the QA40x with none attached is how you arm the Connect button. -->
    <label v-if="status" class="flex items-center gap-1.5" :class="BAR_LABEL" :title="analyzerSwitchTitle">
      Analyzer
      <select :class="BAR_SELECT" :value="status.deviceKind" :disabled="status.acquiring" @change="onDeviceKindChange">
        <option v-for="kind in DEVICE_KINDS" :key="kind.value" :value="kind.value">{{ kind.label }}</option>
      </select>
    </label>
    <!-- The QA40x's own settings, in the same place and with the same glyph as the sound card's
         below. Keyed on the selected analyzer, not on a connection: these are stored settings,
         and one of them (the rate) decides what the FFT screen offers before anything is up. -->
    <button
      v-if="isQa40x"
      type="button"
      class="flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
      aria-label="QA40x settings"
      :title="qa40xTitle"
      @click="emit('openQa40x')"
    >
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <circle cx="12" cy="12" r="3" />
        <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" />
      </svg>
    </button>
    <!-- Sound-card settings. Keyed on the selected analyzer rather than on a connection, for the
         same reason the simulator's controls are: the card has to be choosable before it is up,
         and when nothing is chosen yet this button is the only way to get anywhere. -->
    <button
      v-if="isSoundCard"
      type="button"
      class="flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
      aria-label="Sound card settings"
      :title="soundCardTitle"
      @click="emit('openSoundCard')"
    >
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <circle cx="12" cy="12" r="3" />
        <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" />
      </svg>
    </button>
    <!-- Said out loud rather than left to be discovered: every ratio is still exact, but nothing
         on screen is a voltage until both calibration steps have run. -->
    <span
      v-if="isSoundCard && status?.connected && status.soundCardCalibrated === false"
      class="rounded bg-amber-100 px-1.5 py-0.5 font-medium text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"
      title="Levels are dBFS, not volts. THD, IMD, crosstalk and the response shape are exact regardless — a consistent full-scale constant cancels out of every ratio."
    >
      Uncalibrated
    </span>

    <!-- Simulator-only: what the synthesized DUT adds on top of the generator's stimulus. Keyed on
         the *selected* analyzer rather than on isSimulated, so the DUT can be chosen while the
         simulator is still coming up. Each knob is then shown only where the open measurement can
         read it, or while it is engaged — see simulator/knobs.ts. The DUT dropdown is never
         hidden: picking a model that does not suit the screen is how the hint above the chart gets
         its chance to say so. -->
    <template v-if="status?.deviceKind === 'Simulated'">
      <label
        v-if="knobs.noise"
        class="flex items-center gap-1.5"
        :class="BAR_LABEL"
        title="Simulated DUT noise floor (white + 1/f). Turn off for a clean spectrum."
      >
        <input type="checkbox" :checked="status.simulatedNoiseEnabled ?? false" @change="onSimulatedNoiseChange" />
        Noise
      </label>
      <label
        v-if="knobs.hum"
        class="flex items-center gap-1.5"
        :class="BAR_LABEL"
        title="Simulated mains hum: 50 Hz plus two harmonics, each 6 dB down."
      >
        <input type="checkbox" :checked="status.simulatedHumEnabled ?? false" @change="onSimulatedHumChange" />
        Hum
      </label>
      <label
        v-if="knobs.harmonics"
        class="flex items-center gap-1.5"
        :class="[BAR_LABEL, { 'opacity-50': harmonicsSwitchInert }]"
        :title="harmonicsSwitchTitle"
      >
        <input
          type="checkbox"
          :checked="status.simulatedHarmonicsEnabled ?? false"
          :disabled="harmonicsSwitchInert"
          @change="onSimulatedHarmonicsChange"
        />
        Harmonics
      </label>
      <label
        v-if="knobs.crosstalk"
        class="flex items-center gap-1.5"
        :class="BAR_LABEL"
        title="Simulated inter-channel coupling: each channel leaks into the other at −70 dB through the audio band, rising 6 dB per octave above 6 kHz (≈ −59 dB at 20 kHz sampled at 192 kHz, −53 dB at 48 kHz) — a capacitive coupling. Off, the channels are perfectly separate."
      >
        <input type="checkbox" :checked="status.simulatedCrosstalkEnabled ?? false" @change="onSimulatedCrosstalkChange" />
        Crosstalk
      </label>
      <label
        v-if="knobs.outputStage"
        class="flex items-center gap-1.5"
        :class="BAR_LABEL"
        title="The output impedance the simulated amplifier presents at its terminals — a property of the amplifier, separate from the DUT model, which is its open-circuit behaviour. None by default; the other two only take effect once a load is connected below, and then on every measurement, not only the output-impedance one."
      >
        Zout
        <select :class="BAR_SELECT" :value="status.simulatedOutputStage ?? 'None'" @change="onSimulatedOutputStageChange">
          <option v-for="stage in OUTPUT_STAGES" :key="stage.value" :value="stage.value" :title="stage.title">{{ stage.label }}</option>
        </select>
      </label>
      <label
        v-if="knobs.load"
        class="flex items-center gap-1.5"
        :class="BAR_LABEL"
        title="What hangs on the simulated amplifier's output. Change it between the two passes of an output-impedance run, the way you would swap the resistor on a bench."
      >
        Load
        <select :class="BAR_SELECT" :value="status.simulatedLoad ?? 'Open'" @change="onSimulatedLoadChange">
          <option v-for="load in LOADS" :key="load.value" :value="load.value">{{ load.label }}</option>
        </select>
      </label>
      <label
        class="flex items-center gap-1.5"
        :class="BAR_LABEL"
        title="What the simulator pretends is patched between its generator and its input. A straight wire has no response and no memory, so the impulse response is a single spike and both decay surfaces show nothing."
      >
        DUT
        <select :class="BAR_SELECT" :value="status.simulatedDut ?? 'StraightWire'" @change="onSimulatedDutChange">
          <optgroup v-for="group in DUT_GROUPS" :key="group" :label="group">
            <option
              v-for="model in DUT_MODELS.filter((m) => m.group === group)"
              :key="model.value"
              :value="model.value"
              :title="model.title"
            >
              {{ model.label }}
            </option>
          </optgroup>
        </select>
      </label>
      <button
        v-if="anyKnobHidden || showAllKnobs"
        type="button"
        class="rounded px-1 leading-none text-neutral-400 hover:text-neutral-900 dark:text-neutral-500 dark:hover:text-neutral-100"
        :class="{ 'text-neutral-900 dark:text-neutral-100': showAllKnobs }"
        :aria-pressed="showAllKnobs"
        :aria-label="showAllKnobs ? 'Hide the simulator controls this measurement does not use' : 'Show all simulator controls'"
        :title="
          showAllKnobs
            ? 'Showing every simulator control. Click to go back to the ones this measurement reads.'
            : 'Some simulator controls are hidden because this measurement cannot read them. Click to show them all.'
        "
        @click="showAllKnobs = !showAllKnobs"
      >
        ⋯
      </button>
    </template>

    <!--
      The app starts whether or not a QA40x is attached (a missing one used to abort startup
      outright), so "offline" is a normal state to arrive in rather than a failure. Covers the
      mid-session unplug too: the server releases the dead handle and reports connected=false, so
      the same button brings the device back once it is plugged in again. Hidden while the error
      pill below is showing its own Retry — the two do the same thing, and one affordance at a time.
    -->
    <button
      v-if="status && !status.connected && !(error && errorRetryable)"
      type="button"
      class="h-7 rounded-md border border-neutral-300 px-2.5 font-medium text-neutral-700 hover:bg-neutral-100 disabled:opacity-50 dark:border-neutral-700 dark:text-neutral-200 dark:hover:bg-neutral-800"
      :disabled="connecting"
      title="Look for a QA40x on USB and connect to it — every product id in Qa40xModel.All is tried, QA403 first."
      @click="emit('connect')"
    >
      {{ connecting ? 'Connecting…' : 'Connect' }}
    </button>
    <span
      v-if="status?.acquiring"
      class="rounded bg-emerald-100 px-1.5 py-0.5 font-medium text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-300"
    >
      Acquiring
    </span>

    <span v-if="telemetry.length > 0" class="mx-0.5 h-5 border-l border-neutral-200 dark:border-neutral-800" />

    <!--
      The analyzer's identity and its own housekeeping sensors, in the vendor software's order. Not
      a measurement of anything on the bench: they say whether the USB port is feeding the unit
      properly and how warm it has become.

      Dimmed, not hidden, while a measurement runs: reading the three sensors costs three register
      exchanges on the control endpoint, which is the one thing that must not happen with transfers
      in flight, so the server stops refreshing them for the length of an acquisition. The numbers
      are still the last real reading, and a row that disappears whenever a measurement starts
      reads as a fault.
    -->
    <span v-if="telemetry.length > 0" class="flex items-center gap-3.5" :class="telemetryStale ? 'opacity-50' : ''">
      <span
        v-for="row in telemetry"
        :key="row.key"
        class="flex items-center gap-1.5"
        :title="telemetryStale ? telemetryStaleTitle : row.title"
      >
        <svg
          width="13"
          height="13"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2.2"
          stroke-linecap="round"
          stroke-linejoin="round"
          class="shrink-0"
          :class="row.iconClass"
          aria-hidden="true"
        >
          <path :d="row.icon" />
        </svg>
        <span v-if="row.label" :class="BAR_LABEL">{{ row.label }}</span>
        <span class="font-mono" :class="row.valueClass">{{ row.value }}</span>
      </span>
    </span>

    <!--
      Only shown once it happens, because it should never happen. A dropped chunk splices the
      sample stream, which lifts the measured noise floor and smears tones — so this is a warning
      that the numbers on screen are wrong, not a throughput statistic.
    -->
    <span
      v-if="status && status.droppedRxChunks > 0"
      class="rounded bg-red-100 px-1.5 py-0.5 font-medium text-red-800 dark:bg-red-900/40 dark:text-red-300"
      title="RX chunks dropped because the host fell behind. Each one splices the sample stream, so noise-floor and THD+N readings taken since are unreliable."
    >
      {{ status.droppedRxChunks }} dropped
    </span>
    <!--
      The other half of the pipeline, and shown separately for the same reason the server keeps two
      counters: RX chunks drop before calibration, blocks drop after it. Only droppedRxChunks was
      ever displayed, so this bar read clean through runs that lost 439 blocks — 1.8 M frames, over
      9 seconds of audio — which is exactly the "the numbers on screen are wrong" case it exists to
      warn about.
    -->
    <span
      v-if="status && status.droppedBlocks > 0"
      class="rounded bg-red-100 px-1.5 py-0.5 font-medium text-red-800 dark:bg-red-900/40 dark:text-red-300"
      title="Assembled blocks dropped because the consumer fell behind calibration. Each one is a hole in the sample stream, so noise-floor and THD+N readings taken since are unreliable."
    >
      {{ status.droppedBlocks }} blocks dropped
    </span>
    <!--
      The generator's own fault rather than the pipeline's, and the only badge here that is about
      what went *out*. Red, and worded as a run rather than a count: the live flag beside the output
      range covers the stimulus in force, and this is the one that survives a sweep whose loudest
      points clipped and whose last point did not. A clipped stimulus adds harmonics, so on the
      distortion screens it is the quantity being plotted; nothing else on screen shows it, since the
      generator panel still reads the level that was asked for.
    -->
    <span
      v-if="status?.outputClippedDuringAcquisition"
      class="rounded bg-red-100 px-1.5 py-0.5 font-medium text-red-800 dark:bg-red-900/40 dark:text-red-300"
      title="The generator asked for more than the output range could deliver at some point during this run, so part of what reached the DUT was clipped — and a clipped stimulus has distortion of its own. Lower the generator level, or widen the output range if you pinned it. Cleared when the next run starts."
    >
      generator clipped
    </span>
    <!--
      The transport's own drops, and the one of the three that does NOT mean the measurement is
      wrong: the samples all arrived and anything saved from this run is sound. What is lost is a
      frame of one tick's *set*, which this client pairs by seq — so a torn tick reads as the
      oscilloscope's "no fundamental to remove" flicker, or a chart a tick behind the meters.
      Amber rather than red for exactly that reason, and worded as "display" so the distinction is
      on the badge rather than only in the tooltip. The max is shown because one wedged tab says
      nothing about the others; the total comes along once they differ.
    -->
    <span
      v-if="status && status.droppedStreamFramesMax > 0"
      class="rounded bg-amber-100 px-1.5 py-0.5 font-medium text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"
      title="Stream frames dropped on the way to the browser because a client fell behind. The measurement itself is unaffected — the samples all arrived — but a tick that loses one of its frames can draw torn, which is what the oscilloscope's flicker looks like."
    >
      {{ status.droppedStreamFramesMax }} display
      <template v-if="status.droppedStreamFramesTotal > status.droppedStreamFramesMax">
        / {{ status.droppedStreamFramesTotal }} total
      </template>
      dropped
    </span>

    <!--
      The request error, inline beside the analyzer it is about rather than on a line of its own
      under the bar. Truncated with the whole message in the tooltip: the line is shared by the 2 s
      poll and every action in App.vue, and some of what lands here is a sentence. Retry is offered
      only where retrying means "open the analyzer" — see errorRetryable.
    -->
    <span
      v-if="error"
      role="status"
      class="inline-flex h-[26px] min-w-0 items-center gap-1.5 rounded-full border border-red-200 bg-red-50 px-2.5 text-red-700 dark:border-red-900 dark:bg-red-950/50 dark:text-red-300"
      :title="error"
    >
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" class="shrink-0" aria-hidden="true">
        <circle cx="12" cy="12" r="9" />
        <path d="M12 7v6M12 16.5h.01" />
      </svg>
      <span class="truncate">{{ error }}</span>
      <button
        v-if="errorRetryable"
        type="button"
        class="ml-1 h-5 shrink-0 rounded-full bg-red-600 px-2 text-[11px] font-semibold text-white hover:bg-red-700 disabled:opacity-50"
        :disabled="connecting"
        title="Look for the analyzer again and connect to it."
        @click="emit('connect')"
      >
        {{ connecting ? 'Connecting…' : 'Retry' }}
      </button>
    </span>

    <span class="flex-1"></span>

    <!--
      The programmable load — a second instrument, independent of which analyzer is selected, and
      the only thing on this bar that is not about the analyzer at all.

      Hidden until a load has been chosen in Configuration ▸ Acquisition. Few benches own a QA451,
      and a permanently empty readout in front of everyone else would be worse than no feature:
      with the setting at its default this bar is what it was before.

      The box is itself the button, the way the external-gain badge beside it is: the readings are
      what you want, and making them inert with a separate icon to click reads as a bug. Its
      readings are segments rather than a dot-separated string so the eye can find the one it is
      after — every one of them is a different kind of thing.
    -->
    <span v-if="showLoadBox" class="flex items-center gap-1.5">
      <span :class="BAR_LABEL">Load</span>
      <button
        type="button"
        class="flex h-7 items-stretch overflow-hidden rounded-md border font-mono"
        :class="LOAD_TONE_CLASS[loadBox.tone]"
        aria-haspopup="dialog"
        aria-label="Programmable load"
        :title="loadBox.title"
        @click="emit('openLoad')"
      >
        <span class="flex items-center bg-teal-50 px-2.5 font-semibold text-teal-700 dark:bg-teal-950/50 dark:text-teal-300">
          {{ loadBox.model }}
        </span>
        <span v-if="load?.connected === false" :class="BAR_SEGMENT" class="text-neutral-500 dark:text-neutral-400">
          not available
        </span>
        <template v-else>
          <span :class="BAR_SEGMENT">{{ loadBox.impedance }}</span>
          <span v-if="loadBox.dutPower" :class="BAR_SEGMENT" class="gap-1.5">
            <span class="h-1.5 w-1.5 rounded-full" :class="load?.dutPowerOn === true ? 'bg-emerald-500' : 'bg-neutral-400'" />
            <span
              :class="
                load?.dutPowerOn === true
                  ? 'font-semibold text-emerald-600 dark:text-emerald-400'
                  : 'text-neutral-500 dark:text-neutral-400'
              "
            >
              {{ loadBox.dutPower }}
            </span>
          </span>
          <span v-if="loadBox.temperature" :class="BAR_SEGMENT">{{ loadBox.temperature }}</span>
          <span v-if="loadBox.current" :class="BAR_SEGMENT">{{ loadBox.current }}</span>
        </template>
      </button>
    </span>

    <span class="mx-0.5 h-5 border-l border-neutral-200 dark:border-neutral-800" />

    <!--
      Always on the bar, on every analyzer, because it is now the only place the external gain can
      be read or set -- the Configuration dialog's copy is gone, and with it the field that wrote
      a value the sound card's bench never applied.

      Amber only while something is declared: a divider in front of an input corrects every level
      on screen, and a bench that has forgotten it is on reads a DUT 20 dB louder than it is. Grey
      at 0 says the opposite just as usefully -- nothing in the path, every level as measured --
      and keeps the control where the eye already learned to find it.

      One button, with the pencil inside it: the number is the thing you want to change, and the
      two separate buttons this replaced both did the same thing.
    -->
    <span v-if="status" ref="gainAnchor" class="relative flex items-center gap-1.5">
      <span :class="BAR_LABEL">Ext. gain</span>
      <button
        type="button"
        class="flex h-7 items-center gap-[7px] rounded-md border py-0 pl-2.5 pr-2 font-mono"
        :class="
          gainDeclared
            ? 'border-amber-400 bg-amber-50 text-amber-900 dark:border-amber-600 dark:bg-amber-950/40 dark:text-amber-200'
            : 'border-neutral-200 bg-[var(--surface-control)] text-neutral-900 dark:border-neutral-700 dark:text-neutral-100'
        "
        :aria-expanded="gainOpen"
        aria-label="Edit external gain"
        :title="
          gainDeclared
            ? 'External gain in front of the inputs: every level shown is the DUT’s output, with this undone. Click to change.'
            : 'Nothing declared in front of the inputs, so every level shown is as measured. Click to declare a divider or a front-end.'
        "
        @click="gainOpen = !gainOpen"
      >
        <!-- A dot when a gain is declared, as the mockup draws it: a divider the bench has
             forgotten about makes every level wrong by its whole size, so the badge has to say
             "something is set here" before it is read. -->
        <span v-if="gainDeclared" class="h-1.5 w-1.5 shrink-0 rounded-full bg-amber-500"></span>
        L {{ formatGain(gainLeftDb, gainUnit) }} · R {{ formatGain(gainRightDb, gainUnit) }}
        <svg
          viewBox="0 0 24 24"
          class="h-[13px] w-[13px] shrink-0"
          :class="gainDeclared ? 'text-amber-600 dark:text-amber-300' : 'text-blue-600 dark:text-blue-400'"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M12 20h9" />
          <path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z" />
        </svg>
      </button>

      <!--
        Anchored above the badge rather than centred on the screen: it edits the thing it is
        pointing at, and a modal for two numbers would dim the very readings the gain changes.
        bottom-full puts it clear of the bar; right-0 keeps it inside the window, since the badge
        sits at the right-hand end of a bar that reaches the edge.
      -->
      <div v-if="gainOpen" class="absolute bottom-full right-0 z-30 mb-2">
        <ExternalGainPopover
          :left-db="gainLeftDb"
          :right-db="gainRightDb"
          :unit="gainUnit"
          :bench-label="gainBenchLabel"
          :disabled="rangesLocked"
          @apply="applyGain"
          @close="gainOpen = false"
        />
      </div>
    </span>

    <!--
      Auto is the default and is what every measurement needs: the input range tracks the signal
      (the device's own hardware ranger, or the session's SessionInputRanger where there is
      none) and the output range is derived from the generator
      amplitude (OutputRangeSelector). Manual pins both, which is what reproducing another
      analyzer's reading takes — the same 8.6 dBV loopback measured on the 12 dBV input range and
      on the 18 dBV one differs by 5 dB of H3 and 1.7 dB of noise floor, and nothing but the range
      explains it.
    -->
    <label v-if="status && !isSoundCard" class="flex items-center gap-1.5" :class="BAR_LABEL" :title="rangingTitle">
      Ranging
      <select :class="BAR_SELECT" :value="status.rangingMode" :disabled="rangesLocked" @change="onRangingModeChange">
        <option v-for="mode in RANGING_MODES" :key="mode.value" :value="mode.value">{{ mode.label }}</option>
      </select>
    </label>
    <span v-if="!isSoundCard" class="flex items-center gap-1.5" :class="BAR_LABEL" :title="attenuationTitle">
      Attenuation
      <!--
        The dropdown replaces the readout rather than sitting beside it: two widgets showing one
        number invite the question of which is the truth. Both read status.inputRangeDbv, which is
        the device's own answer (IAnalyzerDevice.InputRangeDbv) rather than what was last asked
        for — so a range the device declines to take shows up as the select snapping back. The
        "auto" chip is only on the readout, because under Manual it is not.
      -->
      <select
        v-if="manual"
        :class="BAR_SELECT"
        class="font-mono"
        :value="String(status?.inputRangeDbv ?? '')"
        :disabled="rangesLocked || !status?.connected"
        @change="onInputRangeChange"
      >
        <option v-for="dbv in inputRanges" :key="dbv" :value="String(dbv)">{{ dbv }} dB</option>
      </select>
      <span v-else :class="BAR_READOUT">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" class="shrink-0 text-blue-600 dark:text-blue-400" aria-hidden="true">
          <path :d="RANGE_ICON.attenuation" />
        </svg>
        {{ status?.inputRangeDbv ?? '—' }} dB
        <span :class="BAR_AUTO_CHIP">auto</span>
      </span>
    </span>
    <!--
      The output range is an analog attenuator whose setting sets the DAC's noise floor, which is
      why Auto derives it rather than parking on 18 dBV: that wasted 33 dB of headroom on a -15 dBV
      tone and put the noise straight into every loopback measurement. Manual hands the choice
      over, and a level the chosen range cannot carry is said out loud rather than quietly
      corrected — correcting it would make "Manual" mean "manual until the app disagrees".
    -->
    <span v-if="!isSoundCard" class="flex items-center gap-1.5" :class="outputRangeClass" :title="outputRangeTitle">
      Output range
      <select
        v-if="manual"
        :class="BAR_SELECT"
        class="font-mono"
        :value="String(status?.outputRangeDbv ?? '')"
        :disabled="rangesLocked || !status?.connected"
        @change="onOutputRangeChange"
      >
        <option v-for="dbv in outputRanges" :key="dbv" :value="String(dbv)">{{ dbv }} dBV</option>
      </select>
      <span v-else :class="BAR_READOUT">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" class="shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true">
          <path :d="RANGE_ICON.output" />
        </svg>
        {{ status?.outputRangeDbv ?? '—' }} dBV
        <span :class="BAR_AUTO_CHIP">auto</span>
      </span>
      <span v-if="status?.outputRangeClipping" class="font-medium">clipping</span>
    </span>

    <!-- Persistent, unlike the request error above: the startup check found the file damaged, and
         nothing the user does in this session changes that. -->
    <p
      v-if="status?.databaseProblem"
      class="w-full text-red-600 dark:text-red-400"
      title="The measurement database failed its integrity check when the app started. Rows outside the damaged pages still work; a damaged result fails to open. Keep a copy of the file before repairing it."
    >
      Database damaged: {{ status.databaseProblem }}
    </p>
  </footer>
</template>
