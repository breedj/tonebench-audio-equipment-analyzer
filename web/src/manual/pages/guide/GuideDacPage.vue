<script setup lang="ts">
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Measuring a USB DAC</h1>

    <p>
      To measure a DAC you have to play <em>through</em> it and listen with something else. The app
      can do that: one device makes the sound, another measures it. The usual pairing is a USB DAC
      playing and a QA403 listening, but it works the other way round too — a QA403 playing into a
      capture-only converter.
    </p>

    <p class="manual-callout">
      <strong>This is not an upgrade.</strong> It is a different measurement: one the QA403 cannot
      make on its own, because it cannot both be the DAC and measure the DAC.
    </p>

    <h2>What to plug in</h2>
    <p>
      Connect the DAC's left output to the analyzer's left input, and its right output to the right
      input. Both channels, even if you only care about one — the second one earns its keep further
      down this page.
    </p>

    <h2>Switching to it</h2>
    <p>
      At the bottom of the window, change the <strong>Analyzer</strong> dropdown to
      <strong>Split (two devices)</strong>. A <strong>cogwheel</strong> appears next to it. Open it
      and set:
    </p>
    <ul>
      <li><strong>Generates</strong> — Sound card. That is the DAC being tested.</li>
      <li><strong>Captures</strong> — QA40x. That is the analyzer.</li>
      <li><strong>Generator rate</strong> — leave it on <em>Follow the capture rate</em> for now.</li>
    </ul>
    <p>
      Press <strong>Save</strong>. Which DAC it is still has to be chosen in the sound-card
      settings, the same place a sound-card bench picks its card —
      <button type="button" class="manual-link" @click="showPage('guide-sound-card')">Using a sound
      card instead</button> covers that, and you only need the <em>output</em> side of it.
    </p>

    <h2>What works straight away</h2>
    <p>
      Play a 1 kHz tone and look at the FFT screen. Level, THD, THD+N, SNR and the two-tone IMD
      figures are all trustworthy, and so are the stepped sweeps. If you only want to know how clean
      a DAC is, you are done — that is what those numbers are.
    </p>

    <h2>The one thing to know</h2>
    <p>
      Two devices have two crystals, and neither runs at exactly the rate it claims. The difference
      is tiny, and for tone measurements it does not matter at all. But a <strong>chirp</strong>
      sweep measures time as well as level, and there a tiny rate difference looks exactly like a
      delay in the thing you are measuring.
    </p>
    <p>
      So if you want the frequency response, the phase, or an impulse response, do one of these two:
    </p>
    <ul>
      <li>
        <strong>Measure the drift once.</strong> In the split settings there is a <strong>Clock
        drift</strong> panel with a button. It takes about a minute, needs no re-plugging, and
        afterwards the chirp screens offer to correct for it. See
        <button type="button" class="manual-link" @click="showPage('clock-drift')">Clock
        drift</button>.
      </li>
      <li>
        <strong>Or use the right channel as a reference</strong> — this is why both channels are
        plugged in. Put the thing you are measuring on the left, feed the DAC's right output
        straight to the right input, and tick <strong>Right channel is reference</strong>. The app
        then compares the two channels against each other, and anything they have in common — the
        clocks, the converters, the cable — drops out.
      </li>
    </ul>
    <p class="manual-callout">
      <strong>The second trick only works for what comes after the DAC.</strong> If the DAC itself
      is what you are measuring, there is nothing to compare it against, so measure the drift
      instead.
    </p>

    <h2>Playing at one rate and listening at another</h2>
    <p>
      There is a <strong>Generator rate</strong> setting in the split settings, and it is worth
      knowing what it is for. A DAC is usually most interesting when it is fed the rate it was built
      for while you listen much wider — that way the extra noise and the images a DAC puts
      <em>above</em> the music are in the picture instead of off the edge of it.
    </p>
    <p>
      Set the generator to 48 kHz and capture at 96 or 192, and the sweep will stop a little below
      20 kHz. That is not the app giving up: a 48 kHz DAC cannot make a clean tone above about
      that, so there is nothing up there to measure except the DAC's own limit.
    </p>
  </div>
</template>
