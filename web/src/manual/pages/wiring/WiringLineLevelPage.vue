<script setup lang="ts">
import WiringDiagram from '../../components/WiringDiagram.vue'
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Wiring: a line-level DUT</h1>

    <p>
      One channel in, one channel out. This is the wiring for almost everything the app measures —
      FFT spectrum, both THD sweeps, IMD, frequency response, stepped sine response and impulse
      response — whenever the device under test works at line level: a preamplifier, a filter, a
      phono stage, an equaliser, a crossover board, a DAC's analogue output.
    </p>
    <p>
      For a <em>power</em> amplifier the same signal path applies, but the levels do not: see
      <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
        Wiring: a power amplifier
      </button>
      for the load, the divider and the ceiling.
    </p>

    <h2>The basic connection</h2>

    <WiringDiagram
      :terminate="['in-l-', 'in-r+', 'in-r-']"
      :height="360"
      alt="Output L plus drives the device under test; the device's output returns to Input L plus. The remaining three inputs carry shorting plugs."
      caption="One cable out, one cable back. Everything else is plugged."
    >
      <template #default="{ cable }">
        <rect x="200" y="280" width="290" height="60" rx="4" class="w-block-fill" />
        <text x="345" y="308" text-anchor="middle" class="w-label">Device under test</text>
        <text x="345" y="325" text-anchor="middle" class="w-note">preamp, filter, phono stage…</text>

        <path class="w-cable w-drive" :d="cable('out-l+', 280, [242, 280])" />
        <path class="w-cable w-measure" :d="cable('in-l+', 280, [445, 280])" />
        <circle cx="242" cy="280" r="3" class="w-node" />
        <circle cx="445" cy="280" r="3" class="w-node" />

        <text x="250" y="208" class="w-label w-drive">Drive</text>
        <text x="453" y="208" class="w-label w-measure">Measure</text>
      </template>
    </WiringDiagram>

    <ul>
      <li><strong>OUT L+</strong> to the DUT's input.</li>
      <li><strong>DUT output</strong> to <strong>IN L+</strong>.</li>
      <li>
        <strong>Shorting plugs on IN L−, IN R+ and IN R−.</strong> All three, even though only one
        input is in use — an open − leg costs 33 dB of noise floor.
      </li>
      <li>The unused <strong>OUT R</strong> stays empty. Never plug an output.</li>
    </ul>

    <h3>Setting the levels</h3>
    <p>
      The generator's amplitude is what the DUT sees, so start low and work up: a preamplifier with
      30 dB of gain turns −20 dBV into +10 dBV. Set the input range a step above the level you
      expect back, and prefer to stay below the 24 dBV range, where the analyzer's own noise floor
      jumps about 20 dB as an internal relay comes in.
    </p>
    <p>
      If you would rather name the level at the <em>DUT's output</em> than at the generator, the
      chirp and stepped-sine screens have an output-referred drive mode that measures the DUT's gain
      first and sets the generator itself. It is on the
      <button type="button" class="manual-link" @click="showPage('frequency-response')">
        Frequency response
      </button>
      page.
    </p>

    <h2>With the right input as a reference</h2>
    <p>
      Phase — and everything built on it: group delay, Nyquist, Nichols, margins, a pole–zero fit —
      needs a second input measuring what actually went <em>into</em> the DUT. The measurement is
      then the ratio of the two inputs rather than the left input against the sweep the app
      generated, and the generator, the cabling and the analyzer's own output stage divide out.
    </p>

    <WiringDiagram
      :terminate="['in-l-', 'in-r-']"
      :height="380"
      alt="As before, with a T-piece at the device's input feeding Input R plus as the reference channel."
      caption="A T-piece at the DUT's input takes the reference. Tick Right channel is reference."
    >
      <template #default="{ cable }">
        <rect x="200" y="275" width="290" height="60" rx="4" class="w-block-fill" />
        <text x="345" y="303" text-anchor="middle" class="w-label">Device under test</text>
        <text x="345" y="320" text-anchor="middle" class="w-note">preamp, filter, phono stage…</text>

        <path class="w-cable w-drive" :d="cable('out-l+', 275, [242, 275])" />
        <path class="w-cable w-measure" :d="cable('in-l+', 275, [445, 275])" />
        <path class="w-cable w-reference" :d="cable('in-r+', 255, [242, 255])" />
        <circle cx="242" cy="255" r="3.6" class="w-node" />
        <circle cx="242" cy="275" r="3" class="w-node" />
        <circle cx="445" cy="275" r="3" class="w-node" />

        <text x="250" y="200" class="w-label w-drive">Drive</text>
        <text x="453" y="200" class="w-label w-measure">Measure</text>
        <text x="272" y="247" class="w-label w-reference">Reference — the T-piece</text>
        <text x="345" y="366" text-anchor="middle" class="w-note">
          The reference tees onto the drive cable at the DUT's input.
        </text>
      </template>
    </WiringDiagram>

    <ul>
      <li>
        Put a <strong>BNC T-piece</strong> at the DUT's input: one leg from OUT L+, one leg on to the
        DUT, one leg to <strong>IN R+</strong>.
      </li>
      <li><strong>IN R−</strong> keeps its shorting plug; only IN R+ is now in use.</li>
      <li>
        Turn on <strong>Right channel is reference</strong> (chirp and stepped response) or set
        <strong>Reference</strong> to <strong>Right input channel</strong> (impulse response).
      </li>
    </ul>
    <p>
      Looping <strong>OUT R+</strong> straight to <strong>IN R+</strong> also works and needs no
      T-piece, which is why the
      <button type="button" class="manual-link" @click="showPage('wiring-output-impedance')">
        output impedance
      </button>
      measurement uses it — both generator channels play the same stimulus. It cancels slightly
      less, because the reference then travels through the right output stage and the right input
      rather than through the cable that actually drives the DUT. Use the T-piece where you can.
    </p>
    <p>
      Note what the reference does <em>not</em> cancel: the analyzer's two input channels are still
      two different channels, and nothing on the input side divides out. For the band edges of an
      impulse measurement, a stored
      <button type="button" class="manual-link" @click="showPage('wiring-loopback')">loopback</button>
      calibration is the tool for that.
    </p>

    <h2>A balanced DUT</h2>
    <p>
      Measure it single-ended anyway: <strong>IN L+</strong> on the DUT's hot output leg, the
      cable's shell on its ground, and the shorting plug left on IN L−. A balanced output whose two
      legs are referenced to ground reads 6 dB lower this way, which is arithmetic rather than a
      fault — it is half the signal, and the DUT's distortion and noise figures are unchanged.
    </p>
    <p>
      Two cases where that does not work. A <strong>floating or bridged</strong> output has neither
      leg at ground, so the shell would ground one of them; measure it through an external
      differential probe or transformer instead, and enter whatever attenuation that introduces as
      the external input gain. And a balanced <em>input</em> on the DUT still needs its cold leg
      taken somewhere: drive from OUT L+ and tie the DUT's cold input to its own ground, exactly as
      a single-ended source would.
    </p>

    <h2>Common mistakes</h2>
    <ul>
      <li>
        <strong>A missing shorting plug.</strong> The floor rises about 33 dB, stays flat and white,
        and does not move when the generator is switched off.
      </li>
      <li>
        <strong>Driving the DUT through the reference T-piece's stub.</strong> The T belongs at the
        DUT's input, not at the analyzer's output; a long stub to the DUT with the T left at the
        panel measures the cable, not the drive.
      </li>
      <li>
        <strong>An input range left over from the previous measurement.</strong> Every sweep probes
        and sets the range itself, but the live FFT screen does not — check the status bar if a
        spectrum looks clipped or buried.
      </li>
    </ul>
  </div>
</template>
