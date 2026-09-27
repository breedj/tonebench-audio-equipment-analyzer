<script setup lang="ts">
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Checking an amplifier</h1>

    <p>
      Six measurements, in this order, and you will know more about your amplifier than its datasheet
      does. Allow an hour the first time. Everything here uses one channel at a time; do the other
      channel afterwards and compare the two — a difference between the channels of one amplifier is
      the easiest fault there is to spot.
    </p>

    <div class="manual-note">
      <p>
        <strong>Before switching anything on:</strong> the load resistor is connected to the
        amplifier's output, and if the amplifier can make more than about 30 V you have a divider
        between it and the analyzer. A valve amplifier driven with no load can destroy its own output
        transformer. The full manual's
        <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
          Wiring: a power amplifier
        </button>
        page has the divider values, the load sizing and the order to connect things in.
      </p>
    </div>

    <h2>0. Measure the analyzer first</h2>
    <p>
      Loopback, 1 kHz, −20 dBV. Write down the THD and the noise. It takes two minutes and it is the
      only way to know later whether a number belongs to the amplifier or to your bench. See
      <button type="button" class="manual-link" @click="showPage('guide-first-measurement')">
        Your first measurement
      </button>.
    </p>

    <h2>1. Gain, at a safe level</h2>
    <p>
      <strong>FFT analysis</strong>, 1 kHz, generator at −40 dBV to start. Look at the level that comes
      back and at the <strong>Gain</strong> tile. Now you know how hard you may drive it: if the gain is
      26 dB, then −20 dBV in gives you +6 dBV out, which is 2 V, which into 8 Ω is half a watt.
    </p>
    <p>
      Work out the generator level you need for <strong>1 watt</strong> and use that for everything
      that follows. 1 W into 8 Ω is 2.8 V out, which is +9 dBV. With 26 dB of gain that means a
      generator setting of −17 dBV.
    </p>

    <h2>2. Frequency response at 1 W</h2>
    <p>
      The
      <button type="button" class="manual-link" @click="showPage('guide-response')">
        frequency response
      </button>
      screen, span 48 kHz, smoothing 1/12 octave, two averages. A few seconds, and you have the whole
      curve plus the bandwidth figures.
    </p>

    <h2>3. Distortion at 1 kHz, 1 W</h2>
    <p>
      Back to <strong>FFT analysis</strong>. Read THD, and then look at the
      <strong>Harmonics</strong> table to see <em>which</em> harmonics. This is the measurement that
      tells you what kind of amplifier you have, not just how good it is — see
      <button type="button" class="manual-link" @click="showPage('guide-distortion')">
        Distortion in plain words
      </button>.
    </p>

    <h2>4. THD vs amplitude — how loud can it really go</h2>
    <p>
      A <strong>THD vs amplitude</strong> sweep from well below 1 W up to just past where you expect
      clipping. The curve is flat, then bends upwards, then goes almost vertical. The convention is to
      quote the power at <strong>1 % distortion</strong>, and it is usually a good deal less than the
      number on the front panel.
    </p>
    <p>
      Approach the top end carefully, in small steps, and keep an eye on the amplifier. Stop as soon as
      the curve has clearly turned upwards; there is nothing to learn from driving it into hard
      clipping for a minute.
    </p>

    <h2>5. THD vs frequency at 1 W</h2>
    <p>
      A <strong>THD vs frequency</strong> sweep, 20 Hz to 20 kHz. On a valve amplifier the bass end
      rises sharply — that is the output transformer's core saturating, and it is the honest limit of
      the design rather than a fault. A transistor amplifier usually rises slightly at the top instead.
    </p>

    <h2>6. Noise, with the generator off</h2>
    <p>
      Stop the generator, keep everything else connected, and measure. This is the amplifier's own
      hiss and hum, and it is what you will hear between tracks. See
      <button type="button" class="manual-link" @click="showPage('guide-noise')">
        Noise, hum and the floor
      </button>.
    </p>

    <h2>Worth adding once you are comfortable</h2>
    <ul>
      <li>
        <strong>Output impedance and damping factor</strong> — measure the gain into two different
        loads and the app works out the amplifier's internal resistance. It pauses and asks you to
        change the load resistor half way through.
      </li>
      <li>
        <strong>Crosstalk</strong> — drive one channel, measure the other, and see how well separated
        they are.
      </li>
      <li>
        <strong>Two-tone IMD</strong> — the distortion test that is hardest to fake.
      </li>
    </ul>

    <h2>A real example to compare against</h2>
    <p>
      A home-built <strong>10 W push-pull EL84 valve amplifier</strong>, measured with this app on this
      bench. Nothing here is a target — it is a healthy small valve amplifier, so if your figures are in
      this neighbourhood, nothing is wrong:
    </p>
    <table>
      <thead>
        <tr>
          <th>Measurement</th>
          <th>Result</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Gain</td>
          <td>26.5 dB, and it inverts (which is normal and inaudible)</td>
        </tr>
        <tr>
          <td>Frequency response</td>
          <td>within 0.11 dB from 100 Hz to 20 kHz; 1 dB down at 12.7 Hz</td>
        </tr>
        <tr>
          <td>THD at 1 W, 1 kHz</td>
          <td>0.158 %, almost all third harmonic</td>
        </tr>
        <tr>
          <td>Best THD</td>
          <td>0.023 %, at 50 mW</td>
        </tr>
        <tr>
          <td>Honest power</td>
          <td>1 % at about 4 W; 13 % by 8.3 W — so "10 W" really means 6–8 W</td>
        </tr>
        <tr>
          <td>Weak spot</td>
          <td>1.06 % at 19 Hz — the transformer core saturating in the deep bass</td>
        </tr>
        <tr>
          <td>Output impedance</td>
          <td>0.94 Ω, damping factor 8.5</td>
        </tr>
      </tbody>
    </table>

    <h2>Save everything as you go</h2>
    <p>
      Press <strong>SINGLE</strong> (or let a sweep finish), then the save button, and give each result
      a name that says the conditions: <em>THD 1 kHz 1 W left</em>. In six months, when you have
      changed the coupling capacitors, you can tick the old result in the History list and draw it
      straight under the new one. That comparison is the whole reason for owning an analyzer, and it
      only works if you saved the first one. See
      <button type="button" class="manual-link" @click="showPage('guide-keeping-results')">
        Keeping your results
      </button>.
    </p>
  </div>
</template>
