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
      <strong>cogwheel</strong> next to the dropdown that opens <strong>Split devices</strong> —
      everything this bench is made of, in one dialog. Its first section names the two devices:
    </p>
    <table>
      <thead>
        <tr><th>Control</th><th>What it is</th></tr>
      </thead>
      <tbody>
        <tr>
          <td>Input card</td>
          <td>
            The device the measurement is taken with. The list is the <strong>QA40x</strong> first,
            then every sound card that has an input, then the <strong>Simulator</strong> — so one
            pick chooses the half <em>and</em>, where it is a card, which card. Its input range, its
            ranging, its drop counters and — if it has any — its telemetry are the bench's.
          </td>
        </tr>
        <tr>
          <td>Output card</td>
          <td>
            The device the stimulus is played from, from the same kind of list. Its output range and
            its generator ceiling are the bench's.
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
      Underneath each dropdown are the rows that half needs and no others. Pick a card and you get
      its channel pair and, further down, its calibration step; pick the QA40x or the simulator and
      both disappear, because there is nothing there to choose or to calibrate. A half that is not a
      card <strong>leaves that direction's stored card alone</strong>, so switching back to the
      <button type="button" class="manual-link" @click="showPage('sound-card')">Sound card</button>
      analyzer finds its own bench as it was — there is one card document, and a split simply uses
      one direction of it.
    </p>
    <p class="manual-callout">
      <strong>There is no Save button.</strong> Every change applies itself, which is what the
      calibration steps need: they drive the device that is actually open. A pairing the app would
      refuse — a QA40x on both sides, or two cards with ASIO drivers — is simply not written, and
      says why in red instead.
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
    <h3>Calibrating a DAC's output without a voltmeter</h3>
    <p>
      The same trick runs the other way, and it is the one most people will meet: with the
      <strong>card generating</strong> and the QA40x capturing, the analyzer's own input is the
      known quantity, so <strong>Full scale output</strong> needs no meter. Pick
      <strong>Measured by → The QA40x's input</strong>, connect the card's output straight to the
      analyzer's input — both channels — and press <strong>Measure peak output</strong>. It plays
      the tone, reads it back and stores the figure in one press. There is no
      <strong>Generate sine</strong> button on this route, and that is deliberate rather than an
      omission: a tone started by hand is a measurement in progress as far as the app is concerned,
      so it would be refused by the very button it sits beside. <strong>Output level</strong> still
      chooses what gets played.
    </p>
    <p>
      Both channels are read and their <em>mean</em> is stored, because a card has one output full
      scale rather than one per channel; a pair that disagrees by more than a decibel is reported
      rather than averaged quietly, since on one interface that is a cable or a channel pair rather
      than the card. Put nothing in between: an attenuator is measured as if it were the card,
      unless it is declared as the external input gain, in which case it is divided back out and
      the figure is the card's own output either way.
    </p>
    <p class="manual-callout">
      <strong>Neither route exists on a plain sound-card bench</strong>, and that is the point of
      both. There the only other end of the cable is the same uncalibrated card, so one unknown
      would be measuring another — which is exactly what the voltmeter is for. A split is what puts
      a known device on the other half.
    </p>
    <p>
      The dialog narrows to match. A bench that plays through the card shows <strong>Full scale
      output</strong> and not the input step; one that captures with it shows the input step and not
      the output one, and does not complain about an output calibration it is not using.
    </p>

    <h3>A card on each half</h3>
    <p>
      <strong>A sound card on both sides is what this feature is for.</strong> Each card gets a
      stream of its own on a transfer thread of its own, so the two clocks never share a buffer —
      which is the whole difference from the way a two-card bench used to be opened, as a single
      duplex stream spanning both. That shape accumulated the difference between the two clocks
      until it ate the buffer, glitching on a schedule no buffer size could push out; two streams do
      not. It also means <strong>each card may run at its own sample rate</strong>, and that one
      card may be on ASIO while the other is on WASAPI.
    </p>
    <p>
      Both cards are chosen in <strong>Split devices</strong>, one per direction, in the same two
      dropdowns that choose the halves.
    </p>
    <p>
      <strong>And its own sample rates.</strong> Each card is asked on its own for the rates it
      accepts, so the dialog shows <em>Input rates</em> and <em>Output rates</em>
      separately — they are not intersected, because the analysis rate is the capturing card's and
      the generating card keeps a declared list of its own. Measured on this bench: a MOTU M Series
      under ASIO reports 44.1 to 192 kHz and a Topping D50 III under WASAPI 44.1 to 768 kHz, and
      both are usable at once.
    </p>
    <p>
      <strong>Each keeps its own best host API.</strong> On a one-interface bench the picker quietly
      moves one side to match the other, because a single stream cannot span two host APIs; on a
      two-card split it does not, because there are two streams. So a MOTU with an ASIO driver stays
      on ASIO while a DAC that has none sits on WASAPI — which is the point, since the better
      converter keeps the better driver. Measured on this bench: an ASIO capture against a WASAPI
      generator, and the reverse, both stream with nothing dropped.
    </p>
    <p class="manual-callout">
      <strong>If your bench used to be two cards under <em>Sound card</em>, it is a split now.</strong>
      Earlier versions let a two-card pairing be set up as the sound-card analyzer, which opened both
      directions in one stream across both cards — the arrangement whose clocks drift into one
      another. Such a bench is moved here once, automatically, the first time this version starts:
      the analyzer becomes <strong>Split (two devices)</strong> with a card on each half, and the two
      cards, their channel pairs, both calibrations and any measured clock drift come with it. A
      one-card bench is left exactly as it was.
    </p>

    <h3>Two pairings are refused</h3>
    <p>
      A <strong>QA40x on both sides</strong> is refused. That is one device opened twice, and a
      QA40x already generates and captures on its own clock.
    </p>
    <p>
      A <strong>split inside a split</strong> is refused for the obvious reason. Two
      <em>simulators</em> are deliberately allowed, which is how the two-device paths are exercised
      with no hardware at all.
    </p>
    <p>
      And <strong>at most one half may be an ASIO card</strong>. This is not a rate problem although
      it looks like one: the ASIO SDK has no notion of more than one driver open per process, and the
      audio library carries that as a single global driver, so the second card cannot be opened at
      any rate. Move one of them to <strong>WASAPI</strong> — always opened here in exclusive mode,
      so nothing is mixed or resampled — and leave the better converter on ASIO. What you give up is
      latency, which no measurement here depends on.
    </p>
    <p>
      With a card on both halves the two dropdowns offer each ASIO card under WASAPI as well, so
      that move is one pick rather than a hunt through <strong>Advanced</strong>. A pair that is
      still two ASIO cards is refused in red, naming both cards — worth knowing because every other
      reading on the dialog looks healthy: each card is probed on a stream of its own, and both
      answer.
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
      <strong>Measure the drift and correct for it.</strong> <strong>Split devices</strong> carries
      a <strong>Clock drift</strong> panel, below the calibration steps — see
      <button type="button" class="manual-link" @click="showPage('clock-drift')">Clock drift</button>
      for what the figure means and how to read the verdict. The test measures through whatever is
      connected, because a DUT changes a tone's level and phase but not its frequency, so the bench
      needs no re-wiring. Once a figure is stored, the two chirp screens offer to take the clock's
      delay ramp off the measurement.
    </p>
    <p>
      <strong>The panel follows the two dropdowns</strong>, not the bench the app currently has
      open: turn the two devices round and it reads the figure for <em>that</em> pairing, or says
      there is none. It follows a change of <em>card</em> too, because with a card on each half the
      figure is stored per card. The two directions really are two pairings — a drift figure's sign
      is a property of which clock is the reference, so a reading taken one way round is not offered
      the other. <strong>Measure the drift</strong> is held for the moment it takes a change to
      apply, because the test itself cannot follow: it measures the bench the app has open.
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
        by which it becomes a real measurement. A QA40x half of a split therefore offers 384 kHz
        <strong>without being asked</strong>: the opt-in a standalone QA403 has for it exists
        because that DAC is what a sweep would be played through, which on a split it is not.
      </li>
    </ul>
    <h3>The two rate families</h3>
    <p>
      Sample rates here come in two families, and a stimulus that repeats cannot cross between them.
      Within a family every pairing is a doubling, so the two sides always line up:
    </p>
    <table>
      <thead>
        <tr><th>Family</th><th>Rates</th><th>Typically</th></tr>
      </thead>
      <tbody>
        <tr>
          <td>48 kHz</td>
          <td>48, 96, 192, 384, 768 kHz</td>
          <td>Every analyzer this app drives, and most interfaces</td>
        </tr>
        <tr>
          <td>44.1 kHz</td>
          <td>44.1, 88.2, 176.4 kHz</td>
          <td>A DAC playing CD-rate material — the rate nearly all recorded music is</td>
        </tr>
      </tbody>
    </table>
    <p>
      Pair <em>across</em> the families — a 44.1 kHz DAC generating into a QA403 capturing at
      192 kHz, which is the commonest reason to want this — and the ratio is 147:640, never a whole
      number at any record length. So anything that has to repeat exactly once per record is
      refused, and the app says so before you press Run rather than after:
    </p>
    <table class="manual-wrap">
      <thead>
        <tr><th>Still works</th><th>Refused across the families</th></tr>
      </thead>
      <tbody>
        <tr>
          <td>
            Tones, so THD, THD+N, SNR and linearity; IMD; the multitone comb; square and triangle
            waves; and <strong>every stepped-sine sweep</strong> — frequency response, crosstalk,
            output impedance and stepped impedance
          </td>
          <td>
            The chirp and periodic noise, and so the frequency-response, impulse-response and
            broadband impedance screens
          </td>
        </tr>
      </tbody>
    </table>
    <p>
      That is most of what a converter is actually judged by, and frequency response has a working
      alternative: measure it with <strong>Stepped sine</strong>, which plays one tone at a time and
      does not care about the ratio. It is slower and it is correct.
    </p>
    <p>
      The limit is arithmetic rather than something waiting to be fixed. The chirp's period has to be
      a whole number of frames at <em>both</em> rates at once, and 147:640 never is, whatever record
      length you choose.
    </p>
    <p>
      Note the 44.1 kHz family is offered as a <strong>generator</strong> rate, and on the FFT screen
      as a capture rate, but not as a frequency <em>span</em>: the response screens derive their
      transform length from the span, and only the 48 kHz family gives a usable one. That costs
      nothing in practice — capturing a 44.1 kHz DAC at 192 kHz is the better measurement anyway,
      being the only one that sees the reconstruction images above 22 kHz.
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
    <p>
      <strong>Gen rate</strong> appears beside the Split devices button whenever the generator is
      pinned to a rate of its own, so the figure is on screen once the dialog is closed. It is the
      <em>generating</em> half's rate and nothing else: the capture rate belongs to the measurement
      — the FFT screen has its own <strong>Sample rate</strong>, the response screens derive theirs
      from the span — which is why the bar does not quote one. With the generator set to
      <em>Follow the capture rate</em> there is one clock and one number, so nothing is shown.
    </p>

    <h2>What is not supported</h2>
    <ul>
      <li>
        <strong>Two ASIO cards</strong>, for the reason above — one driver per process. Put one of
        them on WASAPI.
      </li>
      <li>
        <strong>A chirp or noise run across the two rate families</strong> — see
        <em>The two rate families</em> above. 44.1 kHz and its multiples are offered and a DAC can
        be driven at them, but paired with a 48 kHz family capture only the tone-based measurements
        and the stepped sine run. That is arithmetic rather than a limitation to engineer around.
      </li>
      <li>
        <strong>44.1 kHz as a frequency span.</strong> The response screens derive their transform
        length from the span and only the 48 kHz family gives a usable one, so there is no 22.05 kHz
        span. Nothing wants one: a 44.1 kHz DAC is the <em>generating</em> half, captured wideband.
      </li>
      <li>
        <strong>Nothing else about the rates.</strong> A card is asked about 44.1, 48, 88.2, 96,
        176.4, 192, 384 and 768 kHz and offers whichever its driver accepts, so a fast DAC can be
        driven at its own rate; a QA40x reaches 384 kHz for capture, always on a split and
        otherwise only with the experimental setting on. Verified on this bench: a Topping D50 III accepts the 48 kHz family's five in
        exclusive mode, and a split with it generating at 384 kHz into a QA403 capturing at 384 kHz
        runs with no dropped chunks or blocks.
      </li>
    </ul>
  </div>
</template>
