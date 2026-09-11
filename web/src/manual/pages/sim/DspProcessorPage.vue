<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { bandwidth, impulseCurve, logSpace, magnitudeCurve, phaseCurve } from '../../sim/curves'
import { PROCESSOR_CORNER_HZ, PROCESSOR_LATENCY_MS, digitalProcessor } from '../../sim/models'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11 (24 kHz span, 1/24-octave smoothing): 0.00 dB to
// 15 kHz, −2.84 at 20 kHz, −23.2 at 22 kHz; ±1 dB "< 4.4 Hz – 19.27 kHz", −3 dB "< 4.4 Hz –
// 19.99 kHz"; recovered delay 576.76 samples, which is 12.02 ms at 48 kHz.
const dut = digitalProcessor(48_000)
const bw = bandwidth(dut, 4.4, 22_800, 200, 2000, 24)
const delaySamples48k = (PROCESSOR_LATENCY_MS * 48_000) / 1000

const frSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain (dB)', points: magnitudeCurve(dut, 4.4, 22_800, 400, 24), color: 'gain' },
  { name: 'Phase, delay removed (°)', points: phaseCurve(dut, 4.4, 22_800, 400, delaySamples48k), color: 'phase', axis: 'y2' },
])
const frMarkers = computed<ManualMarker[]>(() => [
  { y: -3, color: 'left' },
  { x: bw.threeDbHighHz!, label: `−3 dB ${formatHz(bw.threeDbHighHz!)}`, color: 'left' },
  { x: bw.oneDbHighHz!, label: `±1 dB ${formatHz(bw.oneDbHighHz!)}`, color: 'amber' },
])

// The phase as it would be with the delay *not* removed, wrapped to ±180°: 12 ms is 4320° per
// kilohertz, so drawn as points it is a solid band above a few hundred hertz — which is the point.
const rawPhaseSeries = computed<ManualSeries[]>(() => [
  {
    name: 'Phase with the delay restored (°)',
    points: logSpace(4.4, 22_800, 1500).map((f) => [f, dut.phaseDeg(f)] as const),
    color: 'phase',
    kind: 'points',
  },
])

const impulseSeries = computed<ManualSeries[]>(() => [{ name: 'Impulse (relative to peak)', points: impulseCurve(dut, 1, 14), color: 'left' }])
const impulseMarkers: ManualMarker[] = [{ x: PROCESSOR_LATENCY_MS, label: `${PROCESSOR_LATENCY_MS} ms`, color: 'amber' }]
</script>

<template>
  <div>
    <h1>DSP processor (12 ms)</h1>

    <p>
      A digital processor: a fourth-order Butterworth low-pass at <strong>{{ PROCESSOR_CORNER_HZ / 1000 }} kHz</strong>
      — maximally flat below it, −3 dB at the corner, falling 24 dB per octave above — and
      <strong>{{ PROCESSOR_LATENCY_MS }} ms of pure latency</strong>, {{ delaySamples48k }} samples at
      48 kHz, 2304 at 192 kHz. It is the only model with a delay. Everything else here answers
      within a sample, so on every other model the frequency-response screen's
      <em>DUT delay</em> readout is the chirp's arbitrary rotation and nothing more, and the impulse
      response's peak is always at zero. This one puts the impulse 12 ms in and gives the delay
      readout a real number to report.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Gain', '0.00 dB to 15 kHz, −0.13 dB at 18 kHz, −3.01 dB at 20 kHz, −24.7 dB at 22 kHz'],
        ['DUT delay', `${delaySamples48k} samples at a 24 kHz span (${PROCESSOR_LATENCY_MS}.0 ms); 1152 at 48 kHz; 2304 at 96 kHz`],
        ['Phase, delay removed', 'the filter’s own: 0° through the passband, turning to −180° at the corner'],
        ['Phase, delay restored', `a slope of ${(360 * PROCESSOR_LATENCY_MS / 1000 * 1000).toFixed(0)}° per kilohertz — 240 turns by 20 kHz`],
        ['−3 dB corners', `< start, ${formatHz(bw.threeDbHighHz!)}`],
        ['±1 dB corners', `< start, ${formatHz(bw.oneDbHighHz!)}`],
        ['Impulse', `a symmetric-looking pulse at t = ${PROCESSOR_LATENCY_MS} ms, ringing briefly at the corner`],
        ['THD', 'the generator’s 0.51 %, until the harmonics reach the corner'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — the one to see the delay readout and the <em>Restore removed delay</em> option on.</li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — an impulse that is not at zero, and what the gate does about it.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — the same brick wall, and a phase that wraps 240 times.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — flat at 0.51 % until 10 kHz, where H2 crosses the corner and D2 disappears; D3 goes at 6.7 kHz.</li>
      <li><a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> and <a href="#" @click.prevent="showPage('thd-vs-amplitude')">THD vs amplitude</a> — as the straight wire at 1 kHz; a 10 kHz tone loses its second harmonic.</li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>Frequency response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Chirp start (Hz)', '2'],
        ['Resolution (Hz)', '1.46'],
        ['Right channel is reference', 'On — the delay is only recovered against a measured reference'],
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
      :y-min="-30"
      :y-max="6"
      :y2-min="-180"
      :y2-max="180"
      :y2-step="90"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Phase (°)"
      :height="280"
      caption="Expected Bode plot: flat, then a fourth-order corner at 20 kHz; the phase is the filter's own once the screen has removed the 576-sample delay it recovered from the impulse."
    />
    <p>
      The progress line above the chart reads <strong>DUT delay {{ delaySamples48k }} samples</strong>
      (576.8 measured — the fourth-order filter puts its group delay of a fraction of a sample on top
      of the processor's 576). That figure is real only because the right channel is the reference:
      both spectra come from one capture, so the chirp's arbitrary rotation cancels and what is left
      is the DUT. Against the generated sweep the same readout is not shown at all, since it would be
      a random number between 0 and the record length.
    </p>
    <ManualChart
      :series="rawPhaseSeries"
      :x-min="4"
      :x-max="24000"
      :y-min="-180"
      :y-max="180"
      x-label="Hz"
      y-label="Phase (°)"
      :height="200"
      caption="The same phase with the delay left in — what Restore removed delay shows on the Nyquist view, drawn wrapped: 12 ms is 4.3° per hertz, so the trace wraps every 83 Hz and reads as a solid band above a few hundred hertz."
    />
    <p>
      That second picture is why the delay is removed by default. Twelve milliseconds is 240 full
      turns across the band; unwrapped it is a straight line to −86 400°, wrapped it is noise, and
      either way it hides the two hundred degrees the filter itself contributes. On the Nyquist and
      Nichols views <strong>Restore removed delay</strong> puts it back, and the contour becomes the
      physical one — a spiral of 240 turns — which is the right picture if the question is about
      loop stability with this processor inside the loop, and the wrong one for anything else.
    </p>

    <h2>Impulse response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Divide by', 'Right input channel — so the impulse lands at its true delay'],
        ['Gate: Before (ms) / After (ms)', '1 / 20'],
        ['Gate window', 'Hann'],
      ]"
    />
    <ManualChart
      :series="impulseSeries"
      :markers="impulseMarkers"
      :x-log="false"
      :x-min="-1"
      :x-max="14"
      :y-min="-0.4"
      :y-max="1.05"
      x-label="ms"
      y-label="relative to peak"
      :height="220"
      caption="Expected impulse response, drawn against the sweep's own time axis: nothing for 12 ms, then a 20 kHz-band-limited pulse with the fourth-order filter's short pre- and post-ring."
    />
    <p>
      The impulse screen puts the <em>peak</em> at t = 0 whatever its delay, so what you see on
      screen is the pulse at the origin with the gate either side of it, and the 12 ms is reported
      as the delay rather than drawn as empty space. The gate's <em>Before</em> then keeps what is
      ahead of the peak — here the filter's own pre-ring, a fraction of a millisecond of it — which
      is why that field is never zero: a band-limited impulse rings before itself. Divide by
      <strong>Nothing</strong> instead and the rotation is arbitrary, the peak is found just the
      same, and the delay is simply unknown; the gated response is identical either way.
    </p>
  </div>
</template>
