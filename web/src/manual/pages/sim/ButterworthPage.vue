<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { bandwidth, groupDelayCurve, impulseCurve, magnitudeCurve } from '../../sim/curves'
import { besselLowPass, butterworthLowPass, chebyshevLowPass, ellipticLowPass, FILTER_CORNER_HZ } from '../../sim/filters'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-21 (24 kHz span, 1/24-octave smoothing, right channel as
// reference, output-voltage mode at 0 dBV): 0.00 dB to 1 kHz, −0.24 at 1.5 kHz, −2.97 at 2 kHz,
// −10.1 at 2.5 kHz, −30.7 at 4 kHz, −64.2 at 8 kHz; ±1 dB "< 4 Hz – 1.75 kHz", −3 dB "< 4 Hz –
// 2.00 kHz"; group delay 256 µs at 100 Hz, 289 at 1 kHz, 400 at the corner; generator settled
// at 0.01 dBV; DUT delay 14.1 samples (the filter's own bulk delay at 48 kHz).
const dut = butterworthLowPass(48_000)
const bw = bandwidth(dut, 4, 22_800, 200, 2000, 24)

const frSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain (dB)', points: magnitudeCurve(dut, 4, 22_800, 400, 24), color: 'gain' },
  { name: 'Group delay (µs)', points: groupDelayCurve(dut, 4, 22_800), color: 'phase', axis: 'y2' },
])
const frMarkers = computed<ManualMarker[]>(() => [
  { y: -3, color: 'left' },
  { x: bw.threeDbHighHz!, label: `−3 dB ${formatHz(bw.threeDbHighHz!)}`, color: 'left' },
  { x: bw.oneDbHighHz!, label: `±1 dB ${formatHz(bw.oneDbHighHz!)}`, color: 'amber' },
])

// The four alignments on one axis: the picture the whole group exists for.
const compareSeries = computed<ManualSeries[]>(() => [
  { name: 'Butterworth', points: magnitudeCurve(dut, 100, 22_800, 400), color: 'gain' },
  { name: 'Chebyshev', points: magnitudeCurve(chebyshevLowPass(48_000), 100, 22_800, 400), color: 'left' },
  { name: 'Bessel', points: magnitudeCurve(besselLowPass(48_000), 100, 22_800, 400), color: 'right' },
  { name: 'Elliptic', points: magnitudeCurve(ellipticLowPass(48_000), 100, 22_800, 400), color: 'amber' },
])

const impulseSeries = computed<ManualSeries[]>(() => [{ name: 'Impulse (relative to peak)', points: impulseCurve(dut, 0.2, 2.5), color: 'left' }])
</script>

<template>
  <div>
    <h1>Butterworth LP (2 kHz)</h1>

    <p>
      A fifth-order Butterworth low-pass with its corner at <strong>{{ FILTER_CORNER_HZ / 1000 }} kHz</strong>:
      maximally flat in the passband, −3.01 dB at the corner, then 100 dB per decade — five poles,
      20 dB each. It is the first of four filters of the same order at the same corner, each the
      textbook answer to a different question, and it is the one the other three are read against:
      the <button type="button" class="manual-link" @click="showPage('sim-chebyshev')">Chebyshev</button>
      trades passband flatness for a steeper transition, the
      <button type="button" class="manual-link" @click="showPage('sim-bessel')">Bessel</button> trades
      steepness for a flat group delay, and the
      <button type="button" class="manual-link" @click="showPage('sim-elliptic')">elliptic</button> adds
      zeros to get the steepest transition of all at the price of a stopband that stops falling.
    </p>
    <p>
      The corner is at 2 kHz on purpose. It is an octave above the FFT screen's default 1 kHz
      tone, which therefore sits in the passband of all four (−0.004 dB here), and a decade below
      20 kHz, so a 24 kHz span shows the whole transition and a decade of stopband — about 100 dB
      of it, which is where the simulator's own floor is once <strong>Noise</strong> is on. Every
      filter is unity at DC, so none of them changes the level of what passes.
    </p>

    <ManualChart
      :series="compareSeries"
      :x-min="100"
      :x-max="24000"
      :y-min="-100"
      :y-max="5"
      x-label="Hz"
      y-label="Gain (dB)"
      :height="280"
      caption="The four alignments, fifth order at 2 kHz: the Butterworth's smooth knee, the Chebyshev's ripple and steeper fall, the Bessel's gentle slope, and the elliptic's two notches above a stopband that levels off at −60 dB."
    />

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Gain', '0.00 dB to 1 kHz, −0.24 dB at 1.5 kHz, −2.97 dB at 2 kHz, −10.1 dB at 2.5 kHz, −30.7 dB at 4 kHz, −64 dB at 8 kHz'],
        ['−3 dB corners', `< start, ${formatHz(bw.threeDbHighHz!)}`],
        ['±1 dB corners', `< start, ${formatHz(bw.oneDbHighHz!)}`],
        ['Reference level', '0.00 dB (passband median, 200 Hz – 2 kHz)'],
        ['Group delay', '256 µs at 100 Hz, 289 µs at 1 kHz, rising to 400 µs at the corner — 54 % more than at DC'],
        ['DUT delay', '14 samples at 48 kHz (the filter’s own bulk delay, 257 µs, not a latency)'],
        ['Impulse', 'a rounded pulse about 0.4 ms wide with one undershoot of about a quarter of the peak, and nothing ahead of it'],
        ['THD', '0.000 %; with Harmonics ticked, 0.51 % below 700 Hz, then D2 and D3 fall away as they cross the corner'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — the corner, the slope, and the group delay column peaking at the corner.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — the same curve point by point; a good first check that the two agree.</li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — a clean pulse with a single undershoot; compare the Chebyshev's ring.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — with Harmonics ticked, the generator's 0.51 % until the harmonics reach 2 kHz: D2 goes at 1 kHz, D3 at 667 Hz.</li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>Frequency response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Chirp start (Hz)', '2'],
        ['Resolution (Hz)', '0.37'],
        ['Right channel is reference', 'On — the group delay column then carries the filter’s own delay rather than only its excess'],
        ['Smoothing', '1/24 octave'],
        ['Averages', '1'],
        ['Bandwidth reference', 'Passband median, 200 – 2000 Hz'],
      ]"
    />
    <ManualChart
      :series="frSeries"
      :markers="frMarkers"
      :x-min="4"
      :x-max="24000"
      :y-min="-100"
      :y-max="5"
      :y2-min="0"
      :y2-max="500"
      :y2-step="100"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Group delay (µs)"
      :height="280"
      caption="Expected Bode plot and group delay: flat to 1 kHz, −3 dB at 2 kHz, 100 dB per decade above; the delay rises from 256 µs to 400 µs at the corner and falls away with the response."
    />
    <p>
      Two things to notice. The <strong>±1 dB</strong> corner sits at 1.75 kHz while the −3 dB one
      is at 2.00 kHz — a fifth-order knee is sharp, but not that sharp. And the group delay is
      not flat: it peaks at the corner, half again what it is at DC, which is what a step through
      this filter overshoots on. That peak is the whole reason the
      <button type="button" class="manual-link" @click="showPage('sim-bessel')">Bessel</button> exists.
      Below the corner the delay reads about 256 µs, which is the filter's bulk delay; the screen
      recovered it as a <em>DUT delay</em> of 14 samples because the right channel was the
      reference, and with the delay removed the <em>phase delay</em> column would read only what
      is left over.
    </p>

    <h2>Impulse response</h2>
    <ManualChart
      :series="impulseSeries"
      :x-log="false"
      :x-min="-0.2"
      :x-max="2.5"
      :y-min="-0.3"
      :y-max="1.05"
      x-label="ms"
      y-label="relative to peak"
      :height="200"
      caption="Expected impulse response: a single rounded pulse a few hundred microseconds wide, one undershoot, and nothing before it. The Chebyshev page shows what ripple does to this."
    />
  </div>
</template>
