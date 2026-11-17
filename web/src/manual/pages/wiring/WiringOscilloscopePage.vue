<script setup lang="ts">
import WiringDiagram from '../../components/WiringDiagram.vue'
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Wiring: the oscilloscope</h1>

    <p>
      The scope measures whatever is on an input, so any of this manual's wirings will do — a
      <button type="button" class="manual-link" @click="showPage('wiring-loopback')">loopback</button>,
      a
      <button type="button" class="manual-link" @click="showPage('wiring-line-level')">line-level DUT</button>,
      a
      <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">power amplifier</button>
      through its divider. What is worth wiring differently is the <em>second</em> input, because a
      scope is the one screen where having the stimulus on the picture beside the response is worth
      a cable.
    </p>

    <h2>The reference leg</h2>

    <WiringDiagram
      :terminate="['in-l-', 'in-r-']"
      :height="380"
      alt="Output L plus drives the device under test and also goes to Input R plus; the device's output returns to Input L plus. Both minus inputs carry shorting plugs."
      caption="One cable out, one back, and a tee from the same output to the right input."
    >
      <template #default="{ cable, link }">
        <rect x="200" y="300" width="290" height="60" rx="4" class="w-block-fill" />
        <text x="345" y="328" text-anchor="middle" class="w-label">Device under test</text>
        <text x="345" y="345" text-anchor="middle" class="w-note">amplifier, filter, stage…</text>

        <path class="w-cable w-drive" :d="cable('out-l+', 300, [242, 300])" />
        <path class="w-cable w-measure" :d="cable('in-l+', 300, [445, 300])" />
        <path class="w-cable w-drive" :d="link('out-l+', 'in-r+', 215)" />
        <circle cx="242" cy="300" r="3" class="w-node" />
        <circle cx="445" cy="300" r="3" class="w-node" />

        <text x="250" y="262" class="w-label w-drive">Drive</text>
        <text x="453" y="262" class="w-label w-measure">Measure</text>
        <text x="345" y="205" text-anchor="middle" class="w-note w-drive">
          Reference: what went in
        </text>
      </template>
    </WiringDiagram>

    <ul>
      <li><strong>OUT L+</strong> to the DUT's input, through a BNC tee.</li>
      <li><strong>The tee's other leg</strong> to <strong>IN R+</strong> — the reference.</li>
      <li><strong>DUT output</strong> to <strong>IN L+</strong>.</li>
      <li>
        <strong>Shorting plugs on IN L− and IN R−.</strong> Both, as always: an open − leg costs
        about 33 dB of noise floor. See
        <button type="button" class="manual-link" @click="showPage('wiring-basics')">
          Connections and cables
        </button>.
      </li>
    </ul>

    <p>
      Tick <strong>Left</strong> and <strong>Right</strong> under Acquisition ▸ Channels and both
      traces are captured, drawn and measured together. The difference between them is the DUT, and
      you can read it directly: the delay through it, the inversion if it has one, which half
      clips first, and how an edge changes shape on the way through.
    </p>

    <h3>Levels</h3>
    <p>
      The two inputs share one range, and it is chosen for the louder of them. With a DUT that has
      gain, that is the output; the reference then sits well down the range, which is fine for
      looking at it and poor for measuring it. If the reference matters more than the output, drive
      harder and attenuate the output instead — the same trade the
      <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">power amplifier</button>
      page describes.
    </p>

    <h2>Two things this wiring cannot give you</h2>
    <ul>
      <li>
        <strong>DC.</strong> The inputs are AC-coupled, with a corner near 0.2 Hz: a step of DC
        moves the trace for about a second and then it drifts back to where it was. A bias voltage,
        a rail, an offset at a speaker terminal — all of them read as zero here after a moment, and
        the <strong>Mean</strong> reading on a quiet input is the analyzer's own few millivolts of
        residual offset rather than anything of the DUT's. Use a multimeter for DC.
      </li>
      <li>
        <strong>A fast edge of its own making.</strong> The analyzer's DAC rolls off near 86 kHz, so
        the squarest square it can play rises in about 4 µs. That is roughly one sample at
        192 kS/s, so a rise time measured from it is the analyzer's unless the DUT is slower than
        about 1 V/µs. With the reference leg wired as above you can at least see <em>both</em>
        edges and compare them, which is the honest version of the measurement.
      </li>
    </ul>

    <h2>Looking at something the analyzer did not make</h2>
    <p>
      There is no trigger socket and no external trigger input, but the second channel is a perfectly
      good substitute: put the signal you want to trigger on into the right input, set
      <strong>Source</strong> to Right, and watch the left. That covers most of what an external
      trigger is for — a sync pulse, a gate, the other half of a stereo pair. The only wiring rule
      is the usual one: whatever you connect, the unused − inputs keep their plugs.
    </p>

    <div class="manual-note">
      <p>
        Nothing in this app is a high-voltage instrument. The input range tops out at 42 dBV, about
        126 V rms, and a speaker terminal on a large amplifier will exceed that at full output — use
        the divider from the
        <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
          power amplifier
        </button>
        page and tell the app about it under Configuration ▸ Acquisition ▸ External gain, so the
        trace reads in the DUT's own volts.
      </p>
    </div>
  </div>
</template>
