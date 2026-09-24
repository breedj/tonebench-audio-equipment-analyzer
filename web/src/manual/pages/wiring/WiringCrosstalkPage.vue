<script setup lang="ts">
import WiringDiagram from '../../components/WiringDiagram.vue'
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Wiring: crosstalk</h1>

    <p>
      One channel is driven — the <em>aggressor</em> — and the other is fed nothing and measured for
      whatever leaks into it — the <em>victim</em>. Both channels of the analyzer are in use at once,
      both amplifier outputs are loaded, and the victim's input is shorted, because an open input
      picks the aggressor out of the air and reads as crosstalk the amplifier does not have.
    </p>

    <WiringDiagram
      :terminate="['in-l-', 'in-r-']"
      :height="430"
      alt="Output L plus drives the amplifier's left input; the left output returns to Input L plus and the right output to Input R plus. The right input is shorted to ground and both outputs are loaded."
      caption="Left as aggressor. Straight through: OUT L with IN L, OUT R with IN R."
    >
      <template #default="{ cable, path }">
        <rect x="270" y="250" width="160" height="80" rx="4" class="w-block-fill" />
        <path class="w-thin" :d="path([[270, 290], [430, 290]])" />
        <text x="350" y="276" text-anchor="middle" class="w-label">Left — aggressor</text>
        <text x="350" y="316" text-anchor="middle" class="w-label">Right — victim</text>

        <!-- Drive into the aggressor's input -->
        <path class="w-cable w-drive" :d="cable('out-l+', 270, [270, 270], { arrive: 'side' })" />
        <text x="250" y="212" class="w-label w-drive">Drive</text>

        <!-- The victim's input, shorted -->
        <path class="w-thin" :d="path([[270, 310], [230, 310], [230, 350]])" />
        <path class="w-thin" :d="path([[220, 350], [240, 350]])" />
        <path class="w-thin" :d="path([[224, 355], [236, 355]])" />
        <path class="w-thin" :d="path([[227, 360], [233, 360]])" />
        <text x="200" y="378" class="w-note">victim's input shorted</text>

        <!-- Both outputs, both loaded -->
        <path class="w-thin" :d="path([[430, 270], [610, 270], [610, 300]])" />
        <rect x="598" y="300" width="24" height="26" class="w-block" />
        <path class="w-thin" :d="path([[610, 326], [610, 392]])" />
        <path class="w-thin" :d="path([[430, 310], [560, 310], [560, 340]])" />
        <rect x="548" y="340" width="24" height="26" class="w-block" />
        <path class="w-thin" :d="path([[560, 366], [560, 392]])" />
        <text x="592" y="317" text-anchor="end" class="w-note">8 Ω</text>
        <text x="542" y="357" text-anchor="end" class="w-note">8 Ω</text>
        <path class="w-thin" :d="path([[560, 392], [610, 392]])" />
        <path class="w-thin" :d="path([[585, 392], [585, 399]])" />
        <path class="w-thin" :d="path([[575, 399], [595, 399]])" />
        <path class="w-thin" :d="path([[579, 404], [591, 404]])" />

        <!-- Both outputs measured -->
        <path class="w-cable w-measure" :d="cable('in-l+', 270, [445, 270])" />
        <path class="w-cable w-measure" :d="cable('in-r+', 310, [530, 310], { exitDx: -25 })" />
        <circle cx="445" cy="270" r="3.2" class="w-node" />
        <circle cx="530" cy="310" r="3.2" class="w-node" />

        <text x="320" y="418" text-anchor="middle" class="w-note">
          Load both outputs at the rated impedance — 8 Ω here.
        </text>
      </template>
    </WiringDiagram>

    <ul>
      <li>
        <strong>OUT L+ to the amplifier's left input</strong> for a left-to-right measurement. Only
        the driven generator channel plays; the other output stays silent.
      </li>
      <li>
        <strong>Short the victim's input.</strong> A shorting plug at the amplifier's right input,
        not at the analyzer — it is the amplifier's input that must see a low source impedance. This
        is the condition every published crosstalk figure assumes.
      </li>
      <li>
        <strong>Both amplifier outputs to the matching analyzer inputs</strong>: left output to
        IN L+, right output to IN R+. Both are read from one capture, and the point is the
        difference between them.
      </li>
      <li>
        <strong>Load both outputs</strong>, not just the driven one. An unloaded output behaves
        differently, and on a valve amplifier an unloaded output is a hazard.
      </li>
      <li><strong>Shorting plugs on IN L− and IN R−.</strong></li>
    </ul>

    <h2>This app does not cross the return cables</h2>
    <p>
      <strong>The measurement pairs each generator output with the input of the same name.</strong>
      <em>Left → right</em> drives OUT L, reads IN L as the aggressor and IN R as the victim.
      Amplifier left output to analyzer left input, right to right — straight through, exactly as
      drawn.
    </p>
    <p>
      That is worth stating because the QuantAsylum tutorial's own crosstalk procedure crosses them:
      its automated test wants the amplifier's left output on the analyzer's <em>right</em> input.
      Follow the tutorial's cabling here and you will get a plausible-looking curve of the wrong
      thing.
    </p>

    <h3>To measure the other direction</h3>
    <p>
      <strong>Move the drive cable to OUT R+</strong> and move the shorting plug to the amplifier's
      left input. Leave the two return cables where they are and select
      <em>Right → left</em> — or pick <strong>Both</strong>, which runs the two passes back to back
      once the cabling is right for each.
    </p>
    <p>
      The tempting mistake, with one BNC-to-RCA cable on the bench, is to move that cable to the
      other amplifier input and leave the BNC on OUT L. Then <em>Right → left</em> drives an output
      with nothing on it, and <em>Left → right</em> calls the victim the aggressor and reports
      <em>positive</em> crosstalk. Move the BNC, not the direction setting.
    </p>
    <p>
      A quick way to confirm which analyzer input is on what before driving anything: with the
      generator off, an amplifier output carries mains hum tens of decibels above the analyzer's own
      floor, while a loopback to a silent output sits right down at that floor.
    </p>

    <h2>Levels and the floor</h2>
    <p>
      One input range serves both inputs and it is set from the <em>aggressor</em>, so the victim
      sits far down that range and the reading's floor is the analyzer's own at that setting. At
      −20 dBV of drive with 5 dB of headroom, about −110 dB is the best a loopback can show; on this
      bench a
      <button type="button" class="manual-link" @click="showPage('wiring-loopback')">loopback</button>
      run measured −100 dB at 20 kHz, which is the real floor under any amplifier result.
    </p>
    <p>
      Drive at about the power the figure is quoted at — 1 W into 8 Ω is the usual choice, and it is
      not critical. At the bottom of the band the victim's <em>own</em> hum often sets the floor
      rather than the coupling: on a real amplifier whose right channel hummed 13 dB harder than its
      left, the two directions agreed within 1.5 dB everywhere except 19 Hz, where the noisier
      channel as victim read 12 dB better than it should have.
    </p>

    <p>
      What the sweep does with all this is on the
      <button type="button" class="manual-link" @click="showPage('crosstalk')">Crosstalk</button>
      page.
    </p>
  </div>
</template>
