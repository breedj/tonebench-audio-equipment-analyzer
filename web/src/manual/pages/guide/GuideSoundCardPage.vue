<script setup lang="ts">
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Using a sound card instead</h1>

    <p>
      You do not need a QA403 to use this app. A sound card can be the analyzer — its output plays
      the test signal, its input listens. It will not be as good as a proper analyzer, but a decent
      audio interface measures a great deal, and it is what a lot of people already have on the
      bench.
    </p>

    <p>
      There is one extra job first. A QA403 knows what a volt is; a sound card does not. Out of the
      box it can only say "this is half as loud as the loudest thing I can handle". Teaching it what
      a volt is takes about five minutes and a multimeter, and this page walks through it.
    </p>

    <h2>Switching to it</h2>
    <p>
      At the bottom of the window there is a dropdown marked <strong>Analyzer</strong>. Change it
      from <strong>QA403</strong> to <strong>Sound card</strong>. A little
      <strong>cogwheel</strong> appears next to it — that opens the sound-card settings, and
      everything below happens in there.
    </p>

    <h2>Step 1: choose the card</h2>
    <p>
      Open the cogwheel. You will see a list of <strong>Input</strong> devices and a list of
      <strong>Output</strong> devices. The same card usually appears several times, once per
      "driver" — that is normal.
    </p>
    <p class="manual-callout">
      <strong>Pick the ASIO entry if there is one.</strong> It is listed first, and it is the only
      kind that lets the app play and record properly at the same time. If your card came with a
      driver disc or download from the manufacturer, ASIO is what it installed. No ASIO entry
      usually means that driver is not installed.
    </p>
    <p>
      Pick the same card for input and output. They have to be one physical device: if the app plays
      through one card and records through another, the two run at very slightly different speeds and
      the measurement slowly goes wrong — and it will not look wrong, it will look like a fault in
      whatever you are testing. The app warns you if the two do not look like the same card.
    </p>
    <p>
      Then choose a <strong>Sample rate</strong>. Only rates your card can really do are offered.
      48 kHz is fine for almost everything.
    </p>
    <p>Press <strong>Use this card</strong>.</p>

    <h2>Step 2: teach it what a volt is — the output</h2>
    <p>You need a multimeter that can measure AC volts. Almost any will do.</p>
    <ol>
      <li>
        Connect the multimeter across the card's <strong>output</strong> — the same socket you will
        later connect your amplifier to. Set it to AC volts, on a low range.
      </li>
      <li>Leave <strong>Output level</strong> at <strong>−6 dB</strong>.</li>
      <li>
        Press <strong>Generate sine (400 Hz)</strong>. The card starts playing a steady tone and
        keeps playing it. You may hear it, if speakers are connected.
      </li>
      <li>
        Read the multimeter. Type that number into <strong>Voltmeter reads</strong>. If your meter
        shows volts rather than millivolts, multiply by 1000 — 0.75 V is 750.
      </li>
      <li>
        Leave the unit on <strong>mV rms</strong>. That is what a multimeter shows. Only change it
        to <strong>mV peak</strong> if you are reading a value off an oscilloscope instead.
      </li>
      <li>
        Press <strong>Estimate peak output mV</strong>. The tone stops and the number is saved.
      </li>
    </ol>
    <p>The app now knows exactly how loud its output is in volts.</p>

    <h2>Step 3: teach it what a volt is — the inputs</h2>
    <p>
      Now the other direction. The easiest way uses a single cable, because the app can already
      produce a known voltage after step 2.
    </p>
    <ol>
      <li>
        Take a cable from the card's <strong>left output</strong> to its <strong>left
        input</strong>. Nothing else in between.
      </li>
      <li>
        Set <strong>Known source</strong> to <strong>This card's output, through a patch
        cable</strong>.
      </li>
      <li>Leave <strong>Input channel</strong> on <strong>Left</strong>, and <strong>Play at</strong> on <strong>−20 dB</strong>.</li>
      <li>Press <strong>Estimate peak input mV</strong>. It plays, listens and saves, all at once.</li>
      <li>
        Move the cable to the <strong>right</strong> output and the <strong>right</strong> input,
        change <strong>Input channel</strong> to <strong>Right</strong>, and press it again.
      </li>
    </ol>
    <p>
      The table fills in as you go. When both rows have numbers, the card is calibrated and the
      orange <strong>Uncalibrated</strong> label at the bottom of the window disappears.
    </p>
    <p class="manual-callout">
      If the two numbers are wildly different, something is wrong with the cable or you calibrated
      the same channel twice. The app tells you when they differ by more than a few dB.
    </p>
    <p>
      If you happen to own a signal generator you can use that instead — set
      <strong>Known source</strong> to <strong>A generator of known voltage</strong>, feed it into
      the input, and type in its voltage. It is slightly more trustworthy, because it does not depend
      on step 2 being right.
    </p>

    <h2>Step 4: tell it about anything in front of the inputs</h2>
    <p>
      If you have built a divider or an attenuator between your amplifier and the card — and for
      anything louder than a line signal you should — tell the app about it, or every voltage it
      reports will be wrong by exactly that much.
    </p>
    <p>
      At the bottom right of the window there are two <strong>Gain</strong> boxes, one per channel,
      and a unit next to them. A 10-to-1 divider is <strong>0.1</strong> with the unit on
      <strong>×</strong>, or <strong>−20</strong> with it on <strong>dB</strong>. Both mean the same
      thing; use whichever you think in. Leave them at 1 (or 0 dB) if the card is connected directly.
    </p>

    <h2>Two things to know</h2>
    <p>
      <strong>Do not go too low in frequency.</strong> Sound cards cannot produce or hear very low
      notes — below about 20 Hz they quietly fade out. The app knows this and will not sweep lower
      than the card can manage; it tells you when it has raised the starting point. If it did not,
      the card's own fading would show up on the chart and look like a fault in your amplifier.
    </p>
    <p>
      <strong>Watch the level.</strong> A QA403 has switchable input ranges and adjusts itself. A
      sound card does not: it has one range, and if you feed it too much it clips. If a measurement
      looks strangely distorted, turn the source down.
    </p>

    <p class="manual-see-also">
      Next:
      <a href="#" @click.prevent="showPage('guide-first-measurement')">your first measurement</a>.
    </p>
  </div>
</template>
