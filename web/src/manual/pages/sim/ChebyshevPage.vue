<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { bandwidth, groupDelayCurve, impulseCurve, magnitudeCurve } from '../../sim/curves'
import { butterworthLowPass, chebyshevLowPass, FILTER_CORNER_HZ } from '../../sim/filters'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-21 (24 kHz span, 1/24-octave smoothing, right channel as
// reference, output-voltage mode at 0 dBV): −0.07 dB at 100 Hz, −0.91 at 500 Hz, −0.27 at 1 kHz,
// −0.80 at 1.5 kHz, −0.94 at 2 kHz, −18.2 at 2.5 kHz, −46.0 at 4 kHz, −81.8 at 8 kHz; reference
// level −0.60 dB; ±1 dB "< 4 Hz – 2.02 kHz", −3 dB "< 4 Hz – 2.08 kHz"; group delay 368 µs at
// 100 Hz, 391 at 1 kHz, 1005 at the corner; generator settled at +0.60 dBV; DUT delay 18.6 samples.
const dut = chebyshevLowPass(48_000)
const bw = bandwidth(dut, 4, 22_800, 200, 2000, 24)

const passbandSeries = computed<ManualSeries[]>(() => [
  { name: 'Chebyshev (dB)', points: magnitudeCurve(dut, 20, 3000, 600), color: 'gain' },
  { name: 'Butterworth (dB)', points: magnitudeCurve(butterworthLowPass(48_000), 20, 3000, 600), color: 'grey', dashed: true },
])
const passbandMarkers: ManualMarker[] = [
  { y: 0, color: 'grey', dashed: true },
  { y: -1, label: '−1 dB ripple floor', color: 'amber' },
  { x: FILTER_CORNER_HZ, label: 'corner', color: 'left' },
]

const frSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain (dB)', points: magnitudeCurve(dut, 4, 22_800, 400, 24), color: 'gain' },
  { name: 'Group delay (µs)', points: groupDelayCurve(dut, 4, 22_800), color: 'phase', axis: 'y2' },
])
const frMarkers = computed<ManualMarker[]>(() => [
  { y: bw.referenceDb - 3, color: 'left' },
  { x: bw.threeDbHighHz!, label: `−3 dB ${formatHz(bw.threeDbHighHz!)}`, color: 'left' },
])

const impulseSeries = computed<ManualSeries[]>(() => [
  { name: 'Chebyshev', points: impulseCurve(dut, 0.2, 4), color: 'left' },
  { name: 'Butterworth', points: impulseCurve(butterworthLowPass(48_000), 0.2, 4), color: 'grey', dashed: true },
])
</script>

<template>
  <div>
    <h1>Chebyshev LP (2 kHz)</h1>

    <p>
      A fifth-order Chebyshev (type I) low-pass at <strong>{{ FILTER_CORNER_HZ / 1000 }} kHz</strong>
      with <strong>1 dB</strong> of passband ripple. The trade is the classic one: allow the passband
      to ripple between 0 and −1 dB and the transition just above the corner becomes about twice
      as steep as the <button type="button" class="manual-link" @click="showPage('sim-butterworth')">Butterworth's</button>
      — −18 dB at 2.5 kHz against −10, −46 dB at 4 kHz against −31. Two conventions follow from the
      ripple. The <em>corner</em> is the ripple edge, where the response is −1 dB, not −3; and
      because the order is odd the ripple runs <em>down</em> from 0 dB at DC, so the passband
      never rises above unity and the filter stays inside the every-DUT-is-unity rule.
    </p>

    <ManualChart
      :series="passbandSeries"
      :markers="passbandMarkers"
      :x-min="20"
      :x-max="3000"
      :y-min="-6"
      :y-max="0.5"
      x-label="Hz"
      y-label="Gain (dB)"
      :height="240"
      caption="The passband, magnified: five ripple extrema between 0 and −1 dB — troughs at 618 Hz, 1.62 kHz and the 2 kHz corner, crests at DC, 1.18 kHz and 1.90 kHz — against the Butterworth's smooth droop."
    />

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Gain', '−0.07 dB at 100 Hz, −0.91 at 500 Hz, −0.27 at 1 kHz, −0.80 at 1.5 kHz, −0.94 at 2 kHz, −18.2 at 2.5 kHz, −46.0 at 4 kHz, −82 at 8 kHz'],
        ['Reference level', '−0.60 dB — the passband median over 200 Hz – 2 kHz lands in the ripple, so every corner is measured from there'],
        ['−3 dB corners', `< start, ${formatHz(bw.threeDbHighHz!)} (3 dB below −0.60 dB)`],
        ['±1 dB corners', `< start, ${formatHz(bw.oneDbHighHz!)}`],
        ['Generator level', '+0.60 dBV in output-voltage mode: the level probe drives the DUT so the reference band reads 0 dBV'],
        ['Group delay', '368 µs at 100 Hz, 391 µs at 1 kHz, peaking at 1005 µs at the corner — 2.7 times its LF value'],
        ['DUT delay', '18.6 samples at 48 kHz (388 µs of bulk delay)'],
        ['Impulse', 'a pulse followed by a visible ring at about 2 kHz that takes a couple of milliseconds to die'],
        ['THD', '0.000 %; with Harmonics ticked, the generator’s 0.51 % bent by the ripple, then dropping steeply as each harmonic crosses 2 kHz'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — the ripple at 1/24-octave smoothing, the steep skirt, and a group-delay peak at the corner.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — the ripple point by point; put a few points between 500 Hz and 2 kHz to catch the extrema.</li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — the ring the ripple costs, side by side with the Butterworth's clean pulse.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — with Harmonics ticked, D2 and D3 each dip and rise with the ripple before they fall off the edge.</li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>Frequency response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Chirp start (Hz)', '2'],
        ['Resolution (Hz)', '0.37'],
        ['Right channel is reference', 'On'],
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
      :y2-max="1200"
      :y2-step="300"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Group delay (µs)"
      :height="280"
      caption="Expected Bode plot and group delay: the ripple is invisible at this scale, the skirt is twice the Butterworth's, and the delay spikes to a millisecond at the corner."
    />
    <p>
      The <strong>reference level</strong> reads −0.60 dB rather than 0: the passband-median
      reference takes the median over 200 Hz – 2 kHz, and on a filter whose passband ripples that
      median sits inside the ripple. The corners are then measured from −0.60 dB — which is why the
      −3 dB corner reads 2.08 kHz here, a shade above the 2 kHz where the response crosses −3 dB
      absolute — and the level probe drives the generator to +0.60 dBV so that the reference band
      reads the 0 dBV asked for. Neither is an error; both are what a reference band that is not
      flat does, and the Measurements panel quotes the level it used.
    </p>

    <h2>Impulse response</h2>
    <ManualChart
      :series="impulseSeries"
      :x-log="false"
      :x-min="-0.2"
      :x-max="4"
      :y-min="-0.5"
      :y-max="1.05"
      x-label="ms"
      y-label="relative to peak"
      :height="200"
      caption="Expected impulse response against the Butterworth's: the same pulse, then a ring near the corner frequency that the equal-ripple passband pays for."
    />
  </div>
</template>
