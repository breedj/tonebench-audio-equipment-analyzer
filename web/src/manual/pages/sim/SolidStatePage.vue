<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { CLIP_K3, CLIP_KNEE_FRACTION, CLIP_RAIL_VOLTS, clipper, harmonicsOfSine, thdVsAmplitude } from '../../sim/waveshapers'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11, THD vs amplitude at 1 kHz, 1 mV – 1 V: 0.0001 % at
// 256 mV, 0.0009 % at 676 mV, 2.47 % at 754 mV, 8.26 % at 869 mV, 13.4 % at 1 V; gain 0.00 dB to
// 676 mV, −0.17 at 754 mV, −0.85 at 869 mV, −1.74 at 1 V; D2 zero throughout.
const curves = computed(() => thdVsAmplitude(clipper, 0.001, 1))
const railDbv = 20 * Math.log10(CLIP_RAIL_VOLTS / Math.SQRT2)
const kneeVrms = (CLIP_RAIL_VOLTS * CLIP_KNEE_FRACTION) / Math.SQRT2
const at1V = computed(() => harmonicsOfSine(clipper, Math.SQRT2, 1000, 192_000))
const at500mV = computed(() => harmonicsOfSine(clipper, 0.5 * Math.SQRT2, 1000, 192_000))

const thdSeries = computed<ManualSeries[]>(() => [
  { name: 'THD', points: curves.value.thd, color: 'gain' },
  { name: 'D3', points: curves.value.d3, color: 'right' },
])
const thdMarkers: ManualMarker[] = [
  { x: kneeVrms, label: `knee ${(kneeVrms * 1000).toFixed(0)} mV`, color: 'amber' },
  { x: CLIP_RAIL_VOLTS / Math.SQRT2, label: `rail ${railDbv.toFixed(0)} dBV`, color: 'left' },
]

const transferSeries = computed<ManualSeries[]>(() => {
  const points: [number, number][] = []
  for (let i = 0; i <= 300; i++) {
    const x = -1.5 + (3 * i) / 300
    points.push([x, clipper(x)])
  }
  return [
    { name: 'Output (V)', points, color: 'left' },
    { name: 'Unity', points: [[-1.5, -1.5], [1.5, 1.5]], color: 'grey', dashed: true },
  ]
})
</script>

<template>
  <div>
    <h1>Solid state (clips at −3 dBV)</h1>

    <p>
      A solid-state amplifier: flat and nearly distortionless — a cubic term of
      k₃ = {{ CLIP_K3 }}, about −100 dBc at 1 V peak — until it meets its supply rail at
      <strong>±{{ CLIP_RAIL_VOLTS }} V peak</strong>, where it clips with a short soft knee starting
      at {{ CLIP_KNEE_FRACTION * 100 }} % of the rail. One volt peak is
      <strong>{{ railDbv.toFixed(0) }} dBV</strong> for a sine, which is where the name comes from,
      and the default 1 mV – 1 V amplitude sweep meets it exactly at its top.
    </p>
    <p>
      This is the classic THD-versus-power plot: a floor, and then a wall. Everything below the knee
      is the residual cubic and the analyzer's own noise; everything above it is the rail, and the
      transition takes a tenth of a decibel of drive. It is the model for the top end of the
      amplitude sweep, and the counterpart of the
      <button type="button" class="manual-link" @click="showPage('sim-triode')">SE triode</button>, which has no wall and no floor, only a slope.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['THD below the knee', `${at500mV.thdPercent.toFixed(5)} % at 500 mV rms — the cubic, at k₃A²/4`],
        ['THD at 1 V rms', `${at1V.thdPercent.toFixed(1)} % (D3 ${at1V.harmonicsPercent[1]!.toFixed(1)} %, D5 ${at1V.harmonicsPercent[3]!.toFixed(1)} %); the app reads 13.4 %`],
        ['Where it turns', `${(kneeVrms * 1000).toFixed(0)} mV rms: the last point clean, the next one at 2–3 %`],
        ['Gain', `0.00 dB to the knee, −1.7 dB at 1 V rms as the peaks flatten`],
        ['Harmonics', 'odd only — the rail is symmetric — with D3, D5, D7 all rising together past the knee'],
        ['Frequency', 'the same at every frequency; the curve has no memory'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('thd-vs-amplitude')">THD vs amplitude</a> — the one it is for: floor, then wall.</li>
      <li><a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> — nothing at −6 dBV; step the amplitude to −3 dBV and the odd harmonics arrive all at once, and the oscilloscope shows the flat tops.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — flat at whatever the level gives: 0.0001 % at −6 dBV, 13 % at 0 dBV, the same at every frequency.</li>
      <li>
        <a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — flat
        at 0 dB at any level under the rail; drive the chirp at 0 dBV and its peaks clip, which reads
        as a gain of −1.7 dB with a coherence that sags, and <em>Set generator by output voltage</em>
        settles the drive just under the rail because that is where its probe finds 0 dBV out.
      </li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>THD vs amplitude</h2>
    <SettingsTable
      :rows="[
        ['Frequency (Hz)', '1000'],
        ['Start / End (V)', '0.001 / 1'],
        ['Steps per octave', '5'],
        ['Left / Right', 'Left'],
        ['Averages per point', '4'],
        ['Measure noise floor first', 'On'],
        ['Display', 'Units %, THD and D3 checked; X axis dBV'],
      ]"
    />
    <ManualChart
      :series="thdSeries"
      :markers="thdMarkers"
      :x-min="0.0008"
      :x-max="1.25"
      :y-min="0.00001"
      :y-max="100"
      y-log
      x-label="V"
      y-label="%"
      :height="260"
      caption="Expected THD against level: a floor rising gently as A² (the cubic) until the knee at 686 mV rms, then a wall to 14 % at 1 V. The app's points sit on it to within a percent of THD at the top."
    />
    <p>
      Two things about the floor. The model's own floor is the cubic and rises 2 dB per dB —
      0.00001 % at 100 mV, 0.0009 % at 676 mV — but the <em>measured</em> floor below about 30 mV is
      the simulated noise instead: 0.02 % at 1 mV, falling 1 dB per dB as the tone rises out of it.
      The <strong>Measure noise floor first</strong> pass draws that as its own trace so the two can
      be told apart, which on this model they cannot be otherwise. And the wall really is a wall:
      between the sweep's 676 mV and 754 mV points THD goes from 0.0009 % to 2.5 %, and the gain
      readout drops from 0.00 to −0.17 dB — the first sign of clipping on a level meter is always a
      fraction of a decibel of compression, well after the spectrum has filled with odd harmonics.
    </p>

    <h2>The transfer curve</h2>
    <ManualChart
      :series="transferSeries"
      :x-log="false"
      :x-min="-1.5"
      :x-max="1.5"
      :y-min="-1.2"
      :y-max="1.2"
      x-label="input (V)"
      y-label="output (V)"
      :height="220"
      caption="Output against input: a straight line to ±0.97 V, then a tanh into the ±1 V rail. The cubic below the knee is invisible at this scale, as it should be at −100 dBc."
    />
    <p>
      On the FFT screen, set the amplitude to −4 dBV and then to −2 dBV and watch the oscilloscope
      view: at −4 dBV (0.89 V peak) the sine is perfect; at −2 dBV (1.12 V peak) its tops are flat
      at ±1 V and the spectrum has a full comb of odd harmonics, D3 at about −20 dBc. The
      <em>Level</em> tile reads only 1.5 dB below what was asked for, which is the compression
      figure above and the reason clipping is found on a spectrum analyser and not on a voltmeter.
    </p>
  </div>
</template>
