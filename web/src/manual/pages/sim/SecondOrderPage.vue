<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { bandwidth, groupDelayCurve, impulseCurve, magnitudeCurve, phaseCurve } from '../../sim/curves'
import { butterworthLowPass, FILTER_CORNER_HZ, firstOrderLowPass, secondOrderLowPass } from '../../sim/filters'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Computed from the same prototype the device uses, for the reason the first-order page gives: the
// value of these two models is that their answer is known in advance, so nothing here is typed in.
const dut = secondOrderLowPass(48_000)
const first = firstOrderLowPass(48_000)
const bw = bandwidth(dut, 4, 22_800, 200, 2000, 24)

const db = (hz: number) => dut.magnitudeDb(hz).toFixed(2)

const frSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain (dB)', points: magnitudeCurve(dut, 4, 22_800, 400, 24), color: 'gain' },
  { name: 'Group delay (µs)', points: groupDelayCurve(dut, 4, 22_800), color: 'phase', axis: 'y2' },
])
const frMarkers = computed<ManualMarker[]>(() => [
  { y: -3, color: 'left' },
  { x: FILTER_CORNER_HZ, label: `−3 dB ${formatHz(FILTER_CORNER_HZ)}`, color: 'left' },
])

const phaseSeries = computed<ManualSeries[]>(() => [
  { name: 'Second order', points: phaseCurve(dut, 20, 22_800), color: 'gain' },
  { name: 'First order', points: phaseCurve(first, 20, 22_800), color: 'left' },
])

const compareSeries = computed<ManualSeries[]>(() => [
  { name: 'Second order', points: magnitudeCurve(dut, 100, 22_800, 400), color: 'gain' },
  { name: 'First order', points: magnitudeCurve(first, 100, 22_800, 400), color: 'left' },
  { name: 'Butterworth, 5th order', points: magnitudeCurve(butterworthLowPass(48_000), 100, 22_800, 400), color: 'amber' },
])

const impulseSeries = computed<ManualSeries[]>(() => [
  { name: 'Impulse (relative to peak)', points: impulseCurve(dut, 0.2, 2.5), color: 'left' },
])
</script>

<template>
  <div>
    <h1>Second order LP (2 kHz)</h1>

    <p>
      One conjugate pole pair at <strong>{{ FILTER_CORNER_HZ / 1000 }} kHz</strong> with
      <strong>Q = 0.707</strong> — a second-order Butterworth, <em>ζ = 1/√2</em>. −3.01 dB at the
      corner, 40 dB per decade above it, phase through −90° at the corner to −180°. Two poles and
      nothing else in the path.
    </p>
    <p>
      <strong>The Q is the point.</strong> Like the
      <button type="button" class="manual-link" @click="showPage('sim-first-order')">first order</button>,
      this model exists to be checked against rather than looked at — but where that one has no Q to
      report, this one has a value that was <em>chosen</em> rather than measured, and three separate
      readouts have to agree with it. The <strong>Poles and zeros</strong> panel must say Q 0.707 and
      ζ 0.707 at 2.00 kHz; the <strong>s-plane map</strong> must put the pair on the 45° line, since
      ζ reads as the cosine of the angle from the negative real axis; and a two-pole fit must have
      nothing left over. A number that comes back 0.71 is right, and one that comes back 1.4 means
      something has inverted a ratio.
    </p>

    <ManualChart
      :series="compareSeries"
      :x-min="100"
      :x-max="24000"
      :y-min="-100"
      :y-max="5"
      x-label="Hz"
      y-label="Gain (dB)"
      :height="260"
      caption="Order counted off the slope: 20, 40 and 100 dB per decade, all through −3.01 dB at the same corner. Any of the three is a Butterworth — it is the order that differs."
    />

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Gain', `${db(100)} dB at 100 Hz, ${db(1000)} dB at 1 kHz, ${db(2000)} dB at 2 kHz, ${db(4000)} dB at 4 kHz, ${db(8000)} dB at 8 kHz, ${db(20000)} dB at 20 kHz`],
        ['Slope', '40 dB per decade — two poles, 12 dB per octave'],
        ['Phase', '−90.0° at the corner exactly, −43.3° at 1 kHz, −136.7° at 4 kHz, approaching −180°. On screen only with Restore removed delay ticked — measured here, −89.82° at the corner'],
        ['−3 dB corner', `${formatHz(bw.threeDbHighHz!)} against the default reference band; 2.00 kHz exactly against a flat one — see the first-order page`],
        ['±1 dB corners', `< start, ${formatHz(bw.oneDbHighHz!)}`],
        ['Group delay', '112 µs at DC (√2/ωₑ), peaking at about 136 µs just under the corner, then falling. The smallest model with a delay peak at all'],
        ['Pole–zero fit', 'A pole pair at 2.00 kHz, Q = 0.707, ζ = 0.707, in the left half-plane, DC gain −0.00 dB — measured, with the fit set up as below'],
        ['Impulse', 'a single pulse with one small undershoot of about 4 % and no ring — Q 0.707 is the most damping a second order can have without the knee rounding off'],
        ['THD', '0.000 %; with Harmonics ticked, the generator’s 0.51 % until its harmonics cross the corner'],
      ]"
    />

    <h2>What the fit returns</h2>
    <p>
      Set up the way the
      <button type="button" class="manual-link" @click="showPage('sim-first-order')">first-order page</button>
      describes — <strong>Restore removed delay</strong> ticked and the frequency window brought in
      to 10 Hz – 20 kHz, so the bilinear mapping near Nyquist is not being fitted —
      <strong>Choose order automatically</strong> picks order 2 and the table reads a
      <strong>pole pair at 2.00 kHz with Q 0.707</strong>, at a residual of <strong>0.009 dB
      rms</strong>. Untick it and set Zeros to 0 and the same pair comes back at 0.112 dB rms.
    </p>
    <p>
      The automatic order also places a zero pair at 70.6 kHz, flagged <em>RHP, out of band</em>, and
      it is worth knowing why the residual is <em>lower</em> with it than without: those zeros are
      four decades of extrapolation away from anything measured, and what they are doing is
      compensating the last fraction of a decibel of sampling warp at the top of the window. A root
      the panel marks out of band is a root the data did not constrain — the figure to read here is
      the pole pair, and the one to check is its Q.
    </p>

    <h2>Why Q = 1/√2 and not something else</h2>
    <p>
      It is the damping at which a second-order low-pass is <em>maximally flat</em>: any higher Q
      lifts a bump before the corner, any lower rounds the knee off early, and only this value puts
      the response exactly −3.01 dB at ω<sub>c</sub> with no peak anywhere. That makes it the one
      second-order damping a reader recognises on sight, which is what a check needs. It is also the
      boundary worth knowing on the decay charts: at Q 0.707 there is no ring to decay, where the
      <button type="button" class="manual-link" @click="showPage('sim-bandpass')">Bandpass</button>
      (Q 20) and the
      <button type="button" class="manual-link" @click="showPage('sim-marginally-stable')">Marginally stable</button>
      model (Q 500) exist to show what happens when a pole pair comes closer to the jω axis.
    </p>

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('view-pole-zero')">Poles and zeros</a> — the reason this model exists: one pair, Q 0.707, on the 45° line.</li>
      <li><a href="#" @click.prevent="showPage('view-s-plane')">|H(s)| surface</a> — two poles and nothing else, which is the simplest surface that is not a single spike.</li>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — 40 dB per decade, and the first delay peak in this group.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — the same curve point by point.</li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — one small undershoot and no ring.</li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>Frequency response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Chirp start (Hz)', '2'],
        ['Resolution (Hz)', '0.37'],
        ['Right channel is reference', 'On — the delay columns then carry the filter’s own delay rather than only its excess'],
        ['Smoothing', '1/24 octave'],
        ['Averages', '1'],
        ['Bandwidth reference', 'Passband median, 20 – 200 Hz'],
      ]"
    />
    <ManualChart
      :series="frSeries"
      :markers="frMarkers"
      :x-min="4"
      :x-max="24000"
      :y-min="-70"
      :y-max="5"
      :y2-min="0"
      :y2-max="200"
      :y2-step="50"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Group delay (µs)"
      :height="280"
      caption="Expected Bode plot and group delay: −3.01 dB at 2 kHz, 40 dB per decade above it, and a delay that rises from 112 µs to about 136 µs before the corner — the gentlest version of the peak the fifth-order alignments exaggerate."
    />

    <h2>Phase</h2>
    <ManualChart
      :series="phaseSeries"
      :x-min="20"
      :x-max="24000"
      :y-min="-200"
      :y-max="10"
      x-label="Hz"
      y-label="Phase (°)"
      :height="220"
      caption="Two poles are 180° in total and 90° of it at the corner — exactly twice the first order's, at every frequency where both are far from their corner."
    />
    <p>
      On the <button type="button" class="manual-link" @click="showPage('view-nyquist')">Nyquist view</button>
      this is the contour that starts at 1 + j0, crosses the negative imaginary axis at the corner
      (|G| = 0.707 at −90°) and spirals into the origin — the clearest small example of what a
      complex-plane view is for, and the one case where you can check the picture against arithmetic
      done on paper.
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
      caption="Expected impulse response: a single pulse with one shallow undershoot and no ring. Compare the Chebyshev, whose ripple buys a visible one."
    />
  </div>
</template>
