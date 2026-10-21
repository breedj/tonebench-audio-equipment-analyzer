<script setup lang="ts">
import SettingsTable from '../../components/SettingsTable.vue'
import WiringDiagram from '../../components/WiringDiagram.vue'
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Wiring: the reference channel</h1>

    <p>
      One extra cable: <strong>left output to right input</strong>, so the right input carries the
      signal that is actually driving the DUT and the left input is measured <em>relative to it</em>.
      This is the wiring for
      <button type="button" class="manual-link" @click="showPage('frequency-response')">
        Frequency response
      </button>
      and
      <button type="button" class="manual-link" @click="showPage('stepped-response')">
        Stepped sine response
      </button>
      with <strong>Right channel is reference</strong> ticked — the setting that turns a level curve
      into a transfer function and gives the phase a meaning.
    </p>
    <p>
      Everything else is the ordinary
      <button type="button" class="manual-link" @click="showPage('wiring-line-level')">
        line-level
      </button>
      or
      <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
        power amplifier
      </button>
      arrangement. The reference is an addition to it, not a different wiring.
    </p>

    <h2>Why a second input</h2>
    <p>
      Without a reference, the left input's response is measured against the sweep the app
      <em>generated</em> — the numbers it asked the DAC for. Everything between that number and the
      DUT's input is then part of the answer: the output stage's own response, the output
      attenuator, the cable, and whatever the DUT's input impedance does to the source driving it.
      None of that is the DUT.
    </p>
    <p>
      With a reference the measurement is a <strong>ratio of two inputs taken from the same
      capture</strong>, and all of it divides out. Three things follow that you cannot have any
      other way:
    </p>
    <ul>
      <li>
        <strong>Gain as a real transfer function.</strong> The <em>Gain</em> trace exists only in
        this mode. Without the reference what you get is the left channel's own output level,
        shifted by the drive level the app commanded.
      </li>
      <li>
        <strong>Phase that is the DUT's.</strong> With the reference it is
        <code>arg(L/R)</code> — a phase difference between two measured signals. Without it, a
        chirp's phase is excess phase against the synthesised sweep, and a stepped sine has no
        phase at all: one tone has no capture trigger, so a single channel's absolute phase is
        wherever the record happened to land.
      </li>
      <li>
        <strong>Everything built on phase.</strong> Group delay, phase delay, and the Nyquist,
        Nichols, pole–zero and |H(s)| views meaning what they claim rather than describing the
        analyzer's own path alongside the DUT's.
      </li>
    </ul>

    <h2>The wiring</h2>

    <WiringDiagram
      :terminate="['in-l-', 'in-r-']"
      :height="390"
      alt="Output L plus goes through a T-piece at the device's input: one leg on to the device, one leg across to Input R plus as the reference. The device's output returns to Input L plus. Shorting plugs on both minus inputs, and Output R left empty."
      caption="Left out to right in. The T-piece belongs at the DUT's input, not at the panel."
    >
      <template #default="{ cable }">
        <rect x="200" y="285" width="290" height="60" rx="4" class="w-block-fill" />
        <text x="345" y="313" text-anchor="middle" class="w-label">Device under test</text>
        <text x="345" y="330" text-anchor="middle" class="w-note">preamp, filter, power amp…</text>

        <path class="w-cable w-drive" :d="cable('out-l+', 285, [242, 285])" />
        <path class="w-cable w-measure" :d="cable('in-l+', 285, [445, 285])" />
        <path class="w-cable w-reference" :d="cable('in-r+', 258, [242, 258])" />
        <circle cx="242" cy="258" r="3.8" class="w-node" />
        <circle cx="242" cy="285" r="3" class="w-node" />
        <circle cx="445" cy="285" r="3" class="w-node" />

        <text x="250" y="208" class="w-label w-drive">Drive</text>
        <text x="453" y="208" class="w-label w-measure">Measure</text>
        <text x="258" y="250" class="w-label w-reference">Reference — the T-piece</text>
        <text x="345" y="376" text-anchor="middle" class="w-note">
          One node: the left output, the DUT's input and the right input are the same point.
        </text>
      </template>
    </WiringDiagram>

    <ul>
      <li>
        <strong>OUT L+ through a BNC T-piece at the DUT's input</strong>: one leg on to the DUT, one
        leg across to <strong>IN R+</strong>.
      </li>
      <li><strong>DUT output</strong> to <strong>IN L+</strong>, as on any other measurement.</li>
      <li>
        <strong>Shorting plugs on IN L− and IN R−.</strong> Both inputs are read single-ended, so
        only their + connectors carry a cable — and the reference channel's noise floor rises about
        33 dB with an open − leg exactly like the measuring channel's.
      </li>
      <li>
        <strong>OUT R+ stays empty.</strong> Never plug an output. The right output does play (see
        below) and here it plays into nothing, which is harmless.
      </li>
      <li>
        <strong>The T-piece goes at the DUT's end of the cable.</strong> That is the whole point of
        this wiring: the reference is taken at the node the DUT is actually driven from, so the
        drive cable and the DUT's own input loading are inside what divides out.
      </li>
    </ul>

    <h2>Turning it on</h2>

    <SettingsTable
      :headings="['Screen', 'What to set']"
      :rows="[
        [
          'Frequency response',
          'Right channel is reference. Nothing else: the chirp is played on both output channels whatever you do, so the right input is fed the identical stimulus by either wiring.',
        ],
        [
          'Stepped sine response',
          'Right channel is reference, which locks Generator channels ▸ Right on. The app assumes the reference is fed from the right output, and will not let you ask for a reference with no stimulus on it.',
        ],
        [
          'Impulse response',
          'Reference set to Right input channel — the same wiring, used the same way.',
        ],
      ]"
    />

    <div class="manual-note">
      <p>
        <strong>The Stepped sine screen locks the right generator on, and on this wiring that output
        goes nowhere.</strong> That is not a conflict: the check exists so you cannot divide the
        left channel by an input that is sitting on nothing, and it can only see whether the
        <em>generator</em> is voiced. The right <em>input</em> is fed from the left output through
        the T-piece, which is what actually matters, and a right output playing into an unconnected
        socket costs nothing.
      </p>
    </div>

    <h2>The alternative: right output to right input</h2>
    <p>
      Both output channels play the same stimulus on both of these measurements, so looping
      <strong>OUT R+</strong> straight to <strong>IN R+</strong> also gives the right input the
      signal it needs — with no T-piece and no stub.
    </p>

    <WiringDiagram
      :terminate="['in-l-', 'in-r-']"
      :height="410"
      alt="Output L plus drives the device and its output returns to Input L plus; separately, Output R plus is looped straight to Input R plus as the reference. Shorting plugs on both minus inputs."
      caption="The loop: no T-piece, and it cancels slightly less. This is what the output impedance measurement uses."
    >
      <template #default="{ cable, link }">
        <rect x="200" y="305" width="290" height="60" rx="4" class="w-block-fill" />
        <text x="345" y="333" text-anchor="middle" class="w-label">Device under test</text>
        <text x="345" y="350" text-anchor="middle" class="w-note">preamp, filter, power amp…</text>

        <path class="w-cable w-reference" :d="link('out-r+', 'in-r+', 228)" />
        <path class="w-cable w-drive" :d="cable('out-l+', 305, [242, 305])" />
        <path class="w-cable w-measure" :d="cable('in-l+', 305, [445, 305])" />
        <circle cx="242" cy="305" r="3" class="w-node" />
        <circle cx="445" cy="305" r="3" class="w-node" />

        <text x="250" y="208" class="w-label w-drive">Drive</text>
        <text x="500" y="222" class="w-label w-reference">Reference</text>
        <text x="453" y="265" class="w-label w-measure">Measure</text>
        <text x="345" y="396" text-anchor="middle" class="w-note">
          The crossing at the right is not a junction — there is no dot on it.
        </text>
      </template>
    </WiringDiagram>

    <p>
      It is what the Stepped sine screen's own tooltip describes, and what
      <button type="button" class="manual-link" @click="showPage('wiring-output-impedance')">
        output impedance
      </button>
      uses — that measurement has both channels playing anyway, and its two passes differ by a few
      hundredths of a decibel, so a spare cable is worth more than a T-piece there.
    </p>
    <p>
      <strong>It cancels less.</strong> The reference then travels through the <em>right</em> output
      stage and the right input rather than through the cable that actually drives the DUT, so the
      difference between the two output channels stays in the answer, and so does anything the DUT's
      input does to the source. Use the T-piece where you can. Use the loop where a T-piece would
      mean a long stub, where the DUT's input is already a loop-through, or where you simply have a
      cable and no T.
    </p>

    <h2>One input range serves both inputs</h2>
    <p>
      The analyzer has a single input range, and the app sets it from whichever of the two channels
      shows the larger peak. With no reference that is academic — only one input carries anything.
      With a reference the two inputs are carrying <em>different</em> levels, and the quiet one gets
      whatever range the loud one chose:
    </p>
    <ul>
      <li>
        <strong>A DUT with gain.</strong> The range is set by the DUT's output, and the reference
        sits that much further down it. A preamplifier with 30 dB of gain leaves the reference 30 dB
        into the range.
      </li>
      <li>
        <strong>A DUT that attenuates</strong> — a passive filter, an L-pad, a crossover network, an
        amplifier measured through a
        <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
          divider
        </button>
        — and it is the other way about: the range is set by the drive, and the DUT's output is the
        channel running close to the floor.
      </li>
    </ul>
    <p>
      Thirty or forty decibels either way is comfortable. Past that the quiet channel starts
      measuring the analyzer's own noise instead of a signal, and the symptom is the
      <em>Gain</em> trace's coherence falling — at the band edges first, where the DUT is quietest
      anyway. The fix is to attenuate the <em>loud</em> side with a divider and enter that
      attenuation as <strong>Ext. gain</strong> for that input, which is per input for exactly this
      reason; lowering the drive moves both channels together and buys nothing.
    </p>

    <h2>What the reference does not cancel</h2>
    <p>
      A reference divides out everything <em>ahead</em> of the two taps and nothing behind them —
      so the analyzer's two input channels are still two different channels, and their difference is
      the floor under every measurement made this way. On this bench it is a small floor: a loopback
      chirp with the reference on reads flat to about <strong>0.01 dB</strong>, which is that
      mismatch measured directly, since both inputs sit behind the same converters and the same
      analog filters. See
      <button type="button" class="manual-link" @click="showPage('analyzer-specs')">
        the analyzer's own figures
      </button>.
    </p>
    <p>
      That is also the standing sanity check for this wiring: run the measurement with the DUT out
      of the path — a
      <button type="button" class="manual-link" @click="showPage('wiring-loopback')">loopback</button>
      with both cables fitted — and the Gain trace <em>is</em> the two input channels' own
      mismatch. Anything you see there will be in your next result too. For an impulse
      measurement's band edges, a stored loopback calibration is the proper tool rather than a
      reference channel.
    </p>

    <h2>Reading the result</h2>
    <ul>
      <li>
        <strong>The Right channel checkbox is not offered in this mode.</strong> What the right
        input's dBV trace would show is the drive, not a measurement of anything, so the Display
        panel hides it and the curve to read is <em>Gain</em>.
      </li>
      <li>
        <strong>“Removed N sample delay” appears only with a measured reference</strong>, and only
        then is it a delay. The chirp is periodic and there is no trigger, so a record starts at an
        arbitrary point in the sweep; measured against the synthesised sweep, that rotation is
        simply where the capture landed and means nothing. With the reference, both spectra come
        from one capture, the rotation is common to them and cancels, and what is left is the DUT's
        own propagation delay.
      </li>
      <li>
        <strong>The delay is restored on the Gain trace and not on the Left one.</strong> Left is
        excess phase against the synthesised sweep; Gain carries the physical delay, which is why
        group delay and phase delay are read off it.
      </li>
      <li>
        <strong>Coherence is the reference's own health check.</strong> The Gain trace has a
        coherence of its own, because left is estimated against right directly rather than by
        dividing two separate results. Near 1.0 across the band means the two inputs agree; a band
        where it collapses is a band where one of them has nothing in it.
      </li>
    </ul>

    <h2>Common mistakes</h2>
    <ul>
      <li>
        <strong>Reference ticked with nothing on IN R+.</strong> The left channel is then divided by
        whatever noise that input is sitting on, which produces a full, plausible-looking curve of
        nothing, with coherence on the floor. The app refuses the one case it can see — a reference
        with the right generator switched off — but it cannot see a missing cable.
      </li>
      <li>
        <strong>The T-piece at the panel instead of at the DUT.</strong> A long stub on to the DUT
        then sits outside what divides out, so the measurement includes the cable — the one thing
        this wiring exists to remove.
      </li>
      <li>
        <strong>A missing shorting plug on IN R−.</strong> It is easy to fit the plugs for a
        one-channel measurement and forget that the reference has made the right input a working
        input. The floor rises about 33 dB and the coherence goes with it.
      </li>
      <li>
        <strong>A divider on one side only, with Ext. gain set for both inputs or neither.</strong>
        With a reference the two inputs are no longer symmetrical, and the app cannot guess which
        one the divider is on.
      </li>
      <li>
        <strong>Expecting the reference to fix a level problem.</strong> It divides out the source,
        not the range: a DUT whose output is 40 dB from the drive still has one channel near the
        analyzer's floor whichever way the ratio is taken.
      </li>
    </ul>
  </div>
</template>
