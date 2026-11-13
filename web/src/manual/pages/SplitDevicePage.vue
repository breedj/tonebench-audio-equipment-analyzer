<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Two devices, one analyzer</h1>

    <p>
      <strong>Split (two devices)</strong> lets one device supply the generator and another the
      input. It is for two benches in particular: measuring a USB DAC by playing through it and
      capturing with the QA403, and driving a capture-only converter — an ADC with no output of its
      own — from the QA403's generator.
    </p>

    <p class="manual-callout">
      <strong>It is not a better analyzer than either device alone.</strong> It is a different one:
      it reaches a measurement neither can make by itself, and it costs you a second clock. What
      that costs, and what it does not, is the rest of this page.
    </p>

    <h2>Setting it up</h2>
    <p>
      The <strong>Analyzer</strong> dropdown in the status bar offers <strong>Split (two
      devices)</strong> beside the QA40x, the sound card and the simulator. Selecting it puts a
      <strong>cogwheel</strong> next to the dropdown that opens the split's own settings, where
      there are three choices:
    </p>
    <table>
      <thead>
        <tr><th>Control</th><th>What it is</th></tr>
      </thead>
      <tbody>
        <tr>
          <td>Generates</td>
          <td>The device the stimulus is played from. Its output range and its generator ceiling are the bench's.</td>
        </tr>
        <tr>
          <td>Captures</td>
          <td>
            The device the measurement is taken with. Its input range, its ranging, its drop
            counters and — if it has any — its telemetry are the bench's.
          </td>
        </tr>
        <tr>
          <td>Generator rate</td>
          <td>
            The rate the generating half runs at. <strong>Follow the capture rate</strong> makes a
            split behave exactly like a single device; a different rate is the subject of its own
            section below.
          </td>
        </tr>
      </tbody>
    </table>
    <p>
      <em>Which</em> card each half is, its channel pair and its calibration stay in
      <button type="button" class="manual-link" @click="showPage('sound-card')">the sound-card
      settings</button>. There is one card document, and a split simply uses one direction of it —
      so that dialog shows only the side this bench opens, and says which half the other device is.
      The unused side's stored settings are left alone, so switching back to the
      <strong>Sound card</strong> analyzer finds its own bench as it was.
    </p>

    <h3>Calibrating a capture-only converter</h3>
    <p>
      An ADC with no output of its own cannot be calibrated the usual way: the patch-cable step plays
      a known voltage out of the card and measures what comes back, and there is nothing to play out
      of. A split answers that, and it is the reason the pairing exists.
    </p>
    <p>
      With the <strong>QA40x generating</strong> and the card capturing, the patch-cable step uses the
      QA40x as its known source — so <strong>Full scale input</strong> can be measured with no output
      calibration at all. The QA40x's output level is a factory figure, which makes this the better of
      the two routes rather than the fallback: on an ordinary card bench the input figures inherit
      whatever error the output calibration carries, and here there is no such figure to inherit from.
      Patch both QA40x outputs to the card's two inputs and press
      <strong>Estimate peak input mV</strong>.
    </p>
    <p>
      The dialog narrows to match. A bench that plays through the card shows <strong>Full scale
      output</strong> and not the input step; one that captures with it shows the input step and not
      the output one, and does not complain about an output calibration it is not using.
    </p>

    <h3>Two pairings are refused</h3>
    <p>
      A <strong>sound card on both sides</strong> is refused, with a message. Everything PortAudio
      does runs on one thread, which a card's transfer loop occupies for as long as a stream is
      open, so the second card would never be serviced — and that bench already has a home: the
      <strong>Sound card</strong> analyzer opens both directions of a two-card pairing in one
      stream.
    </p>
    <p>
      A <strong>QA40x on both sides</strong> is refused too. That is one device opened twice, and a
      QA40x already generates and captures on its own clock.
    </p>

    <h2>Two devices are two clocks</h2>
    <p>
      Neither device is obliged to produce exactly the rate it was asked for, so what comes back is
      a slightly time-scaled copy of what was played. The difference is measured in parts per
      million, and it reaches different measurements very differently:
    </p>
    <table>
      <thead>
        <tr><th>Measurement</th><th>Affected?</th><th>Why</th></tr>
      </thead>
      <tbody>
        <tr>
          <td>Level, THD, THD+N, SNR, IMD</td>
          <td><strong>No</strong></td>
          <td>Every one of them finds its tone by searching for it. A 100 ppm error moves a 1 kHz tone by 0.1 Hz.</td>
        </tr>
        <tr>
          <td>Stepped sine response, THD vs frequency, THD vs amplitude</td>
          <td><strong>No</strong></td>
          <td>One tone at a time, same peak search.</td>
        </tr>
        <tr>
          <td>Chirp response — magnitude</td>
          <td>Barely</td>
          <td>A clock ratio is a time scaling, which costs a few ten-thousandths of a decibel.</td>
        </tr>
        <tr>
          <td>Chirp response — phase, group delay, phase delay</td>
          <td><strong>Yes</strong></td>
          <td>The error is a delay that grows with frequency, which reads as the DUT's own.</td>
        </tr>
        <tr>
          <td>Impulse response, CSD, burst decay</td>
          <td><strong>Yes</strong></td>
          <td>The impulse smears, which is what the gating and both decay surfaces are built on.</td>
        </tr>
      </tbody>
    </table>
    <p>
      There are two ways round the bottom half of that table, and the second is the better one.
    </p>
    <p>
      <strong>Measure the drift and correct for it.</strong> The split settings carry the same
      <strong>Clock drift</strong> panel the sound card's does — see
      <button type="button" class="manual-link" @click="showPage('clock-drift')">Clock drift</button>
      for what the figure means and how to read the verdict. The test measures through whatever is
      connected, because a DUT changes a tone's level and phase but not its frequency, so the bench
      needs no re-wiring. Once a figure is stored, the two chirp screens offer to take the clock's
      delay ramp off the measurement.
    </p>
    <p>
      <strong>The panel follows the two dropdowns</strong>, not the bench the app currently has
      open: turn Generates and Captures round and it reads the figure for <em>that</em> pairing, or
      says there is none. The two directions really are two pairings — a drift figure's sign is a
      property of which clock is the reference, so a reading taken one way round is not offered the
      other. <strong>Measure the drift</strong> is held until you press Save, because the test
      itself cannot follow: it measures the bench the app has open.
    </p>
    <p>
      <strong>Or use the right input as a reference</strong>, which escapes the problem rather than
      correcting it. Feed the generator's second channel straight back to the right input and tick
      <strong>Right channel is reference</strong>: the gain trace is then one captured channel
      divided by another from the <em>same</em> capture, so the clock ratio — and the converters'
      own filters, and their delay — cancel exactly. Measured on a QA403 fed by a USB DAC, both
      channels wired straight: the gain trace reads <strong>−0.03 dB flat with 0.11° of phase</strong>
      across 200 Hz–20 kHz, against 17° of phase on the same capture's left trace alone.
    </p>
    <p class="manual-callout">
      <strong>The catch is what that makes the measurement <em>of</em>.</strong> Dividing by the
      reference removes everything the two channels share, so it measures the DUT and nothing else —
      which is exactly right for an amplifier fed by the DAC, and no use at all when the DAC
      <em>is</em> the DUT. For that, measure the drift.
    </p>

    <h2>Running the two halves at different rates</h2>
    <p>
      The generator does not have to run at the rate you are capturing at, and three measurements
      want that:
    </p>
    <ul>
      <li>
        <strong>A DAC under test wants its own rate.</strong> The canonical measurement plays the
        content the DAC is built for and captures wideband, precisely to see the reconstruction
        images and the ultrasonic noise <em>above</em> the playback Nyquist.
      </li>
      <li>
        <strong>Ultrasonic residue needs a fast ADC and only a slow DAC.</strong> Class-D switching
        sits at 300–600 kHz; the stimulus for it is a 1 kHz tone any card can produce.
      </li>
      <li>
        <strong>It makes the QA403's 384 kHz input usable.</strong> That rate is capture-only — the
        QA403's own DAC produces garbage there — so a stimulus from somewhere else is the only route
        by which it becomes a real measurement.
      </li>
    </ul>
    <p>
      The two rates have to be a whole-number ratio, which every rate the app offers is. A pairing
      that is not would be refused, naming both rates: nothing periodic — a chirp, a noise sequence,
      a multitone comb — can line up with the analysis record otherwise.
    </p>

    <h3>What a slower generator costs</h3>
    <p>
      A sweep has to stop inside the generator's own Nyquist or it aliases at the top, so the
      stimulus reaches about <strong>0.4 of the generator's rate</strong> — 19.2 kHz on a 48 kHz
      DAC — and the analysed band stops with it rather than trailing off into a region nothing was
      played in. That is not a loss when the DAC is what is being tested; it is the measurement.
    </p>
    <p>
      A <em>faster</em> generator narrows nothing, and gains nothing either: whatever it does above
      the capture's Nyquist is invisible, and worse, aliases into the measurement. For that the
      answer is a faster ADC and nothing else.
    </p>

    <h2>What the status bar shows</h2>
    <p>
      The analyzer pill names both devices in signal order — <em>what plays → what measures</em> —
      and the serial and firmware rows name both. A QA40x in either half keeps its three sensor
      rows, because they are its own. The sound-card settings and the QA40x settings are both
      reachable while a split is selected, whichever half each belongs to.
    </p>

    <h2>What is not supported</h2>
    <ul>
      <li>
        <strong>A sound card on both sides</strong>, for the reason above. Use the
        <strong>Sound card</strong> analyzer, which handles that bench in one stream.
      </li>
      <li>
        <strong>44.1 kHz and its multiples.</strong> They cannot be periodic against the app's
        analysis record, which is a power of two — this is arithmetic rather than a limitation to
        engineer around. A 44.1 kHz DAC measurement is still ahead.
      </li>
      <li>
        <strong>Sound-card rates above 192 kHz.</strong> The card ladder is 48, 96 and 192 kHz; only
        a QA40x input reaches 384 kHz, and only with the experimental setting on.
      </li>
    </ul>
  </div>
</template>
