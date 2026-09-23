<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { bandwidth, groupDelayCurve, impulseCurve, magnitudeCurve } from '../../sim/curves'
import { chebyshevLowPass, ELLIPTIC, ellipticLowPass, FILTER_CORNER_HZ } from '../../sim/filters'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-21 (24 kHz span, 1/24-octave smoothing, right channel as
// reference, output-voltage mode at 0 dBV): −0.06 dB at 100 Hz, −0.83 at 500 Hz, −0.50 at 1 kHz,
// −0.93 at 2 kHz, −23.7 at 2.5 kHz, −42.8 at 3 kHz, −72 at the 3.47 kHz column, −60.1 at 4 kHz,
// −78 at 5 kHz, −76 at 5.32 kHz, −65 at 6 kHz, −60.2 at 8 kHz, −60.3 at 10 kHz, −71.5 at
// 20 kHz; reference level −0.60 dB; ±1 dB "< 4 Hz – 2.02 kHz", −3 dB "< 4 Hz – 2.07 kHz"; group
// delay 324 µs at 100 Hz, 338 at 1 kHz, 1248 at the corner; generator +0.60 dBV; DUT delay 17.4.
const dut = ellipticLowPass(48_000)
const bw = bandwidth(dut, 4, 22_800, 200, 2000, 24)
const zeroHz = ELLIPTIC.zeroOmegas.map((z) => z * FILTER_CORNER_HZ)

const frSeries = computed<ManualSeries[]>(() => [
  { name: 'Elliptic (dB)', points: magnitudeCurve(dut, 4, 22_800, 800, 24), color: 'gain' },
  { name: 'Chebyshev (dB)', points: magnitudeCurve(chebyshevLowPass(48_000), 4, 22_800, 400, 24), color: 'grey', dashed: true },
])
const frMarkers = computed<ManualMarker[]>(() => [
  { y: -60, label: '−60 dB stopband', color: 'amber' },
  { x: zeroHz[0]!, label: `null ${formatHz(zeroHz[0]!)}`, color: 'left' },
  { x: zeroHz[1]!, label: `null ${formatHz(zeroHz[1]!)}`, color: 'left' },
])

const delaySeries = computed<ManualSeries[]>(() => [{ name: 'Group delay (µs)', points: groupDelayCurve(dut, 20, 6000, 600), color: 'phase' }])
const delayMarkers: ManualMarker[] = [{ x: FILTER_CORNER_HZ, label: 'corner', color: 'grey', dashed: true }]

const impulseSeries = computed<ManualSeries[]>(() => [{ name: 'Impulse (relative to peak)', points: impulseCurve(dut, 0.2, 5), color: 'left' }])
</script>

<template>
  <div>
    <h1>Elliptic LP (2 kHz)</h1>

    <p>
      A fifth-order elliptic (Cauer) low-pass at <strong>{{ FILTER_CORNER_HZ / 1000 }} kHz</strong>
      with <strong>1 dB</strong> of passband ripple and a <strong>60 dB</strong> stopband. It is the
      only one of the four with zeros: two of them, at {{ formatHz(zeroHz[0]!) }} and
      {{ formatHz(zeroHz[1]!) }}, which put two nulls in the stopband and make the transition the
      steepest anything of this order can have — −60 dB by 4 kHz, where the
      <button type="button" class="manual-link" @click="showPage('sim-chebyshev')">Chebyshev</button>
      has reached −46 and the <button type="button" class="manual-link" @click="showPage('sim-butterworth')">Butterworth</button>
      −31. What the zeros cost is visible just as plainly: between and above the nulls the response
      climbs back to −60 dB and <em>stays</em> there all the way to the top of the band, where the
      all-pole designs keep falling at 100 dB per decade. The passband ripple is the Chebyshev's,
      running down from 0 dB at DC to −1 dB at the corner; the corner is the ripple edge, −1 dB.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Gain', '−0.06 dB at 100 Hz, −0.83 at 500 Hz, −0.50 at 1 kHz, −0.93 at 2 kHz, −23.7 at 2.5 kHz, −42.8 at 3 kHz, −60.1 at 4 kHz'],
        ['Stopband', `two nulls, at ${formatHz(zeroHz[0]!)} and ${formatHz(zeroHz[1]!)} — the trace bottoms at −72 and −76 dB in the nearest display columns, deeper than that between them — with −60 dB between and above: −65 at 6 kHz, −60.2 at 8 kHz, −60.3 at 10 kHz`],
        ['Reference level', '−0.60 dB — the passband median over 200 Hz – 2 kHz lands in the ripple'],
        ['−3 dB corners', `< start, ${formatHz(bw.threeDbHighHz!)} (3 dB below the −0.60 dB reference)`],
        ['±1 dB corners', `< start, ${formatHz(bw.oneDbHighHz!)}`],
        ['Generator level', '+0.60 dBV in output-voltage mode, so the reference band reads 0 dBV'],
        ['Group delay', '324 µs at 100 Hz, 338 µs at 1 kHz, peaking at 1.25 ms at the corner — the sharpest spike of the four'],
        ['DUT delay', '17.4 samples at 48 kHz (362 µs of bulk delay)'],
        ['Impulse', 'a pulse and a long ring near 2 kHz, the longest of the four'],
        ['THD', '0.000 %; with Harmonics ticked, D2 of a 1.75 kHz tone lands in the first null and disappears entirely'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — the two nulls and the flat −60 dB floor; with Noise on, the floor is the simulator's and the nulls fill in.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — a stepped run with points near 3.5 and 5.3 kHz reads the nulls only if a point lands on one; 1/24-octave resolution puts one within a few dB of the bottom.</li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — the longest ring of the four, which is the time-domain cost of the steepest skirt.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — with Harmonics ticked, D2 dips into the first null as the fundamental passes 1.74 kHz.</li>
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
        ['Smoothing', '1/24 octave — a null is one bin wide, and smoothing over a 1/24-octave band fills the bottom of it; None shows the nulls at their true depth'],
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
      x-label="Hz"
      y-label="Gain (dB)"
      :height="280"
      caption="Expected Bode plot against the Chebyshev's: the same ripple and corner, a steeper fall to the first null at 3.48 kHz, a second at 5.31 kHz, and a stopband that levels off at −60 dB where the Chebyshev keeps falling."
    />
    <p>
      The <strong>reference level</strong> reads −0.60 dB for the reason the Chebyshev's does:
      the passband median lands in the ripple, the corners are measured from there, and the level
      probe drives the generator 0.6 dB harder so that band reads 0 dBV. What is new here is the
      floor. With <strong>Noise</strong> off the stopband is exactly −60 dB, ripple included, and
      the nulls read tens of dB deeper than the columns show; with Noise on the simulator's own
      floor is around −100 dB at this record length, so the nulls still show, and a stopband at
      −60 dB is 40 dB clear of it — which is the whole point of quoting a stopband figure rather
      than a slope.
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
      :height="220"
      caption="Expected group delay: a slow rise through the passband, a 1.25 ms spike at the corner, then the delay collapsing through the first null — the response screens' Group delay column draws this once the right channel is the reference."
    />

    <h2>Impulse response</h2>
    <ManualChart
      :series="impulseSeries"
      :x-log="false"
      :x-min="-0.2"
      :x-max="5"
      :y-min="-0.6"
      :y-max="1.05"
      x-label="ms"
      y-label="relative to peak"
      :height="200"
      caption="Expected impulse response: the pulse, then a ring near the corner that takes several milliseconds to die — the longest of the four alignments."
    />
  </div>
</template>
