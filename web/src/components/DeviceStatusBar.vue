<script setup lang="ts">
import { computed } from 'vue'
import type { DeviceKindName, DeviceStatus, SimulatedDutModel, SimulatedProfileRequest } from '../api/client'

const DEVICE_KINDS: { value: DeviceKindName; label: string }[] = [
  { value: 'Hardware', label: 'QA403' },
  { value: 'Simulated', label: 'Simulator' },
]

/**
 * What the simulator can pretend is patched in. A straight wire has no response and no memory,
 * which is enough for a spectrum and shows nothing at all on the measurements whose subject is
 * what a DUT does over time.
 */
const DUT_MODELS: { value: SimulatedDutModel; label: string; title: string }[] = [
  { value: 'StraightWire', label: 'Straight wire', title: 'No DUT: the generator reaches the input unaltered.' },
  {
    value: 'TubeAmplifier',
    label: 'Tube amp',
    title:
      'A push-pull valve amplifier: rolled off at both ends, with the output transformer’s resonance just above the audio band. Most of that sits above 24 kHz, so set the span to 96 kHz to see it.',
  },
  {
    value: 'Loudspeaker',
    label: 'Loudspeaker',
    title:
      'A two-way loudspeaker in a room: a cabinet panel resonance, a cone breakup peak, and a wall reflection 3 ms behind the direct sound. The model to use when demonstrating the impulse-response screen.',
  },
  {
    value: 'BandpassFilter',
    label: 'Bandpass',
    title:
      'A single second-order bandpass at 1 kHz, Q 20 — 50 Hz wide, and nothing else in the path. The one model whose decay you can work out on paper: 27.29/Q = 1.36 dB per period, which is the slope a burst decay must draw. Use it to check the analysis rather than to demonstrate a device.',
  },
  {
    value: 'MarginallyStable',
    label: 'Marginally stable',
    title:
      'An amplifier almost out of phase margin: flat, with one Q-500 resonance at 1 kHz that rings for a third of a second. Stable — but on a burst decay it falls only 1.6 dB over thirty periods, so the ridge refuses to die. On a magnitude plot it is just a peak, which is the argument for having decay charts.',
  },
  {
    value: 'Oscillating',
    label: 'Oscillating',
    title:
      'An amplifier that has gone the rest of the way: poles outside the unit circle, clipping against its own ±0.5 V rail at about 1010 Hz. The only DUT whose output is not a response to its input — it wrecks coherence, the level probe and the deconvolution, which is exactly what it is for.',
  },
  {
    value: 'ImpedanceResistive',
    label: 'Fixture: 8 Ω',
    title:
      'An impedance fixture rather than a DUT: a 100 Ω reference resistor with a flat 8 Ω load. The one model in this list whose answer is arithmetic — a correct impedance run reads 8.000 Ω at 0.00° at every frequency, through either stimulus. Set the impedance screen’s reference channel to Right, because the simulator puts its DUT on the left.',
  },
  {
    value: 'ImpedanceReactive',
    label: 'Fixture: 6 Ω + 1 mH',
    title:
      'The same 100 Ω fixture with a series 6 Ω and 1 mH, which puts the 45° point near 955 Hz. The model to use when checking that phase is measured rather than assumed.',
  },
  {
    value: 'ImpedanceLoudspeaker',
    label: 'Fixture: loudspeaker',
    title:
      'The same fixture with a driver on it: 6.4 Ω voice coil and a 45 Hz resonance peaking at 60 Ω, Q 2.5. The shape an impedance sweep is normally run to see.',
  },
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

function onSimulatedDutChange(e: Event) {
  emit('setSimulatedProfile', { dut: (e.target as HTMLSelectElement).value as SimulatedDutModel })
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
          title="What the simulator pretends is patched between its generator and its input. A straight wire has no response and no memory, so the impulse response is a single spike and both decay surfaces show nothing."
        >
          DUT
          <select
            class="rounded border border-neutral-300 bg-transparent px-1 py-0.5 text-xs dark:border-neutral-700"
            :value="status.simulatedDut ?? 'StraightWire'"
            @change="onSimulatedDutChange"
          >
            <option v-for="model in DUT_MODELS" :key="model.value" :value="model.value" :title="model.title">
              {{ model.label }}
            </option>
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
  </footer>
</template>
