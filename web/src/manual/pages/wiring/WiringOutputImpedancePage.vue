<script setup lang="ts">
import WiringDiagram from '../../components/WiringDiagram.vue'
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Wiring: output impedance</h1>

    <p>
      The amplifier's output impedance is found by measuring its gain into two different loads and
      solving for the source resistance that explains the difference. So the wiring is the
      <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
        power amplifier
      </button>
      arrangement with one addition: you swap the load part way through, when the run stops and
      waits for you.
    </p>

    <WiringDiagram
      :terminate="['in-r-']"
      :height="430"
      alt="Output L plus drives the amplifier; its two output terminals go to Input L plus and Input L minus; Output R plus loops to Input R plus as a reference; the load is fitted at the output terminals and swapped part way through."
      caption="Load 1 first. When the run pauses, swap it for Load 2 and press CONTINUE."
    >
      <template #default="{ cable, link, path }">
        <rect x="190" y="270" width="210" height="70" rx="4" class="w-block-fill" />
        <text x="295" y="300" text-anchor="middle" class="w-label">Power amplifier</text>
        <text x="295" y="318" text-anchor="middle" class="w-note">one channel</text>

        <!-- Output terminals -->
        <path class="w-thin" :d="path([[400, 290], [530, 290]])" />
        <path class="w-thin" :d="path([[400, 330], [530, 330]])" />

        <!-- Load 1, fitted -->
        <path class="w-thin" :d="path([[530, 290], [530, 297]])" />
        <path class="w-thin" :d="path([[530, 323], [530, 330]])" />
        <rect x="518" y="297" width="24" height="26" class="w-block" />
        <text x="530" y="352" text-anchor="middle" class="w-note">Load 1</text>

        <!-- Load 2, in its place later -->
        <path class="w-thin w-dashed" :d="path([[530, 290], [600, 290], [600, 297]])" />
        <path class="w-thin w-dashed" :d="path([[530, 330], [600, 330], [600, 323]])" />
        <rect x="588" y="297" width="24" height="26" class="w-block w-dashed" />
        <text x="600" y="352" text-anchor="middle" class="w-note">Load 2</text>

        <path class="w-cable w-drive" :d="cable('out-l+', 270, [242, 270])" />
        <path class="w-cable w-measure" :d="cable('in-l+', 290, [445, 290])" />
        <path class="w-cable w-measure" :d="cable('in-l-', 250, [490, 330])" />
        <circle cx="445" cy="290" r="3.2" class="w-node" />
        <circle cx="490" cy="330" r="3.2" class="w-node" />

        <path class="w-cable w-reference" :d="link('out-r+', 'in-r+', 190)" />
        <text x="478" y="186" class="w-label w-reference">Reference</text>

        <text x="250" y="216" class="w-label w-drive">Drive</text>
        <text x="330" y="246" text-anchor="end" class="w-label w-measure">Measure</text>
        <text x="320" y="400" text-anchor="middle" class="w-note">
          Take the measurement pair from the terminals; fit the load on top of it.
        </text>
      </template>
    </WiringDiagram>

    <ul>
      <li>
        <strong>OUT L+ to the amplifier's input</strong>, and the two output terminals to
        <strong>IN L+</strong> and <strong>IN L−</strong>.
      </li>
      <li>
        <strong>Take the measurement pair from the terminals themselves and fit the load on top
        of it.</strong> This is a Kelvin connection, and here it is not a refinement: the whole
        answer is the difference between two gains a few hundredths of a decibel apart, so any
        resistance between the tap and the terminal is added straight onto the result.
      </li>
      <li>
        <strong>Both load resistors non-inductive and within a few tens of centimetres.</strong> One
        microhenry reads as 0.13 Ω at 20 kHz, which on a decent amplifier is more than the whole
        answer. Enter their <em>measured</em> values in <strong>Load 1</strong> and
        <strong>Load 2</strong>.
      </li>
      <li>
        <strong>OUT R+ looped to IN R+</strong> for the complex method. Tick
        <strong>Right channel is reference</strong> and each gain is measured with its phase, so the
        result separates into a resistance and a reactance.
      </li>
      <li><strong>A shorting plug on IN R−.</strong> Everything else is in use.</li>
    </ul>

    <h2>Two loads, not an open circuit</h2>
    <p>
      The QuantAsylum tutorial's version of this test measures the second pass with the output
      <em>open</em>, which gives the cleanest arithmetic. Do not do that to a valve amplifier: an
      output transformer driven into an open secondary can arc over. The app therefore defaults to
      <strong>8 Ω then 4 Ω</strong>, and an open second load is an explicit checkbox you have to
      tick.
    </p>
    <p>
      The two loads must differ — equal loads make the denominator zero and the app refuses them.
      Note that the power target is defined into Load 1, so at the same generator level a lighter
      Load 2 draws more current: 1 W into 8 Ω becomes about 2 W into 4 Ω. Keep the level inside what
      the amplifier will deliver into the smaller load.
    </p>

    <h2>The pause</h2>
    <p>
      The run measures every point into Load 1, then goes silent and waits with a banner on screen.
      <strong>Swap the load — leave the amplifier on and everything else exactly as it is — and
      press CONTINUE.</strong> The second pass reuses the input range and the drive level from the
      first, which is deliberate: a range step between the passes would put about 0.1 dB of gain
      error into a measurement whose whole signal is 0.03 dB, and read as three times the real
      answer.
    </p>
    <p>
      So do not change anything else during the pause, and do not switch the amplifier off and on —
      a warm amplifier and a cold one have different output impedances.
    </p>

    <h2>Why the reference loop matters here</h2>
    <p>
      Without it the measurement has only magnitudes, and a reactive output impedance hides in
      those. An output coil of j0.19 Ω into 8 Ω moves the level by 0.03 dB — it reads as 0.03 Ω,
      a sixth of what it is. With the right channel as reference each gain carries its phase, the
      ratio is complex, and the coil comes back. For a valve amplifier, whose output impedance is an
      ohm or more and largely resistive, the magnitude-only method is usually good enough; for a
      solid-state amplifier at the top of the band it is not.
    </p>

    <p>
      What the app does with the two passes, and how to read the result, is on the
      <button type="button" class="manual-link" @click="showPage('output-impedance')">
        Output impedance and damping factor
      </button>
      page.
    </p>
  </div>
</template>
