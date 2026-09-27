<script setup lang="ts">
import WiringDiagram from '../../components/WiringDiagram.vue'
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Plugging everything in</h1>

    <p>
      The front of the analyzer has eight sockets: two outputs and two inputs, left and right, and
      each of those four is a <em>pair</em> of sockets marked <strong>+</strong> and
      <strong>−</strong>. Eight in total. You will only ever use the <strong>+</strong> ones.
    </p>

    <h2>Rule 1: a shorting plug on every − input, always</h2>
    <p>
      Fit a BNC shorting plug to <strong>IN L−</strong> and <strong>IN R−</strong> and leave them
      there forever. Fit them to the <strong>+</strong> inputs too whenever nothing is connected to
      them.
    </p>
    <p>
      The reason is that each input measures the <em>difference</em> between its + and its − socket.
      An empty − socket is a little aerial, and whatever it picks up — mains hum, the computer's
      power supply, the room — gets subtracted from your measurement. Measured on this bench, one
      missing plug made the noise floor <strong>33 dB worse</strong> and the distortion reading ten
      times worse. Nothing on screen says "you forgot a plug"; it just looks like a bad amplifier.
    </p>

    <WiringDiagram
      :terminate="['in-l+', 'in-l-', 'in-r+', 'in-r-']"
      :height="196"
      alt="The analyzer's front panel with shorting plugs on all four input sockets."
      caption="How the analyzer should sit when nothing is connected: four plugs, and no cables."
    >
      <template #default>
        <text x="320" y="180" text-anchor="middle" class="w-note">
          Plugs on all four inputs — and never on an output.
        </text>
      </template>
    </WiringDiagram>

    <h2>Rule 2: never put a plug in an output</h2>
    <p>
      A shorting plug does what its name says: it shorts the socket to ground. In an input that is
      exactly what you want. In an output it is a dead short across the analyzer's own generator.
      Plugs belong on the right-hand half of the panel only.
    </p>

    <h2>Rule 3: never more than 40 V into an input</h2>
    <p>
      40 V is the limit, and it is a hard one — above it you damage the analyzer, and no setting in
      this app protects you. 40 V does not sound like much once an amplifier is involved:
    </p>
    <table>
      <thead>
        <tr>
          <th>Amplifier into 8 Ω</th>
          <th>Output voltage</th>
          <th>Safe to connect directly?</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>1 W</td>
          <td>2.8 V</td>
          <td>Yes</td>
        </tr>
        <tr>
          <td>20 W</td>
          <td>12.6 V</td>
          <td>Yes</td>
        </tr>
        <tr>
          <td>100 W</td>
          <td>28 V</td>
          <td>Yes, with nothing to spare</td>
        </tr>
        <tr>
          <td>200 W</td>
          <td>40 V</td>
          <td>No — this is the limit itself</td>
        </tr>
      </tbody>
    </table>
    <p>
      If the amplifier can go louder than about 100 W, or if you simply do not know, put a
      <strong>divider</strong> between it and the analyzer — two resistors that pass on a tenth of
      the voltage. The full manual's
      <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
        Wiring: a power amplifier
      </button>
      page gives the values, and shows where to tell the app about it so the readings still come out
      in the amplifier's own volts.
    </p>

    <div class="manual-note">
      <p>
        <strong>A valve amplifier must never be driven with its loudspeaker or load resistor
        disconnected.</strong> The output transformer has nowhere to put the energy and the voltage
        can climb until something arcs. Connect the load first, then the analyzer, then switch on.
        The same order is good practice for any amplifier.
      </p>
    </div>

    <h2>The normal connection</h2>
    <p>
      For almost everything: one cable from <strong>OUT L+</strong> to the input of the thing you are
      testing, and one cable from its output back to <strong>IN L+</strong>. The ground comes along
      the outside of the cables, so two cables is all it takes.
    </p>

    <WiringDiagram
      :terminate="['in-l-', 'in-r+', 'in-r-']"
      :height="360"
      alt="Output L plus drives the device under test; its output returns to Input L plus. The other three inputs carry shorting plugs."
      caption="One cable out, one cable back, and plugs in the three inputs you are not using."
    >
      <template #default="{ cable }">
        <rect x="200" y="280" width="290" height="60" rx="4" class="w-block-fill" />
        <text x="345" y="308" text-anchor="middle" class="w-label">What you are testing</text>
        <text x="345" y="325" text-anchor="middle" class="w-note">amplifier, filter, preamp…</text>

        <path class="w-cable w-drive" :d="cable('out-l+', 280, [242, 280])" />
        <path class="w-cable w-measure" :d="cable('in-l+', 280, [445, 280])" />
        <circle cx="242" cy="280" r="3" class="w-node" />
        <circle cx="445" cy="280" r="3" class="w-node" />

        <text x="250" y="208" class="w-label w-drive">Out</text>
        <text x="453" y="208" class="w-label w-measure">Back in</text>
      </template>
    </WiringDiagram>

    <h2>Keeping hum out</h2>
    <p>
      An analyzer can see a few millionths of a volt, so it sees every bit of hum your ears never
      notice. Four things help, in order of how much:
    </p>
    <ul>
      <li>
        <strong>Fit the plugs.</strong> Most mystery noise is a missing one.
      </li>
      <li>
        <strong>Use short cables and keep them together</strong> rather than running them around
        opposite sides of the bench. Two cables side by side enclose almost no area, and it is the
        enclosed area that picks up hum.
      </li>
      <li>
        <strong>Computer and amplifier on the same wall socket.</strong> A laptop running on its
        battery is quieter still.
      </li>
      <li><strong>Switch off LED and fluorescent lights</strong> when you are chasing noise.</li>
    </ul>

    <h2>A five-minute check worth doing first</h2>
    <p>
      Before connecting anything at all: fit all four plugs, open an
      <strong>FFT analysis</strong> measurement, leave the generator off and press
      <strong>RUN</strong>. You should see a flat, featureless floor around
      <strong>−117 dBV</strong> and no spikes at 50 Hz. That is your bench at its best. If there are
      hum spikes now, they are coming from the room and not from anything you are about to measure —
      much easier to learn that now than halfway through an amplifier.
    </p>
    <p>
      Next:
      <button type="button" class="manual-link" @click="showPage('guide-first-measurement')">
        Your first measurement
      </button>.
    </p>
  </div>
</template>
