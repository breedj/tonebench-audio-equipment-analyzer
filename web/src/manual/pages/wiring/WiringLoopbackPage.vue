<script setup lang="ts">
import WiringDiagram from '../../components/WiringDiagram.vue'
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Wiring: loopback</h1>

    <p>
      Output straight back to input, with no DUT in the path at all. It measures the analyzer
      itself: its own distortion, its own noise floor, its own frequency response and its own
      channel-to-channel crosstalk. Every one of those is the floor under whatever you measure next,
      so a loopback is the right first move of a session and the right sanity check whenever a
      number looks wrong.
    </p>

    <WiringDiagram
      :terminate="['in-l-', 'in-r-']"
      :height="310"
      alt="Output L plus wired to Input L plus, Output R plus to Input R plus, with shorting plugs on both minus inputs."
      caption="Single-ended loopback, both channels. The unused − inputs still need their plugs."
    >
      <template #default="{ link }">
        <path class="w-cable w-measure" :d="link('out-r+', 'in-r+', 205)" />
        <path class="w-cable w-measure" :d="link('out-l+', 'in-l+', 252)" />
        <text x="345" y="185" text-anchor="end" class="w-label">Right</text>
        <text x="255" y="232" text-anchor="end" class="w-label">Left</text>
        <text x="320" y="292" text-anchor="middle" class="w-note">
          One BNC cable per channel. Nothing else connected.
        </text>
      </template>
    </WiringDiagram>

    <h2>What it is good for</h2>
    <ul>
      <li>
        <strong>The analyzer's own figures.</strong> On this bench, 1 kHz at −20 dBV, 48 kHz, 32k
        Hann, 8 averages, input range 0 / output −12: THD −118.6 dB left and −113.9 dB right, THD+N
        0.0015 %, SNR 96.4 dB, noise −159 dBV/√Hz. Yours will differ a little, and the two channels
        will differ from each other — that is normal.
      </li>
      <li>
        <strong>Proving a fault is not the DUT.</strong> A floor that stays high in loopback is the
        analyzer, the cables, the plugs or the room.
      </li>
      <li>
        <strong>The impulse screen's loopback calibration.</strong> Capture one here, with the DUT
        out of the path, and the analyzer's own converters and anti-alias filter divide out of every
        later impulse measurement. See
        <button type="button" class="manual-link" @click="showPage('impulse-response')">
          Impulse response
        </button>.
      </li>
      <li>
        <strong>A crosstalk floor.</strong> Run a
        <button type="button" class="manual-link" @click="showPage('crosstalk')">crosstalk</button>
        sweep on this wiring and you measure the analyzer's own channel separation — about −100 dB
        at 20 kHz here, and the best any amplifier measurement can show.
      </li>
    </ul>

    <h2>Levels</h2>
    <p>
      There is no DUT gain, so the generator level and the input range are yours to choose. Leave
      headroom: the input range should be a step above the level you are driving, and the output
      range follows the amplitude automatically. Driving −20 dBV into a 0 dBV range is a sensible
      default, and staying below the 24 dBV input range keeps the analyzer on its quiet side of the
      internal attenuator.
    </p>

    <h2>Balanced loopback</h2>
    <p>
      For a balanced loop, run a second cable from <strong>OUT L−</strong> to
      <strong>IN L−</strong> instead of fitting a plug, and the same on the right. It exercises both
      legs of each pair and is what you want if the DUT you are about to measure is balanced. For
      everything else the single-ended loop above is simpler and quieter, because one cable encloses
      a smaller loop than two.
    </p>

    <h2>If the loopback looks wrong</h2>
    <ul>
      <li>
        <strong>A high, flat, white floor that does not move when the generator is switched off</strong>
        is on the input side — almost always a missing or worn shorting plug. See
        <button type="button" class="manual-link" @click="showPage('wiring-basics')">
          Connections and cables
        </button>.
      </li>
      <li>
        <strong>Mains harmonics</strong> are environmental: a transformer nearby, a switching supply,
        LED lighting, or the computer's own supply. A laptop on battery is the quickest test.
      </li>
      <li>
        <strong>One channel worse than the other</strong> by a lot, when it was not before, is
        usually the plug on that channel's − input rather than the analyzer.
      </li>
    </ul>
  </div>
</template>
