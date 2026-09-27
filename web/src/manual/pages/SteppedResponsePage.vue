<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Stepped sine response</h1>

    <p>
      The same level, gain and phase curves as the chirp measurement, acquired one tone at a time.
      It takes minutes rather than seconds, and in exchange nothing else is playing while each point
      is measured — one steady sine, settled, captured, recorded, and only then the next.
    </p>

    <p class="manual-wiring-links">
      <strong>How to wire it:</strong>
      <button type="button" class="manual-link" @click="showPage('wiring-line-level')">
        a line-level DUT
      </button>
      ·
      <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
        a power amplifier
      </button>
    </p>

    <h2>When to prefer it over the chirp</h2>
    <ul>
      <li>
        <strong>The DUT behaves differently under a wideband signal.</strong> Anything with a
        limiter, a compressor, a protection circuit, or a transformer near saturation responds to
        the whole sweep at once; a stepped sine asks it one question at a time.
      </li>
      <li>
        <strong>You want a result with no deconvolution in it.</strong> Each point is a plain
        single-tone measurement, so the distortion artefacts that a deconvolved sweep folds back
        into the record cannot occur here at all.
      </li>
      <li>
        <strong>You need one specific frequency measured properly</strong> rather than a bin near
        it — every point is a tone placed exactly where you asked.
      </li>
    </ul>
    <p>
      For everything else the chirp is faster and finer, and its results are stored and drawn by the
      same code, so the two are directly comparable and can be overlaid on one chart.
    </p>

    <h2>Running it</h2>
    <p>
      One button: <strong>RUN SWEEP</strong>, which walks the points once and stops.
      <strong>STOP</strong> ends it early and keeps what was measured. There is no repeat-until-stopped
      mode, because a run of minutes is not something anyone wants looping.
    </p>

    <h2>Stepped sweep settings</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Start / End (Hz)</td>
          <td>
            The frequency range, stepped upward. Anything above 98% of Nyquist is dropped, and the
            panel warns when that will truncate the run.
          </td>
        </tr>
        <tr>
          <td>Steps per octave</td>
          <td>
            Point density. Every point is its own capture, so this is the dominant cost of the
            measurement — the estimate underneath is worth reading before pressing RUN SWEEP.
          </td>
        </tr>
        <tr>
          <td>Level mode</td>
          <td>
            <strong>Generator voltage</strong> holds the drive constant. The two output modes probe
            at 1 kHz first and solve for the generator level that reaches the target at the DUT's
            output.
          </td>
        </tr>
        <tr>
          <td>Level / Power, Unit, Load (Ω)</td>
          <td>The target and the load it is referred to, as on the other screens.</td>
        </tr>
        <tr>
          <td>Generator channels</td>
          <td>
            Which outputs play the tone. Both inputs are always analysed, because the gain trace is
            a ratio between them and the two have to come from one capture. The right box locks on
            while the right input is the reference — it has to be fed the signal it is compared
            against.
          </td>
        </tr>
        <tr>
          <td>Right channel is reference</td>
          <td>
            Loop the right output back to the right input and compare the two. This is what makes
            gain a real transfer function and gives it a phase; without it, gain is the left
            channel's own level shifted by the drive, and there is no phase to report at all.
          </td>
        </tr>
        <tr>
          <td>Averages per point</td>
          <td>How many records are averaged before a point is recorded, at a proportional cost in time.</td>
        </tr>
        <tr>
          <td>Adaptive resolution</td>
          <td>
            Sizes the FFT per point and picks the sample rate from the top of the range. This
            measurement reads only the tone and no harmonics, so a 20 kHz run needs 48 kHz rather
            than 192 kHz — a quarter of the work. Turn it off for a fixed 192 kHz / 131072-point
            reference comparable with a THD sweep. The line underneath states what will be used.
          </td>
        </tr>
      </tbody>
    </table>
    <p>
      What the ±1 dB and −3 dB corners are measured down from is set at the top of the
      <strong>Measurements</strong> panel, not here: the same control, the same default and the
      same reasoning as the chirp measurement's, and changed after the sweep it re-finds the corners
      of the points already on screen — see
      <button type="button" class="manual-link" @click="showPage('frequency-response')">
        Frequency response
      </button>.
    </p>

    <h2>What the views show</h2>
    <p>
      Exactly the chirp measurement's views, since both produce the same curves: the Bode chart with
      its FR (dBV) and Gain (dB) views, phase, the ±1 dB and −3 dB bandwidth markers, the
      Measurements panel with the corners and bandwidth, and — when the right channel is the
      reference — the Nyquist and Nichols projections with their Stability and Poles-and-zeros
      panels. See
      <button type="button" class="manual-link" @click="showPage('frequency-response')">
        Frequency response
      </button>
      for what each of those means, and
      <button type="button" class="manual-link" @click="showPage('view-nyquist')">the Nyquist view</button>,
      <button type="button" class="manual-link" @click="showPage('view-nichols')">the Nichols view</button>
      and
      <button type="button" class="manual-link" @click="showPage('view-pole-zero')">the pole–zero map</button>
      and
      <button type="button" class="manual-link" @click="showPage('view-s-plane')">the |H(s)| surface</button>
      for the four projections in detail. One difference there: a stepped run removes no bulk
      delay, so <strong>Restore removed delay</strong> is not offered and the phase it plots is
      already the physical one.
    </p>
    <p>Two differences:</p>
    <ul>
      <li>
        <strong>No coherence trace.</strong> A stepped point's coherence is 1 by construction, so
        drawing it would be a flat line claiming a measurement nobody made. The checkbox is not
        offered.
      </li>
      <li>
        <strong>No delay figure in the progress line, and no delay removed.</strong> Nothing is
        deconvolved, so there is no bulk rotation to recover or restore — which is why the
        <strong>Delay</strong> trace (group delay, phase delay) and the Measurements panel's delay
        rows are the DUT's <em>physical</em> delay here, with nothing put back, whenever the right
        channel is the reference. Group delay is a plain difference between neighbouring points, so
        it is only right while the tone spacing keeps each phase step under half a turn: at
        <em>n</em> steps per octave a DUT with delay τ is readable up to about
        <em>f</em> = 1 / (2 · τ · (2<sup>1/n</sup> − 1)). A 12 ms digital processor at 4 steps per
        octave runs out at ~220 Hz; step density is the lever.
      </li>
    </ul>
    <p>
      <strong>Show data points</strong> in the display panel is worth turning on here: it marks
      where readings were actually taken, which on a stepped run is genuine information rather than
      decoration.
    </p>

    <h2>Zooming</h2>
    <p>
      As on every response chart: drag a box to zoom, double-click or press
      <strong>Reset zoom</strong> to return to the display panel's window.
    </p>

    <div class="manual-note">
      <p>
        Steps per octave times averages per point is the run time. A 20 Hz–20 kHz run at 12 steps
        per octave is about 120 points; at four averages each, that is a long time to hold a valve
        amplifier at full drive. Survey with the chirp, then step only the region that matters.
      </p>
    </div>
  </div>
</template>
