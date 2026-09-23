<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { bandwidth, groupDelayCurve, impulseCurve, magnitudeCurve } from '../../sim/curves'
import { BESSEL_DELAY_AT_DC, besselLowPass, butterworthLowPass, chebyshevLowPass, ellipticLowPass, FILTER_CORNER_HZ } from '../../sim/filters'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-21 (24 kHz span, 1/24-octave smoothing, right channel as
// reference, output-voltage mode at 0 dBV): −0.01 dB at 100 Hz, −0.18 at 500 Hz, −0.72 at 1 kHz,
// −1.64 at 1.5 kHz, −3.00 at 2 kHz, −7.5 at 3 kHz, −14.5 at 4 kHz, −30.0 at 6 kHz, −43.8 at
// 8 kHz; reference level −0.87 dB; ±1 dB "< 4 Hz – 1.60 kHz", −3 dB "< 4 Hz – 2.24 kHz"; group
// delay 192.1 µs at 100 Hz, 192.9 at 1 kHz, 194.6 at 2 kHz, 192.5 at 2.5 kHz; generator settled
// at +0.86 dBV; DUT delay 8.9 samples.
const dut = besselLowPass(48_000)
const bw = bandwidth(dut, 4, 22_800, 200, 2000, 24)
const delayUs = (BESSEL_DELAY_AT_DC / (2 * Math.PI * FILTER_CORNER_HZ)) * 1e6

const delaySeries = computed<ManualSeries[]>(() => [
  { name: 'Bessel', points: groupDelayCurve(dut, 20, 6000), color: 'right' },
  { name: 'Butterworth', points: groupDelayCurve(butterworthLowPass(48_000), 20, 6000), color: 'gain' },
  { name: 'Chebyshev', points: groupDelayCurve(chebyshevLowPass(48_000), 20, 6000), color: 'left' },
  { name: 'Elliptic', points: groupDelayCurve(ellipticLowPass(48_000), 20, 6000), color: 'amber' },
])
const delayMarkers: ManualMarker[] = [{ x: FILTER_CORNER_HZ, label: 'corner', color: 'grey', dashed: true }]

const frSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain (dB)', points: magnitudeCurve(dut, 4, 22_800, 400, 24), color: 'gain' },
  { name: 'Group delay (µs)', points: groupDelayCurve(dut, 4, 22_800), color: 'phase', axis: 'y2' },
])
const frMarkers = computed<ManualMarker[]>(() => [
  { y: bw.referenceDb - 3, color: 'left' },
  { x: bw.threeDbHighHz!, label: `−3 dB ${formatHz(bw.threeDbHighHz!)}`, color: 'left' },
  { x: bw.oneDbHighHz!, label: `±1 dB ${formatHz(bw.oneDbHighHz!)}`, color: 'amber' },
])

const impulseSeries = computed<ManualSeries[]>(() => [
  { name: 'Bessel', points: impulseCurve(dut, 0.2, 2.5), color: 'right' },
  { name: 'Butterworth', points: impulseCurve(butterworthLowPass(48_000), 0.2, 2.5), color: 'grey', dashed: true },
])
</script>

<template>
  <div>
    <h1>Bessel LP (2 kHz)</h1>

    <p>
      A fifth-order Bessel low-pass, normalised to −3.01 dB at <strong>{{ FILTER_CORNER_HZ / 1000 }} kHz</strong>
      so it can be laid over the <button type="button" class="manual-link" @click="showPage('sim-butterworth')">Butterworth</button>.
      A Bessel alignment is designed for one thing: a <strong>maximally flat group delay</strong>,
      which is to say every frequency in the passband comes out delayed by the same
      <strong>{{ delayUs.toFixed(0) }} µs</strong> — {{ BESSEL_DELAY_AT_DC.toFixed(4) }}/ω<sub>c</sub> for
      this normalisation — so a waveform keeps its shape and a step passes without overshoot. It
      pays with the gentlest roll-off of the four: an octave above the corner it is 14 dB down where
      the Butterworth is 31, and it does not reach −3 dB abruptly but droops toward it from a
      decade below. It is the model for the response screens' <em>Group delay</em> and
      <em>Phase delay</em> columns, which read a flat line here and a peak at the corner on every
      other filter.
    </p>

    <ManualChart
      :series="delaySeries"
      :markers="delayMarkers"
      :x-min="20"
      :x-max="6000"
      :y-min="0"
      :y-max="1400"
      :y-step="200"
      x-label="Hz"
      y-label="Group delay (µs)"
      :height="260"
      caption="Group delay of the four alignments. The Bessel holds 193 µs to the corner and beyond; the Butterworth rises 54 % to its corner; the Chebyshev and the elliptic spike to a millisecond or more, which is what their steep skirts cost in the time domain."
    />

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Gain', '−0.01 dB at 100 Hz, −0.18 at 500 Hz, −0.72 at 1 kHz, −1.64 at 1.5 kHz, −3.00 at 2 kHz, −7.5 at 3 kHz, −14.5 at 4 kHz, −30.0 at 6 kHz, −44 at 8 kHz'],
        ['Group delay', `${delayUs.toFixed(0)} µs, flat: 192.1 µs at 100 Hz, 192.9 at 1 kHz, 194.6 at the corner, 192.5 at 2.5 kHz — within 1.5 % of its DC value well past the corner`],
        ['Phase delay', 'the same figure, since a constant group delay is a linear phase'],
        ['Reference level', '−0.87 dB — the response is already drooping inside the 200 Hz – 2 kHz reference band, so its median sits below 0'],
        ['−3 dB corners', `< start, ${formatHz(bw.threeDbHighHz!)} — measured 3 dB below the −0.87 dB reference, so it lands past the 2 kHz where the response crosses −3 dB absolute`],
        ['±1 dB corners', `< start, ${formatHz(bw.oneDbHighHz!)}`],
        ['DUT delay', '9 samples at 48 kHz — the 193 µs of bulk delay, the same number the group delay reads'],
        ['Impulse', 'the narrowest and cleanest pulse of the four: no ring at all, and no undershoot to speak of'],
        ['THD', '0.000 %; with Harmonics ticked, 0.51 % at the bottom of the band, easing off from a few hundred hertz as the harmonics droop'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — with the right channel as reference, the Group delay column reads a straight line at 193 µs.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — the same flat delay from the stepped phase, which carries the physical turns without an anchor.</li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — a symmetric-looking pulse with no ring; the picture of what a flat delay means.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — with Harmonics ticked, a gentle fall rather than the Butterworth's cliff.</li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>Frequency response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Chirp start (Hz)', '2'],
        ['Resolution (Hz)', '0.37'],
        ['Right channel is reference', 'On — without it the delay column shows only the excess over the bulk delay the screen removed, which for a Bessel is nearly nothing'],
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
      :y2-max="300"
      :y2-step="100"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Group delay (µs)"
      :height="280"
      caption="Expected Bode plot and group delay: a slow droop into a −3 dB corner and a gentle slope above it, under a delay that stays at 193 µs through the whole passband and only lets go an octave above the corner."
    />
    <p>
      The <strong>reference level</strong> reads −0.87 dB because the response is already
      falling inside the 200 Hz – 2 kHz reference band (−0.72 dB at 1 kHz), and the passband
      median is taken over linear frequency, which weights it toward the top of the band. The
      corners are then measured from that level, so the −3 dB row reads 2.24 kHz — 3 dB below
      −0.87 — rather than the 2.00 kHz where the curve crosses −3 dB absolute. Narrow the reference
      band to 200 – 500 Hz and the reference moves to −0.09 dB and the corner to 2.02 kHz; the
      Measurements panel lets you do that after the run.
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
      caption="Expected impulse response against the Butterworth's: a slightly narrower pulse with no undershoot and no ring — the time-domain face of a flat group delay."
    />
  </div>
</template>
