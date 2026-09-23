<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Using the simulator</h1>

    <p>
      The simulator is a second analyzer built into the app: a QA403 that does not exist, with a
      generator, two inputs and a device under test already patched between them. Everything a real
      QA403 does — ranges, sample rates, the generator, both channels — it does too, with signals
      synthesized from known numbers rather than captured from a cable. It is what lets you learn a
      measurement screen before wiring a bench, check that the analysis reads a known device
      correctly, or work on a laptop with the QA403 at home.
    </p>

    <h2>Selecting it</h2>
    <p>
      The <strong>Analyzer</strong> dropdown in the status bar switches between the
      <strong>QA403</strong> and the <strong>Simulator</strong>, live, with no restart. It is
      unavailable while a measurement is running — stop first. Starting the app with
      <code>--simulate</code> (or <code>run-simulator.bat</code>) only picks which analyzer it
      <em>starts</em> on. With the simulator selected the status bar shows <strong>Simulated</strong>
      and a row of controls beside it:
    </p>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Noise</td>
          <td>
            Adds a white noise floor of −150 dBV/√Hz, with a little 1/f texture below a few hundred
            hertz, downstream of the DUT. On an FFT screen at 192 kHz with 65536 points and a Hann
            window that reads as a floor near −144 dBV per bin, and as −150 on the
            <strong>Noise dBV/√Hz</strong> tile at any FFT size. Turn it off for a noise-free run —
            which is what makes a simulated result exactly reproducible. With the generator off,
            Noise on and Hum off, this is the easiest way to watch a weighting act: A-weighting takes
            the flat floor down by about 2 dB over 20 Hz – 20 kHz, and the hum stack by about 22 dB.
          </td>
        </tr>
        <tr>
          <td>Hum</td>
          <td>
            Adds mains hum: 50 Hz at −90 dBV, then 100 and 150 Hz each 6 dB lower. Also downstream of
            the DUT, so a DUT's own roll-off never removes it — as a real hum pick-up on the input side
            would not be.
          </td>
        </tr>
        <tr>
          <td>DUT</td>
          <td>
            Which device or fixture the simulator pretends is in the path. The default,
            <strong>Straight wire</strong>, is nothing at all. The models are listed below, and each
            has a page of its own saying what it is, what it is for and what every measurement should
            read against it. A DUT model is the device's <em>open-circuit</em> behaviour — its
            response, or its distortion — and has no output impedance of its own; that is the next
            two controls.
          </td>
        </tr>
        <tr>
          <td>Harmonics</td>
          <td>
            Gives every generator tone a fixed harmonic stack — H2 at −46 dBc, H3 at −60 dBc, then 12 dB
            further down per order — standing in for a distorting source. On by default, so a linear
            model has a THD to show. See <em>How it is wired</em> below; the four nonlinear amplifier
            models grey it out and switch it off.
          </td>
        </tr>
        <tr>
          <td>Crosstalk</td>
          <td>
            Couples each channel into the other at −70 dB through the audio band, rising above 6 kHz.
            Off, the channels are perfectly separate. See below.
          </td>
        </tr>
        <tr>
          <td>Zout</td>
          <td>
            The output impedance the simulated amplifier presents at its terminals:
            <strong>None</strong> (an ideal source, the default), <strong>0.03 Ω + 1.5 µH</strong>
            (a solid-state output with its coil) or <strong>2 Ω + 0.3 mH</strong> (a tube amplifier's
            output-transformer leakage). A property of the amplifier separate from the DUT model,
            and it only does anything once a load is connected.
          </td>
        </tr>
        <tr>
          <td>Load</td>
          <td>
            What hangs on the amplifier's terminals: <strong>Open</strong> (the default), 8 Ω or
            4 Ω. With a Zout set, the two form a voltage divider that every measurement sees.
            Swap it between the two passes of an output-impedance run, the way you would swap the
            resistor on a bench.
          </td>
        </tr>
      </tbody>
    </table>
    <p>
      All of them take effect immediately, even mid-run: changing the DUT while a live spectrum is on
      screen shows the difference at once, which is the clearest thing the simulator can demonstrate.
      Changing one part-way through a <em>sweep</em> quietly gives you a result measured under two
      different conditions, so don't.
    </p>

    <h2>How it is wired</h2>
    <p>The simulated bench follows the way a real one is patched, and reading its results depends on knowing that.</p>
    <ul>
      <li>
        <strong>Both generator channels play the same stimulus</strong>, exactly as on the hardware,
        and with <strong>Harmonics</strong> ticked in the status bar each generator tone carries a
        fixed harmonic stack standing in for a distorting source: H2 at −46 dBc, H3 at −60 dBc, and
        every order above that 12 dB further down. That stack is the same at 1 mV and at 1 V. It is
        why every <em>linear</em> model then reads 0.51 % THD at any level, and why the four
        nonlinear amplifier models switch it off — see below. The box starts unchecked, as do
        <strong>Noise</strong> and <strong>Hum</strong>, so an untouched simulator is a clean bench.
      </li>
      <li>
        <strong>A DUT model sits on the left input only.</strong> The left input measures the device;
        the right input taps the generator as a clean reference, as you would wire a bench. That is
        what keeps <em>Right channel is reference</em> meaningful in simulation — filtering both
        channels would divide the DUT straight back out of any two-channel ratio.
      </li>
      <li>
        <strong>An impedance fixture sits on the right input only</strong>, the other way round for
        the same reason: a series-resistor fixture has the generator side on the left — the impedance
        screen's default reference — and the junction with the load on the right.
      </li>
      <li>
        <strong>Every DUT is unity gain in its passband</strong>, so switching a model on changes the
        <em>shape</em> of what comes back and not its level. The one exception is the phono stage,
        whose RIAA curve is only recognisable referenced to 1 kHz and reaches +19 dB at 20 Hz; the
        fixtures attenuate, since a voltage divider never reads unity.
      </li>
      <li>
        <strong>The amplifier is an ideal source until Zout says otherwise.</strong> Every DUT model
        describes an amplifier's <em>open-circuit</em> behaviour — what its terminals do with nothing
        connected — and none of them has an output impedance, so on its own a DUT model drives any
        load exactly as it drives none. The status bar's <strong>Zout</strong> dropdown is a separate
        property of the same amplifier: the impedance its terminals present, <strong>None</strong>,
        <strong>0.03 Ω + 1.5 µH</strong> (a solid-state output with its coil — the BC-1 of the
        QuantAsylum tutorial, DF 286) or <strong>2 Ω + 0.3 mH</strong> (a tube amplifier's
        output-transformer leakage, DF 4). <strong>Load</strong> is what hangs on the terminals:
        Open, 8 Ω or 4 Ω. The two form a voltage divider that is applied to the left channel right
        after the DUT model and before the crosstalk and the noise, so the three compose —
        <em>DUT: Tube amp, Zout: 2 Ω + 0.3 mH, Load: 8 Ω</em> is the transformer's response
        <em>and</em> the 2 Ω + 0.3 mH into 8 Ω (about 1.9 dB of loss with a pole near 4 kHz), the way
        a real tube amplifier behaves into a real speaker.
        The divider is only there while <em>both</em> knobs say so; at None or Open the stream is
        exactly what it was without it. It is not tied to the output-impedance screen: once it is
        on, every measurement sees it — an FFT, a THD sweep, a frequency response — which is what
        makes it a model rather than a demonstration. Set Zout back to None when you do not want it
        in other measurements. Swap the load between the two passes of an output-impedance run (8 Ω,
        then 4 Ω) to demonstrate that measurement.
      </li>
      <li>
        <strong>The channels are perfectly separate until Crosstalk is ticked.</strong> With it on,
        each channel leaks into the other at −70 dB through the audio band, rising 6 dB per octave
        above 6 kHz — about −59 dB at 20 kHz sampled at 192 kHz, −53 dB at 48 kHz, as the filters
        are not pre-warped — a capacitive coupling, the same both ways. It is
        applied to what leaves the DUT and before hum and noise, so a crosstalk sweep against the
        simulator draws the model and nothing else; off, it draws the noise floor.
      </li>
      <li>
        <strong>Hum and noise are added after the DUT</strong>, and the whole thing then meets the
        ADC's soft clip at the selected input range. The simulator has <strong>no autoranging</strong>
        of its own: a screen that ranges — the frequency-response and stepped-sine screens do — is
        safe with a DUT that has gain; a screen that does not will clip a loud low tone through the
        phono stage on the default range, exactly as the hardware would.
      </li>
      <li>
        <strong>The generator's output is hard-clipped at the output range</strong> before the DUT,
        as a DAC's is, so asking for a level above the selected output range truncates the waveform.
        Output range is derived from the amplitude on every screen, so this only happens when a
        stimulus is changed faster than the range can follow.
      </li>
    </ul>

    <h2>The models</h2>
    <p>
      Every model is a small circuit with numbers you can check, not a recording. The linear ones are
      cascades of second-order filter sections; the nonlinear ones a memoryless transfer curve; the
      fixtures a reference resistor with a load. Each page gives, per measurement, one example set of
      settings and the result it should produce — computed from the model's own transfer function,
      so what the screen shows will carry the simulated noise floor and the chart's own decimation on
      top of it, and nothing else.
    </p>
    <p>
      Which model a measurement needs is not obvious from its name, so the DUT list is grouped by
      it — <strong>Linear — response, no distortion</strong>, <strong>Distorting — for THD and
      IMD</strong>, <strong>Impedance fixtures — for the impedance screen</strong> — and each
      measurement screen says so above its chart whenever the simulator's setup makes its result
      trivial: an IMD sweep into a linear model (every point 0 %, nothing on a log axis), a THD sweep
      with Harmonics off, an impedance run into an amplifier model, a crosstalk sweep with the
      Crosstalk box off, an output-impedance run with Zout at None, or a response measurement of the
      straight wire. The line disappears as soon as the status bar is set to something the
      measurement can show, and it never appears with the real analyzer.
    </p>

    <h3>Linear devices</h3>
    <table>
      <thead>
        <tr>
          <th>Model</th>
          <th>What it is</th>
          <th>Best shown by</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-straight-wire')">Straight wire</button>
          </td>
          <td>Nothing in the path. The generator — with whichever of its harmonic stack, hum and noise are switched on — reaches both inputs unaltered.</td>
          <td>Learning the FFT screen; a loopback check of the analysis itself.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-tube-amp')">Tube amp</button>
          </td>
          <td>
            A push-pull valve amplifier with an output transformer: −3 dB at 11 Hz and 52 kHz, a
            small lift at 45 Hz and the transformer's resonance 4 dB up at 38 kHz.
          </td>
          <td>Frequency response at a 96 kHz span; the bandwidth reference setting.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-loudspeaker')">Loudspeaker</button>
          </td>
          <td>
            A two-way speaker in a room: sealed-box roll-off, a 230 Hz cabinet panel that rings, a
            crossover dip, a 4.2 kHz breakup peak, and a wall reflection 3 ms behind the direct sound.
          </td>
          <td>Impulse response and its gate; both decay surfaces.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-bandpass')">Bandpass</button>
          </td>
          <td>
            One second-order band-pass at 1 kHz, Q 20 — 50 Hz wide, unity at the centre. Its decay is
            27.29/Q = 1.36 dB per period, on paper.
          </td>
          <td>Checking the burst decay and CSD against arithmetic.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-marginally-stable')">Marginally stable</button>
          </td>
          <td>Flat, with one Q-500 resonance at 1 kHz that rings for a third of a second. Stable, only just.</td>
          <td>Decay surfaces — a ridge that will not die.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-oscillating')">Oscillating</button>
          </td>
          <td>
            An amplifier that sings on its own at about 1010 Hz, clipping against a ±0.5 V rail. Its
            output is not a response to its input.
          </td>
          <td>What every measurement looks like when its assumptions fail.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-phono-riaa')">Phono stage (RIAA)</button>
          </td>
          <td>The RIAA playback curve: 0 dB at 1 kHz, +19.3 dB at 20 Hz, −19.6 dB at 20 kHz. The one DUT with gain.</td>
          <td>Frequency response and stepped sine; the input ranging.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-dsp-processor')">DSP processor (12 ms)</button>
          </td>
          <td>A fourth-order 20 kHz low-pass with 12 ms of latency. The only model with a real delay.</td>
          <td>The DUT delay readout; the impulse response's position.</td>
        </tr>
      </tbody>
    </table>

    <h3>Filters (the four classic low-pass alignments)</h3>
    <p>
      Four fifth-order low-pass filters with the same 2 kHz corner, each the textbook answer to a
      different question, so a frequency-response run of each shows exactly the trade it made. All
      four are unity at DC and none distorts; the
      <button type="button" class="manual-link" @click="showPage('sim-butterworth')">Butterworth</button>
      page draws them on one axis.
    </p>
    <table>
      <thead>
        <tr>
          <th>Model</th>
          <th>What it is</th>
          <th>Best shown by</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-butterworth')">Butterworth LP (2 kHz)</button>
          </td>
          <td>Maximally flat: −3 dB at 2 kHz, then 100 dB per decade. The reference the other three are read against.</td>
          <td>Frequency response; the ±1 dB and −3 dB rows; a group delay that peaks at the corner.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-chebyshev')">Chebyshev LP (2 kHz)</button>
          </td>
          <td>1 dB of passband ripple for a transition twice as steep; the corner is the −1 dB ripple edge.</td>
          <td>The ripple on a stepped sweep; the ring on the impulse response; the reference level landing in the ripple.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-bessel')">Bessel LP (2 kHz)</button>
          </td>
          <td>A flat 193 µs group delay across the passband, at the price of the gentlest roll-off of the four.</td>
          <td>The Group delay and Phase delay columns — a straight line here, a peak on every other filter.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-elliptic')">Elliptic LP (2 kHz)</button>
          </td>
          <td>Two stopband nulls, at 3.48 and 5.31 kHz, the steepest skirt of the four, and a stopband that levels off at −60 dB.</td>
          <td>Frequency response with smoothing off; what a stopband figure means against a noise floor.</td>
        </tr>
      </tbody>
    </table>

    <h3>Nonlinear amplifiers (the Distorting group)</h3>
    <p>
      Selecting any of these switches the generator's harmonic stack <em>off</em> whatever the
      <strong>Harmonics</strong> box says — the box is greyed out to show it: the left channel then
      carries only what the model does to the signal, and the right is a clean generator tap. The
      box's own setting comes back into force when a linear model is selected again.
    </p>
    <table>
      <thead>
        <tr>
          <th>Model</th>
          <th>What it is</th>
          <th>Best shown by</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-triode')">SE triode</button>
          </td>
          <td>A single-ended triode: H2 = 0.5 % at 1 V peak, climbing exactly 1 dB per dB of drive.</td>
          <td>THD vs amplitude — a straight line.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-class-ab')">Class AB (crossover)</button>
          </td>
          <td>A cold-biased output stage: a 20 mV-wide gain notch at zero, so distortion <em>rises</em> as the level falls, peaking near −33 dBV.</td>
          <td>THD vs amplitude — a hump.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-solid-state')">Solid state (clips at −3 dBV)</button>
          </td>
          <td>Nearly distortionless until a ±1 V rail, where it clips with a short knee.</td>
          <td>THD vs amplitude — a floor and then a wall.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-output-transformer')">Output transformer (saturating)</button>
          </td>
          <td>A core whose flux is limited: third harmonic rising 18 dB per octave below 200 Hz, about 3 % at 20 Hz for 1 V peak.</td>
          <td>THD vs frequency.</td>
        </tr>
      </tbody>
    </table>

    <h3>Impedance fixtures</h3>
    <p>
      Each is a <strong>100 Ω</strong> reference resistor with a load across it, wired as the
      impedance screen expects. The screen's own reference-resistor field must read 100 Ω for a
      simulated run to read true; any other value scales every reading by the ratio.
    </p>
    <table>
      <thead>
        <tr>
          <th>Model</th>
          <th>The load</th>
          <th>Should read</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-fixture-8-ohm')">Fixture: 8 Ω</button>
          </td>
          <td>A plain resistor.</td>
          <td>8.000 Ω at 0.00° at every frequency — the calibration target.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-fixture-rl')">Fixture: 6 Ω + 1 mH</button>
          </td>
          <td>A series RL.</td>
          <td>6 Ω at DC, 45° at 955 Hz, then rising 6 dB per octave.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-fixture-loudspeaker')">Fixture: loudspeaker</button>
          </td>
          <td>6.4 Ω of voice coil with a 45 Hz resonance peaking at 60 Ω, Q 2.5.</td>
          <td>The classic driver curve, with the peak exactly 60 Ω.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-fixture-capacitor')">Fixture: 47 µF</button>
          </td>
          <td>A capacitor with 0.3 Ω of ESR.</td>
          <td>|Z| falling 6 dB per octave towards −90°; the mirror of the reactive fixture.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-fixture-standards')">Fixture: short (leads)</button>
          </td>
          <td>Shorted leads: 0.12 Ω and 0.6 µH.</td>
          <td>The short calibration standard.</td>
        </tr>
        <tr>
          <td>
            <button type="button" class="manual-link" @click="showPage('sim-fixture-standards')">Fixture: open</button>
          </td>
          <td>Nothing but 100 pF of stray capacitance.</td>
          <td>The open calibration standard.</td>
        </tr>
      </tbody>
    </table>

    <div class="manual-note">
      <p>
        <strong>What the simulator cannot show.</strong> Its noise floor does not move with either
        attenuator, so the noise-floor step a real QA403 shows when the generator level crosses an
        output-range boundary never appears. It has no USB transfer pump, so it never drops a block.
        And its stimulus latency is zero, so the flush-after-a-stimulus-change bugs the hardware
        exposes cannot be reproduced on it. Anything about timing between the host and the device
        needs the QA403.
      </p>
    </div>
  </div>
</template>
