<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { burstDecaySlice, impulseCurve, magnitudeCurve, phaseCurve } from '../../sim/curves'
import { DB_PER_PERIOD_NUMERATOR, MARGINAL_GAIN_DB, MARGINAL_Q, marginallyStable } from '../../sim/models'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11 (24 kHz span, 1/24-octave smoothing): a flat-topped bump
// of +2.2 dB from 990 to 1010 Hz where the model has a +12 dB peak 2 Hz wide — the smoothing's
// 29 Hz band averaging 12 dB over 2 Hz — and 0.00 dB everywhere else.
const dut = marginallyStable(48_000)
// A peaking section's poles sit at Q·A, with A = 10^(gain/40): the ring decays at that Q, not at
// the nominal one.
const poleQ = MARGINAL_Q * Math.pow(10, MARGINAL_GAIN_DB / 40)
const dbPerPeriod = DB_PER_PERIOD_NUMERATOR / poleQ
const ringTimeConstantMs = (poleQ / Math.PI) // periods of 1 kHz to fall 8.7 dB, i.e. milliseconds

const zoomSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain, no smoothing (dB)', points: magnitudeCurve(dut, 980, 1020, 800), color: 'gain' },
  { name: 'Gain, 1/24 octave', points: magnitudeCurve(dut, 980, 1020, 200, 24), color: 'grey', dashed: true },
  { name: 'Phase (°)', points: phaseCurve(dut, 980, 1020, 800), color: 'phase', axis: 'y2' },
])
const impulseSeries = computed<ManualSeries[]>(() => [{ name: 'Impulse (relative to peak)', points: impulseCurve(dut, 2, 60), color: 'left' }])
const burstSeries = computed<ManualSeries[]>(() => [
  { name: '1 kHz', points: burstDecaySlice(dut, 1000, 30), color: 'left' },
  { name: '900 Hz', points: burstDecaySlice(dut, 900, 30), color: 'grey', dashed: true },
])
</script>

<template>
  <div>
    <h1>Marginally stable</h1>

    <p>
      An amplifier whose feedback loop has almost run out of phase margin: flat everywhere, with
      one resonance at <strong>1 kHz</strong> so sharp — Q {{ MARGINAL_Q }}, +{{ MARGINAL_GAIN_DB }} dB,
      about 2 Hz wide — that it rings for a third of a second after any excitation. Nothing about it
      is unstable: every pole is inside the unit circle, and its output is a response to its input
      like any other DUT's. It is the picture of <em>nearly</em>.
    </p>
    <p>
      What it demonstrates is that a magnitude plot and a decay plot answer different questions. On a
      Bode plot this is a narrow peak like any other — and through the default smoothing not even
      that. On a burst decay it is unmistakable: a resonance at pole Q {{ poleQ.toFixed(0) }} loses
      {{ dbPerPeriod.toFixed(3) }} dB per period, so over thirty periods the ridge falls under a
      decibel and simply refuses to die. That is the argument for having decay charts at all.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Peak', `+${MARGINAL_GAIN_DB} dB at 1000 Hz, 2 Hz wide, with Smoothing None; +2.2 dB flat-topped over 990 – 1010 Hz through 1/24 octave`],
        ['Everywhere else', '0.00 dB, 0°'],
        ['Phase', 'a full 180° turn inside 2 Hz — invisible at any ordinary resolution'],
        ['Ring', `a 1 kHz tail with a time constant of about ${ringTimeConstantMs.toFixed(0)} ms; still audible at −60 dB a third of a second in`],
        ['Burst decay', `${dbPerPeriod.toFixed(3)} dB per period at 1 kHz: under 1 dB over the last twenty periods, after a 26 dB step in the first five`],
        ['THD', '0.000 %; with Harmonics ticked, the generator’s 0.51 % — at 500 Hz the second harmonic sits on the peak and D2 reads 12 dB high'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — the burst decay is the one that tells the truth about it.</li>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — the peak, and how completely smoothing can hide it.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — five points per octave step straight over a 2 Hz peak; 48 per octave still miss it half the time.</li>
      <li><a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> — a 1 kHz tone comes back +12 dB; move it to 1010 Hz and it comes back at 0 dB.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — zero; with Harmonics ticked, flat at 0.51 % except for a spike at 500 Hz (H2 on the peak) and a dip at 1 kHz.</li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>Frequency response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Resolution (Hz)', '0.37 — see below'],
        ['Right channel is reference', 'On'],
        ['Smoothing', 'None'],
        ['Averages', '1'],
        ['Display', 'Frequency range 980 – 1020 Hz, or zoom in with a drag'],
      ]"
    />
    <ManualChart
      :series="zoomSeries"
      :x-log="false"
      :x-min="980"
      :x-max="1020"
      :y-min="-2"
      :y-max="14"
      :y2-min="-180"
      :y2-max="180"
      :y2-step="90"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Phase (°)"
      :height="260"
      caption="Expected response between 980 and 1020 Hz: a +12 dB peak 2 Hz wide with the phase turning through 180° inside it, and (dashed) what the default 1/24-octave smoothing makes of it — a +2.2 dB plateau 20 Hz wide."
    />
    <p>
      Two settings decide whether this peak is measured at all. <strong>Resolution</strong>: at the
      default 1.46 Hz per bin the 2 Hz peak is one bin; 0.37 Hz gives it five, and the record grows
      to 2.7 s. <strong>Smoothing</strong>: a 1/24-octave band is 29 Hz at 1 kHz, and a power average
      of 12 dB over 2 Hz spread across 29 Hz is +2.2 dB — the app measured exactly that. The record
      also has to be long enough for the ring to finish inside it, or the sweep's own gate
      truncates the tail and broadens the peak: the ring's time constant is
      {{ ringTimeConstantMs.toFixed(0) }} ms, so a 0.68 s record is comfortable and a 0.34 s one is
      not. Coherence stays at 1.000 throughout; this is a linear, deterministic DUT, however alarming
      its ring looks.
    </p>

    <h2>Impulse response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Resolution (Hz)', '0.37'],
        ['Divide by', 'Nothing (the generated sweep)'],
        ['Gate: Before (ms) / After (ms)', '2 / 300 — the ring lasts that long'],
        ['Burst decay', 'Prefer frequency, 30 periods, 8 points per period'],
      ]"
    />
    <ManualChart
      :series="impulseSeries"
      :x-log="false"
      :x-min="-2"
      :x-max="60"
      :y-min="-0.4"
      :y-max="1.05"
      x-label="ms"
      y-label="relative to peak"
      :height="220"
      caption="Expected impulse response, first 60 ms: the direct spike, then a 1 kHz ring that has lost only 2 dB by the end of the chart. In the volts view it looks like a small tail; switch to Normalised (dB) and it is a straight line falling 0.027 dB per cycle for hundreds of cycles."
    />
    <ManualChart
      :series="burstSeries"
      :x-log="false"
      :x-min="0"
      :x-max="30"
      :y-min="-60"
      :y-max="2"
      x-label="periods"
      y-label="dB"
      :height="220"
      caption="Expected burst decay at 1 kHz (blue): a 26 dB step as the wavelet leaves the broadband spike behind, then a shelf that falls under a decibel across the remaining periods. 900 Hz (dashed), where nothing rings, drops straight into the floor."
    />
    <p>
      The shelf is the point. The spike at t = 0 is broadband and large; the ring is narrowband and,
      at any one instant, small — so the wavelet reads a big number while it overlaps the spike and
      a much smaller one afterwards, and the step between them is the ratio of the two. After that
      the ring decays at its pole Q of {{ poleQ.toFixed(0) }} (a +12 dB peaking section at Q 500 has
      its poles at twice the nominal Q), {{ dbPerPeriod.toFixed(3) }} dB per period: over the last
      twenty periods of the chart that is {{ (20 * dbPerPeriod).toFixed(2) }} dB. On the screen's
      waterfall this is a ridge at 1 kHz that runs flat out to the back of the surface while every
      other frequency has long since dropped into the floor. Compare the
      <button type="button" class="manual-link" @click="showPage('sim-bandpass')">Bandpass</button>, which falls 1.36 dB per period and is gone by thirty; the two models
      differ by a factor of twenty-five in Q and look nothing alike here, and nearly identical on
      a smoothed Bode plot.
    </p>
  </div>
</template>
