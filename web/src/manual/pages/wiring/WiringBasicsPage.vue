<script setup lang="ts">
import WiringDiagram from '../../components/WiringDiagram.vue'
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Connections and cables</h1>

    <p>
      Eight BNCs: two outputs and two inputs, each a <strong>balanced pair</strong> of a
      <strong>+</strong> and a <strong>−</strong> connector. The shell of every one of them is
      chassis ground; the centre pins carry the two legs of the pair. Everything the analyzer
      measures is the difference between a pair's + and − pins — which is why every input's −
      connector must be shorted to ground, in use or not. Leave one open and the analyzer subtracts
      whatever that open pin picks up from your measurement.
    </p>

    <WiringDiagram
      :terminate="['in-l+', 'in-l-', 'in-r+', 'in-r-']"
      :height="196"
      alt="The QA403 front panel with shorting plugs fitted to all four input BNCs."
      caption="The baseline before a session: four shorting plugs and nothing else connected."
    >
      <template #default>
        <text x="320" y="180" text-anchor="middle" class="w-note">
          Shorting plugs on all four inputs — never on an output.
        </text>
      </template>
    </WiringDiagram>

    <h2>Everything here is wired single-ended</h2>
    <p>
      An ordinary coaxial cable carries one signal and a ground, so a single-ended connection uses
      the <strong>+</strong> connector of a pair and nothing else — the DUT's ground arrives through
      the shell. <strong>No measurement in this app is made differentially</strong>, so every page
      that follows wires it this way and no diagram ever puts a cable on a − input.
    </p>
    <ul>
      <li>
        <strong>On an output</strong>, drive from <strong>+</strong> and leave <strong>−</strong>
        open.
      </li>
      <li>
        <strong>On an input</strong>, measure on <strong>+</strong> and fit a
        <strong>shorting plug</strong> to <strong>−</strong> — a 0, 50 or 75 Ω BNC terminator.
        <strong>Always, on every input, whether or not that input is in use.</strong> This is not
        optional, and it is the single most expensive mistake on this bench.
      </li>
      <li>
        <strong>Never fit a shorting plug to an output.</strong> It is a short across the analyzer's
        output stage.
      </li>
    </ul>

    <h3>What an unterminated − input costs</h3>
    <p>
      An open − input is an aerial hanging on the inverting leg of a differential amplifier, and the
      analyzer subtracts everything it picks up into the measurement. Measured on this bench, 1 kHz
      loopback at −20 dBV:
    </p>
    <table>
      <thead>
        <tr>
          <th></th>
          <th>− inputs open</th>
          <th>− inputs plugged</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>THD</td>
          <td>−87 dB</td>
          <td>−119 dB</td>
        </tr>
        <tr>
          <td>Noise density</td>
          <td>−126 dBV/√Hz</td>
          <td>−159 dBV/√Hz</td>
        </tr>
      </tbody>
    </table>
    <p>
      Thirty-three decibels, and the tell is subtle: the floor was flat and white with no mains
      structure, and it did not move when the generator was switched off. It matched the
      <em>24 dBV range's</em> published floor while the analyzer was on the 0 dBV range, which is
      what finally gave it away. A plug can also work loose mid-session — then one channel degrades
      and the other does not, which is the quickest proof that the fault is physical rather than in
      the app.
    </p>

    <h3>The baseline check</h3>
    <p>
      Worth running at the start of a session, before anything is connected to the DUT: fit all four
      plugs, set the input range to 0 dBV, the sample rate to 48 kHz and the FFT size to 64k or
      more, and look at the noise. You should see roughly <strong>−117 dBV</strong> RMS on both
      channels and <strong>no mains lines at all</strong>. Mains components here are environmental —
      a transformer, a switching supply, an LED lamp — and not something the DUT will explain later.
    </p>

    <h2>The +32 dBV ceiling</h2>
    <p>
      <strong>+32 dBV is 40 V rms, and it is the maximum the inputs will take.</strong> Into 8 Ω
      that is 200 W. Exceeding it damages the analyzer, and no setting protects you: the input range
      list goes up to 42 dBV, but that is an internal attenuator ahead of the ADC, not a higher
      ceiling. Anything that can swing past 40 V rms needs an
      <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
        external divider
      </button>
      — and leave some margin, because clean readings want the signal a few decibels below the
      range anyway.
    </p>

    <h2>Hum, ground loops and the shells</h2>
    <p>
      Every BNC shell is the same ground, so each extra cable between the analyzer and the DUT is
      another path in parallel with the last, and the loop they enclose picks up whatever is around
      it. An FFT of a few hundred nanovolts per bin is brutal about showing it.
    </p>
    <ul>
      <li>
        <strong>Keep the drive cable short and away from the DUT's output stage.</strong> With an
        amplifier of gain 20, anything that gets into the input cable arrives at the analyzer
        twenty times larger than the same pickup on the output cable.
      </li>
      <li>
        <strong>As few cables as the measurement needs.</strong> Driving and measuring one channel
        joins the two chassis twice, and that loop is fixed by the measurement rather than by the
        wiring — so keep both cables short and run them alongside each other rather than around
        opposite sides of the bench, which is what makes the loop small. Unplug the cables a
        measurement is not using; see the
        <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
          power amplifier
        </button>
        page, where the loop matters most.
      </li>
      <li>
        <strong>Computer and DUT on the same outlet</strong>, and a laptop on its battery is better
        still. A desktop is often improved by a powered USB hub, which also guarantees the analyzer
        a solid supply.
      </li>
      <li><strong>Switch off CFL and LED lighting</strong> for noise work.</li>
    </ul>

    <h2>How to read these diagrams</h2>
    <p>
      Each page draws the analyzer's front panel with the cables a measurement needs. The colours
      mean:
    </p>
    <ul>
      <li>
        <strong class="manual-wire-drive">Blue</strong> — the analyzer driving the DUT, from a
        generator output.
      </li>
      <li>
        <strong class="manual-wire-measure">Orange</strong> — what is being measured, back into an
        input.
      </li>
      <li>
        <strong class="manual-wire-reference">Purple, dashed</strong> — a reference: a second input
        the measurement is taken relative to, rather than against the generated signal.
      </li>
    </ul>
    <p>
      A dark cap on a connector is a shorting plug. A dot where two lines meet is a junction;
      lines that cross without one are not connected.
    </p>
    <p>
      <strong>A ground symbol is a BNC shell.</strong> The analyzer has no separate earth terminal —
      the shell of every one of its eight connectors is its chassis ground, and that is the only
      ground connection it has. So wherever a diagram draws a ground, the wire that actually gets
      there is the outer shell of a cable already in the picture: the return of the coax measuring
      the amplifier's output, the reference resistor's end of the impedance fixture, the amplifier's
      own chassis reached back through those same shells. Nothing needs a separate ground lead, and
      adding one only enlarges the loop that picks up hum.
    </p>

    <p>
      The wiring for each measurement:
      <button type="button" class="manual-link" @click="showPage('wiring-loopback')">loopback</button>,
      <button type="button" class="manual-link" @click="showPage('wiring-line-level')">
        a line-level DUT
      </button>,
      <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
        a power amplifier
      </button>,
      <button type="button" class="manual-link" @click="showPage('wiring-impedance')">
        the impedance fixture
      </button>,
      <button type="button" class="manual-link" @click="showPage('wiring-crosstalk')">crosstalk</button>
      and
      <button type="button" class="manual-link" @click="showPage('wiring-output-impedance')">
        output impedance
      </button>.
    </p>
  </div>
</template>

<style>
/* The three cable colours, so the legend above can name them in the same ink the diagrams use. */
.manual-prose .manual-wire-drive {
  color: #2563eb;
}
.manual-prose .manual-wire-measure {
  color: #ea580c;
}
.manual-prose .manual-wire-reference {
  color: #7c3aed;
}
.dark .manual-prose .manual-wire-drive {
  color: #60a5fa;
}
.dark .manual-prose .manual-wire-measure {
  color: #fb923c;
}
.dark .manual-prose .manual-wire-reference {
  color: #a78bfa;
}
</style>
