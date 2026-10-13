<script setup lang="ts">
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Measuring frequency response</h1>

    <p>
      This answers one question: <strong>does the device treat every frequency the same?</strong> The
      answer is a curve. A flat curve means bass, middle and treble all come out the same size, which
      is what most equipment is trying to do. Where the curve droops at the ends is where the device
      runs out.
    </p>
    <p>
      The clever part is how fast it is. Instead of measuring one tone at a time, the analyzer plays a
      <strong>chirp</strong> — a slide from the bottom of the band to the top, in under a second — and
      works out the whole curve from that single recording. A complete response measurement takes a
      couple of seconds.
    </p>

    <h2>Wire it up</h2>
    <ul>
      <li><strong>OUT L+</strong> to the device's input.</li>
      <li>The device's output back to <strong>IN L+</strong>.</li>
      <li>Shorting plugs in the other three inputs.</li>
      <li>
        For a power amplifier: its load resistor connected, and a divider if it can produce more than
        about 30 V. See
        <button type="button" class="manual-link" @click="showPage('guide-connecting')">
          Plugging everything in
        </button>.
      </li>
    </ul>

    <h2>Settings to start with</h2>
    <table class="manual-wrap">
      <thead>
        <tr>
          <th>Setting</th>
          <th>Start with</th>
          <th>Why</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Set generator by</td>
          <td>Input voltage</td>
          <td>
            You name the level the analyzer plays. The other two settings let you name what should
            come <em>out</em> of the device instead, which is very handy later — the app works out the
            drive itself.
          </td>
        </tr>
        <tr>
          <td>Amplitude</td>
          <td>−20 dBV, or whatever gives about 1 W</td>
          <td>Response hardly changes with level, so there is no reason to be brave.</td>
        </tr>
        <tr>
          <td>Frequency span</td>
          <td>48 kHz</td>
          <td>
            Measures up to 48 kHz — well past hearing, which is where an amplifier's troubles start to
            show. It costs no extra time.
          </td>
        </tr>
        <tr>
          <td>Resolution</td>
          <td>0.73 Hz</td>
          <td>Plenty of detail, and each pass takes about a second and a half.</td>
        </tr>
        <tr>
          <td>Smoothing</td>
          <td>1/12 octave</td>
          <td>Takes the ripple off without hiding anything real.</td>
        </tr>
        <tr>
          <td>Averages</td>
          <td>2</td>
          <td>
            Two or more is what makes the <strong>coherence</strong> reading mean something. A single
            pass always claims 1.
          </td>
        </tr>
      </tbody>
    </table>
    <p>
      Press <strong>Single</strong> for one measurement, or <strong>Run</strong> to keep sweeping
      while you adjust something and watch the curve move.
    </p>

    <h2>Reading the curve</h2>
    <ul>
      <li>
        <strong>The flat middle</strong> is the device working properly. How flat is the interesting
        part: ±0.5 dB across 20 Hz–20 kHz is a good solid-state amplifier; a valve amplifier with an
        output transformer will do less well at both ends, and that is normal rather than broken.
      </li>
      <li>
        <strong>The droop at the bottom</strong> comes from coupling capacitors and output
        transformers.
      </li>
      <li>
        <strong>The droop at the top</strong> comes from the amplifier running out of bandwidth.
      </li>
      <li>
        <strong>A bump just above the audio band</strong> — say 4 dB up around 30–40 kHz — is normal on
        a valve amplifier. It is the output transformer resonating, and it is one of the reasons to
        measure past 20 kHz.
      </li>
      <li>
        <strong>Coherence</strong>, if you switch that curve on, should sit at almost exactly 1 across
        the band. Where it dips, the measurement there is not to be trusted — usually too little signal
        or too much noise.
      </li>
    </ul>

    <h2>The numbers under the chart</h2>
    <p>
      The <strong>Measurements</strong> panel reads the corners off the curve for you: where it has
      fallen 1 dB and where it has fallen 3 dB. It also shows the <strong>reference level</strong>
      those corners were measured down from.
    </p>
    <p>
      That reference is worth understanding, because it is the one thing here that catches people out.
      The corners are measured down from the <em>flat middle</em> of the response, not from its highest
      point. On an amplifier with a resonant bump above the band, measuring down from the highest point
      would put both "corners" on the sides of that bump and report a bandwidth of a few kHz on a
      perfectly good amplifier. Measuring down from the passband instead gives the answer you wanted.
      If your device genuinely <em>is</em> a peak — a tweeter, a bandpass filter — there is a setting
      to reference the peak instead.
    </p>

    <h2>The slow, literal alternative</h2>
    <p>
      There is a second screen, <strong>Stepped sine response</strong>, that measures the same curve one
      tone at a time. It takes minutes instead of seconds, and it is worth it in two cases: when a
      device is so noisy or so nonlinear that the chirp's answer looks odd, and when you want to be
      able to say that every point was measured with nothing else playing. Start with the chirp; reach
      for the stepped sweep when the chirp puzzles you.
    </p>

    <p>
      The full manual's
      <button type="button" class="manual-link" @click="showPage('frequency-response')">
        Frequency response
      </button>
      chapter covers every setting, the phase curves, and the stability views built on them.
    </p>
  </div>
</template>
