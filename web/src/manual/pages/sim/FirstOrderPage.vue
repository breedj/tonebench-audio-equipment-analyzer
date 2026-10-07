<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { bandwidth, groupDelayCurve, impulseCurve, magnitudeCurve, phaseCurve } from '../../sim/curves'
import { FILTER_CORNER_HZ, firstOrderLowPass, secondOrderLowPass } from '../../sim/filters'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Every figure on this page is computed from the same prototype the device uses (see
// ../../sim/filters.ts), not typed in from a run: this filter's whole point is that its answer is
// known in closed form, so a hand-copied number would be the one thing on the page that could be
// wrong. The sampled model and -10·log10(1 + (f/fc)²) agree to three decimals at the corner.
const dut = firstOrderLowPass(48_000)
const second = secondOrderLowPass(48_000)
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
  { name: 'First order', points: phaseCurve(dut, 20, 22_800), color: 'gain' },
  { name: 'Second order', points: phaseCurve(second, 20, 22_800), color: 'left' },
])

const compareSeries = computed<ManualSeries[]>(() => [
  { name: 'First order', points: magnitudeCurve(dut, 100, 22_800, 400), color: 'gain' },
  { name: 'Second order', points: magnitudeCurve(second, 100, 22_800, 400), color: 'left' },
])

const impulseSeries = computed<ManualSeries[]>(() => [
  { name: 'Impulse (relative to peak)', points: impulseCurve(dut, 0.2, 2.5), color: 'left' },
])
</script>

<template>
  <div>
    <h1>First order LP (2 kHz)</h1>

    <p>
      One real pole at <strong>{{ FILTER_CORNER_HZ / 1000 }} kHz</strong> and nothing else:
      <em>H(s) = ω<sub>c</sub> / (s + ω<sub>c</sub>)</em>. −3.01 dB at the corner, 20 dB per decade
      above it, phase heading for −90°, no ring and no overshoot. It is the simplest thing in this
      app that has a frequency response at all.
    </p>
    <p>
      <strong>It is here to be checked against, not to be looked at.</strong> Every other model is
      something you measure and believe; this one has an answer written down in advance, so it is
      what to point an analysis at when the analysis is what you doubt. Set up as the next section
      describes, a pole–zero fit of it comes back with <strong>one pole at 2.00 kHz and no
      zeros</strong> — and if it does not, the fault is in the fit or in the measurement rather than
      in the DUT. The five-pole alignments cannot do that job: at order 5 a fit has enough freedom to
      track a curve it has misunderstood, which is exactly how a right-half-plane pair gets reported
      on an amplifier that has none. Its companion is the
      <button type="button" class="manual-link" @click="showPage('sim-second-order')">second order</button>,
      which adds the one thing missing here — a Q.
    </p>

    <ManualChart
      :series="compareSeries"
      :x-min="100"
      :x-max="24000"
      :y-min="-70"
      :y-max="5"
      x-label="Hz"
      y-label="Gain (dB)"
      :height="240"
      caption="The two orders at the same corner: 20 dB per decade against 40. Both are exactly −3.01 dB at 2 kHz, which is what makes the pair readable against each other."
    />

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Gain', `${db(100)} dB at 100 Hz, ${db(1000)} dB at 1 kHz, ${db(2000)} dB at 2 kHz, ${db(4000)} dB at 4 kHz, ${db(8000)} dB at 8 kHz, ${db(20000)} dB at 20 kHz`],
        ['Slope', '20 dB per decade — one pole. 6 dB per octave: every doubling above the corner costs the same 6 dB'],
        ['Phase', '−45.0° at the corner exactly, −26.6° at 1 kHz, −63.4° at 4 kHz, approaching −90°. On screen only with Restore removed delay ticked — see below'],
        ['−3 dB corner', `${formatHz(bw.threeDbHighHz!)} — see the note below on why this is not 2.00 kHz`],
        ['±1 dB corners', `< start, ${formatHz(bw.oneDbHighHz!)}`],
        ['Group delay', '79 µs at DC (1/ωₑ), falling with frequency — 63 µs at 1 kHz, 40 µs at the corner. A single pole has no delay peak'],
        ['Pole–zero fit', '1 pole at 2.00 kHz, no zeros, ζ = 1 (a real pole, so no Q) and DC gain −0.00 dB — with the fit set up as below'],
        ['Impulse', 'a one-sided exponential decaying with τ = 79 µs. No undershoot at all, which no higher order can manage'],
        ['THD', '0.000 %; with Harmonics ticked, the generator’s 0.51 % until its harmonics cross the corner'],
      ]"
    />

    <h2>Getting the one pole out of it</h2>
    <p>
      Two settings decide whether the fit returns the textbook answer, and both are worth
      understanding because they apply to every DUT — this one just makes them visible.
    </p>
    <ol>
      <li>
        <strong>Tick <em>Restore removed delay</em></strong> (Display rail). The chirp estimator
        removes a bulk delay from every record, and a rational model in <em>s</em> cannot represent a
        delay, so a fit of the uncorrected curve distorts its poles imitating one.
      </li>
      <li>
        <strong>Bring the frequency window in, to 10 Hz – 20 kHz.</strong> This matters more than it
        looks. The simulated filter is one pole in the <em>sampled</em> domain, which is one pole in
        <em>s</em> only below about a tenth of the sample rate; up near Nyquist the bilinear mapping
        bends it well away from a 20 dB-per-decade line. Fitted over the whole 10 Hz – 90.9 kHz band
        of a 96 kHz span at order 1 the pole still lands at 2.00 kHz, but the residual is
        <strong>1.91 dB rms</strong> and the panel says the model does not follow the measurement —
        and it is right: at the top of that band the measurement is not a one-pole response. Over
        10 Hz – 20 kHz the same fit reads <strong>0.057 dB rms</strong> and no warning. What is being
        measured there is the sampling, not the fitter.
      </li>
    </ol>
    <p>
      With both set, <strong>Choose order automatically</strong> picks order 1 and the table reads a
      single pole at 2.00 kHz, with a zero parked at 18 GHz and flagged <em>out of band</em> — the
      automatic order matches numerator to denominator, so it has a zero to spend and puts it where
      it does nothing. Untick it and set <strong>Zeros</strong> to 0 for the clean answer:
      H(s) = 0.9999 / (1 + s / 2π·2.00 kHz), which is the model, to four figures.
    </p>
    <p>
      Left at the <em>automatic</em> order over the full 96 kHz span, the fit instead reports order 3:
      the right pole at 2.00 kHz plus a pole pair at 280 kHz and two zeros, every one of them flagged
      out of band, with the panel advising a lower order. That is not a failure — it is the fit
      spending the freedom it was given on the part of the curve the sampling bent, and the panel
      saying so. It is worth doing once, because it is what every over-ordered fit on a real
      amplifier looks like.
    </p>

    <h2>Why the corners read wide</h2>
    <p>
      Both bandwidth figures come out above the textbook values — the −3 dB corner at
      {{ formatHz(bw.threeDbHighHz!) }} rather than 2.00 kHz, the ±1 dB one at
      {{ formatHz(bw.oneDbHighHz!) }} rather than 1.02 kHz — and nothing is wrong. The corners are
      measured down from the <strong>passband median</strong> over the reference band, and a
      first-order filter has no flat passband to take a median of: over the default 200 Hz – 2 kHz
      band the response is already sliding, so the reference itself sits about a decibel low and
      every threshold moves down with it.
    </p>
    <p>
      That is the reference doing its job rather than failing at it — on a real amplifier it is what
      keeps a transformer resonance from being mistaken for the passband — but on this model it is
      worth knowing. Set the <strong>Bandwidth reference</strong> band to something genuinely flat,
      20 – 200 Hz say, and the two corners come back to 2.00 kHz and 1.02 kHz, which is what the
      formula says they are. See
      <button type="button" class="manual-link" @click="showPage('frequency-response')">Frequency response</button>
      for the control.
    </p>

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — the corner, a clean 20 dB per decade, and a group delay that only falls.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — the same curve point by point, and the easiest check that the two paths agree.</li>
      <li><a href="#" @click.prevent="showPage('view-pole-zero')">Poles and zeros</a> — the reason this model exists: one pole on the negative real axis, and nothing else.</li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — a one-sided exponential; compare the second order's small undershoot.</li>
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
        ['Bandwidth reference', 'Passband median, 20 – 200 Hz (see above)'],
      ]"
    />
    <ManualChart
      :series="frSeries"
      :markers="frMarkers"
      :x-min="4"
      :x-max="24000"
      :y-min="-40"
      :y-max="5"
      :y2-min="0"
      :y2-max="100"
      :y2-step="20"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Group delay (µs)"
      :height="280"
      caption="Expected Bode plot and group delay: −3.01 dB at 2 kHz, 20 dB per decade above it, and a delay that starts at 79 µs and only falls — a single pole cannot make a delay peak."
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
      caption="Each pole is worth 90° of phase lag and 45° of it at the corner, which is the quickest way to count the order of something from its Bode plot alone."
    />
    <p>
      −45.0° at 2 kHz is exact, not approximate, and it is the one reading that identifies the
      order without reference to a slope: a pole contributes 90° in total and half of it at its own
      frequency. <strong>Tick <em>Restore removed delay</em> to see it:</strong> the chirp estimator
      takes a bulk delay out of every record, which here is about one sample and reads as 17° of
      phase at the corner — measured on this bench, the raw trace says −27.7° and the restored one
      −44.94°. On the
      <button type="button" class="manual-link" @click="showPage('view-nyquist')">Nyquist view</button>
      the same thing is a semicircle from 1 + j0 to the origin, passing through 0.5 − j0.5.
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
      caption="Expected impulse response: a one-sided exponential with τ = 79 µs, nothing before it and nothing below zero. Every higher order undershoots; this one cannot."
    />
  </div>
</template>
