<script setup lang="ts">
import { computed } from 'vue'
import type { DeviceKindName, DeviceStatus, SimulatedDutModel, SimulatedLoad, SimulatedOutputStageModel, SimulatedProfileRequest } from '../api/client'

const DEVICE_KINDS: { value: DeviceKindName; label: string }[] = [
  { value: 'Hardware', label: 'QA403' },
  { value: 'Simulated', label: 'Simulator' },
]

type DutGroup =
  | 'Linear — response, no distortion'
  | 'Filters — the four classic low-pass alignments, 5th order at 2 kHz'
  | 'Distorting — for THD and IMD'
  | 'Impedance fixtures — for the impedance screen'

/**
 * What the simulator can pretend is patched in. A straight wire has no response and no memory,
 * which is enough for a spectrum and shows nothing at all on the measurements whose subject is
 * what a DUT does over time. Four groups, because twenty-two flat entries are unreadable: the
 * linear models (a shape and a memory, unity gain), the four classic filters (the same again, at
 * one corner, for comparing alignments), the nonlinear ones (distortion that depends on level or
 * frequency — the THD sweeps' models), and the impedance fixtures (dividers, so never unity,
 * wired bench-style with the load junction on the right input).
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
    value: 'ButterworthLowPass',
    label: 'Butterworth LP (2 kHz)',
    title:
      'A fifth-order Butterworth low-pass at 2 kHz: maximally flat, −3 dB at the corner, then 100 dB per decade. The reference the other three filters are read against — same order, same corner, a different trade each.',
    group: 'Filters — the four classic low-pass alignments, 5th order at 2 kHz',
  },
  {
    value: 'ChebyshevLowPass',
    label: 'Chebyshev LP (2 kHz)',
    title:
      'A fifth-order Chebyshev low-pass, 1 dB ripple: the passband ripples between 0 and −1 dB, the corner is the ripple edge at −1 dB, and the transition just above it is twice as steep as the Butterworth’s. Its group delay peaks at the corner — see the delay column on the response screens.',
    group: 'Filters — the four classic low-pass alignments, 5th order at 2 kHz',
  },
  {
    value: 'BesselLowPass',
    label: 'Bessel LP (2 kHz)',
    title:
      'A fifth-order Bessel low-pass, −3 dB at 2 kHz: the flattest group delay of the four — 193 µs, constant across the passband — at the price of the gentlest roll-off, 28 dB down where the Butterworth is 48. The model for the group-delay and phase-delay columns.',
    group: 'Filters — the four classic low-pass alignments, 5th order at 2 kHz',
  },
  {
    value: 'EllipticLowPass',
    label: 'Elliptic LP (2 kHz)',
    title:
      'A fifth-order elliptic (Cauer) low-pass, 1 dB passband ripple, 60 dB stopband: the only one with zeros — two notches at 3.5 and 5.3 kHz — and the steepest transition anything of this order can have, −60 dB by 4 kHz. Between and above the notches the stopband comes back up to −60 dB and stays there, which is what the zeros cost.',
    group: 'Filters — the four classic low-pass alignments, 5th order at 2 kHz',
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
  'Filters — the four classic low-pass alignments, 5th order at 2 kHz',
  'Distorting — for THD and IMD',
  'Impedance fixtures — for the impedance screen',
]

const props = defineProps<{
  status: DeviceStatus | null
  error: string | null
  /** True while a connect attempt is in flight — the emit is fire-and-forget, so the button can't tell on its own. */
  connecting: boolean
}>()

const emit = defineEmits<{
  setSimulatedProfile: [patch: SimulatedProfileRequest]
  setDeviceKind: [kind: DeviceKindName]
  connect: []
}>()

function onDeviceKindChange(e: Event) {
  emit('setDeviceKind', (e.target as HTMLSelectElement).value as DeviceKindName)
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
 * The four nonlinear amplifier models carry their own distortion and switch the generator's
 * harmonic stack off whatever the profile says, so the checkbox is shown but disabled under one of
 * them — a live checkbox that changes nothing reads as a bug.
 */
const harmonicsSwitchInert = computed(() => {
  const dut = props.status?.simulatedDut
  return dut != null && DUT_MODELS.some((m) => m.value === dut && m.group === 'Distorting — for THD and IMD')
})

const harmonicsSwitchTitle = computed(() =>
  harmonicsSwitchInert.value
    ? "This DUT model distorts on its own, so the generator's harmonic stack is off regardless."
    : 'Fixed harmonic stack on each generator tone: H2 at −46 dBc, H3 at −60 dBc, then 12 dB per order — 0.51 % THD at any level.',
)

function onSimulatedDutChange(e: Event) {
  emit('setSimulatedProfile', { dut: (e.target as HTMLSelectElement).value as SimulatedDutModel })
}

/** Signed, so a divider reads "−20" and an amplifier "+20" at a glance. */
function formatGain(db: number): string {
  return `${db > 0 ? '+' : ''}${db.toFixed(db % 1 === 0 ? 0 : 1)}`
}
</script>

<template>
  <footer
    class="flex flex-wrap items-center gap-4 border-t border-neutral-200 bg-white px-4 py-2 text-xs dark:border-neutral-800 dark:bg-neutral-900"
  >
    <div class="flex items-center gap-2">
      <span class="h-2.5 w-2.5 rounded-full" :class="status?.connected ? 'bg-emerald-500' : 'bg-neutral-400'" />
      <span class="font-semibold text-neutral-900 dark:text-neutral-100">
        {{ status?.connected ? status.productName : 'Disconnected' }}
      </span>
      <!-- Says which kind of disconnected this is: never attached, or unplugged while we were using it. -->
      <span
        v-if="status && !status.connected && status.deviceLost"
        class="rounded bg-amber-100 px-1.5 py-0.5 text-xs font-medium text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"
        title="The QA403 stopped responding — it was unplugged or powered off. Plug it back in and press Connect."
      >
        Device removed
      </span>
      <span
        v-if="status?.isSimulated"
        class="rounded bg-amber-100 px-1.5 py-0.5 text-xs font-medium text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"
      >
        Simulated
      </span>
      <!-- Which analyzer the session is talking to. Always offered, and independent of whether it is
           connected: switching to the QA403 with none attached is how you arm the Connect button. -->
      <label
        v-if="status"
        class="flex items-center gap-1.5 text-neutral-500 dark:text-neutral-400"
        :title="analyzerSwitchTitle"
      >
        Analyzer
        <select
          class="rounded border border-neutral-300 bg-transparent px-1 py-0.5 text-xs disabled:opacity-50 dark:border-neutral-700"
          :value="status.deviceKind"
          :disabled="status.acquiring"
          @change="onDeviceKindChange"
        >
          <option v-for="kind in DEVICE_KINDS" :key="kind.value" :value="kind.value">{{ kind.label }}</option>
        </select>
      </label>

      <!-- Simulator-only: what the synthesized DUT adds on top of the generator's stimulus. Keyed on
           the *selected* analyzer rather than on isSimulated, so the DUT can be chosen while the
           simulator is still coming up. -->
      <template v-if="status?.deviceKind === 'Simulated'">
        <label
          class="flex items-center gap-1.5 text-neutral-500 dark:text-neutral-400"
          title="Simulated DUT noise floor (white + 1/f). Turn off for a clean spectrum."
        >
          <input type="checkbox" :checked="status.simulatedNoiseEnabled ?? false" @change="onSimulatedNoiseChange" />
          Noise
        </label>
        <label
          class="flex items-center gap-1.5 text-neutral-500 dark:text-neutral-400"
          title="Simulated mains hum: 50 Hz plus two harmonics, each 6 dB down."
        >
          <input type="checkbox" :checked="status.simulatedHumEnabled ?? false" @change="onSimulatedHumChange" />
          Hum
        </label>
        <label
          class="flex items-center gap-1.5 text-neutral-500 dark:text-neutral-400"
          :class="{ 'opacity-50': harmonicsSwitchInert }"
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
          class="flex items-center gap-1.5 text-neutral-500 dark:text-neutral-400"
          title="Simulated inter-channel coupling: each channel leaks into the other at −70 dB through the audio band, rising 6 dB per octave above 6 kHz (≈ −59 dB at 20 kHz sampled at 192 kHz, −53 dB at 48 kHz) — a capacitive coupling. Off, the channels are perfectly separate."
        >
          <input type="checkbox" :checked="status.simulatedCrosstalkEnabled ?? false" @change="onSimulatedCrosstalkChange" />
          Crosstalk
        </label>
        <label
          class="flex items-center gap-1.5 text-neutral-500 dark:text-neutral-400"
          title="The output impedance the simulated amplifier presents at its terminals — a property of the amplifier, separate from the DUT model, which is its open-circuit behaviour. None by default; the other two only take effect once a load is connected below, and then on every measurement, not only the output-impedance one."
        >
          Zout
          <select
            class="rounded border border-neutral-300 bg-transparent px-1 py-0.5 text-xs dark:border-neutral-700"
            :value="status.simulatedOutputStage ?? 'None'"
            @change="onSimulatedOutputStageChange"
          >
            <option v-for="stage in OUTPUT_STAGES" :key="stage.value" :value="stage.value" :title="stage.title">{{ stage.label }}</option>
          </select>
        </label>
        <label
          class="flex items-center gap-1.5 text-neutral-500 dark:text-neutral-400"
          title="What hangs on the simulated amplifier's output. Change it between the two passes of an output-impedance run, the way you would swap the resistor on a bench."
        >
          Load
          <select
            class="rounded border border-neutral-300 bg-transparent px-1 py-0.5 text-xs dark:border-neutral-700"
            :value="status.simulatedLoad ?? 'Open'"
            @change="onSimulatedLoadChange"
          >
            <option v-for="load in LOADS" :key="load.value" :value="load.value">{{ load.label }}</option>
          </select>
        </label>
        <label
          class="flex items-center gap-1.5 text-neutral-500 dark:text-neutral-400"
          title="What the simulator pretends is patched between its generator and its input. A straight wire has no response and no memory, so the impulse response is a single spike and both decay surfaces show nothing."
        >
          DUT
          <select
            class="rounded border border-neutral-300 bg-transparent px-1 py-0.5 text-xs dark:border-neutral-700"
            :value="status.simulatedDut ?? 'StraightWire'"
            @change="onSimulatedDutChange"
          >
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
      </template>
      <!--
        The app starts whether or not a QA403 is attached (a missing one used to abort startup
        outright), so "Disconnected" is a normal state to arrive in rather than a failure. Covers the
        mid-session unplug too: the server releases the dead handle and reports connected=false, so
        the same button brings the device back once it is plugged in again.
      -->
      <button
        v-if="status && !status.connected"
        type="button"
        class="rounded border border-neutral-300 px-2 py-0.5 text-xs font-medium text-neutral-700 hover:bg-neutral-100 disabled:opacity-50 dark:border-neutral-700 dark:text-neutral-200 dark:hover:bg-neutral-800"
        :disabled="connecting"
        title="Look for a QA403 on USB and connect to it."
        @click="emit('connect')"
      >
        {{ connecting ? 'Connecting…' : 'Connect' }}
      </button>
      <span
        v-if="status?.acquiring"
        class="rounded bg-emerald-100 px-1.5 py-0.5 text-xs font-medium text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-300"
      >
        Acquiring
      </span>
    </div>

    <div v-if="status?.connected" class="flex items-center gap-3 text-neutral-500 dark:text-neutral-400">
      <span>S/N {{ status.serialNumber }}</span>
      <span>FW {{ status.firmwareVersion }}</span>
      <!--
        Only shown once it happens, because it should never happen. A dropped chunk splices the
        sample stream, which lifts the measured noise floor and smears tones — so this is a warning
        that the numbers on screen are wrong, not a throughput statistic.
      -->
      <span
        v-if="status.droppedRxChunks > 0"
        class="rounded bg-red-100 px-1.5 py-0.5 text-xs font-medium text-red-800 dark:bg-red-900/40 dark:text-red-300"
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
        v-if="status.droppedBlocks > 0"
        class="rounded bg-red-100 px-1.5 py-0.5 text-xs font-medium text-red-800 dark:bg-red-900/40 dark:text-red-300"
        title="Assembled blocks dropped because the consumer fell behind calibration. Each one is a hole in the sample stream, so noise-floor and THD+N readings taken since are unreliable."
      >
        {{ status.droppedBlocks }} blocks dropped
      </span>
    </div>

    <div class="ml-auto flex items-center gap-4">
      <!--
        Only while something is in the path: a divider in front of an input corrects every level on
        screen, and a bench that has forgotten it is on reads a DUT 20 dB louder than it is.
      -->
      <span
        v-if="status && (status.externalGainLeftDb !== 0 || status.externalGainRightDb !== 0)"
        class="flex items-center gap-2 text-amber-700 dark:text-amber-400"
        title="External gain in front of the inputs (Configuration ▸ Acquisition): every level shown is the DUT's output, with this undone."
      >
        Ext. gain
        <span class="rounded border border-amber-300 px-2 py-0.5 font-mono dark:border-amber-700">
          L {{ formatGain(status.externalGainLeftDb) }} · R {{ formatGain(status.externalGainRightDb) }} dB
        </span>
      </span>
      <!--
        Read-only: auto-ranging is always on (see AcquisitionPanel's former Auto checkbox, now
        removed) — either the device's own hardware ranger or, where that's not available (the
        simulator), App.vue's live peak-level monitor (see determineAttenuation in attenuation.ts).
        Either way this is never something to pick by hand anymore, only to watch.
      -->
      <span
        class="flex items-center gap-2 text-neutral-500 dark:text-neutral-400"
        title="Automatically kept matched to the input signal — by the device's own auto-ranging where available, otherwise by continuously monitoring the peak level."
      >
        Attenuation
        <span class="rounded border border-neutral-200 px-2 py-0.5 font-mono dark:border-neutral-800">
          {{ status?.inputRangeDbv ?? '--' }} dB
        </span>
      </span>
      <!--
        Read-only: the output range is derived from the generator's own amplitude
        (OutputRangeSelector), the way QuantAsylum's software does it, because it is an analog
        attenuator whose setting sets the DAC's noise floor. Leaving it on 18 dBV for a -15 dBV tone
        wasted 33 dB of headroom and put that noise straight into every loopback measurement.
      -->
      <span
        class="flex items-center gap-2 text-neutral-500 dark:text-neutral-400"
        title="Selected automatically from the generator amplitude — the lowest range with enough headroom, for the lowest DAC noise."
      >
        Output range
        <span class="rounded border border-neutral-200 px-2 py-0.5 font-mono dark:border-neutral-800">
          {{ status?.outputRangeDbv ?? '--' }} dBV
        </span>
      </span>
    </div>

    <p v-if="error" class="w-full text-red-600 dark:text-red-400">{{ error }}</p>
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
