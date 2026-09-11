<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { CROSSOVER_DEPTH, CROSSOVER_WIDTH_VOLTS, crossover, thdVsAmplitude } from '../../sim/waveshapers'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11, THD vs amplitude at 1 kHz, 1 mV – 1 V: 0.065 % at 2 mV,
// 0.94 % at 8 mV, 3.44 % peak at 25.6 mV (−31.8 dBV), 0.89 % at 91 mV, 0.0915 % at 256 mV,
// 0.0015 % at 1 V; gain −1.93 dB at 1 mV rising to 0.00 dB by 100 mV; D2 zero throughout.
const curves = computed(() => thdVsAmplitude(crossover, 0.001, 1))
const peak = computed(() => curves.value.thd.reduce((a, b) => (b[1] > a[1] ? b : a)))
const gainInNotchDb = 20 * Math.log10(1 - CROSSOVER_DEPTH)

const thdSeries = computed<ManualSeries[]>(() => [
  { name: 'THD', points: curves.value.thd, color: 'gain' },
  { name: 'D3', points: curves.value.d3, color: 'right' },
  { name: 'D5', points: curves.value.d5, color: 'coherence' },
])
const thdMarkers = computed<ManualMarker[]>(() => [{ x: peak.value[0], label: `peak ${(20 * Math.log10(peak.value[0])).toFixed(0)} dBV`, color: 'amber' }])

const gainSeries = computed<ManualSeries[]>(() => [{ name: 'Gain (dB)', points: curves.value.gainDb, color: 'left' }])

const transferSeries = computed<ManualSeries[]>(() => {
  const points: [number, number][] = []
  for (let i = 0; i <= 300; i++) {
    const x = -0.1 + (0.2 * i) / 300
    points.push([x * 1000, (crossover(x) - x) * 1000])
  }
  return [{ name: 'Output minus input (mV)', points, color: 'left' }]
})
</script>

<template>
  <div>
    <h1>Class AB (crossover)</h1>

    <p>
      A class-AB output stage biased a little cold: a gain notch around zero,
      <code>y = x·(1 − a·e^(−x²/2w²))</code>, {{ CROSSOVER_DEPTH * 100 }} % deep and
      {{ CROSSOVER_WIDTH_VOLTS * 1000 }} mV wide, that every half-cycle passes through twice on its
      way across zero. Away from zero the stage is a straight wire.
    </p>
    <p>
      Its signature is that <strong>distortion gets worse as the level falls</strong> — the opposite
      of every other amplifier — because the notch is a fixed width and a smaller signal spends more
      of its cycle inside it. On a THD-vs-amplitude sweep that is a hump, peaking near
      <strong>{{ (20 * Math.log10(peak[0])).toFixed(0) }} dBV</strong> at a few percent. Below
      the peak the whole signal fits inside the notch, where the stage is merely linear at reduced
      gain, and THD falls again. The notch is symmetric, so there are odd harmonics only, and unity
      gain at any ordinary listening level: this is the amplifier that measures beautifully at
      1 W and sounds wrong at night.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['THD peak', `${peak[1].toFixed(2)} % at ${(peak[0] * 1000).toFixed(0)} mV rms (${(20 * Math.log10(peak[0])).toFixed(1)} dBV); the app reads 3.44 % at 25.6 mV`],
        ['THD at 1 V', `${curves.thd.at(-1)![1].toFixed(4)} % — effectively nothing`],
        ['THD at 1 mV', `${curves.thd[0]![1].toFixed(3)} %, falling again: the signal lives inside the notch`],
        ['Gain', `${gainInNotchDb.toFixed(2)} dB deep in the notch (1 − a), rising to 0.00 dB by 100 mV`],
        ['Harmonics', 'D3 largest, then D5, D7 … — odd only; D2 reads 0.000 %'],
        ['Frequency', 'the same at every frequency; the curve has no memory'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('thd-vs-amplitude')">THD vs amplitude</a> — the one it is for: the hump.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — flat at the level's own figure: 3.4 % at −32 dBV, 0.0015 % at 0 dBV.</li>
      <li><a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> — at −32 dBV a forest of odd harmonics; at 0 dBV nothing. Watch it appear as you lower the amplitude field.</li>
      <li>
        <a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — flat
        at 0 dB at 0 dBV; at −40 dBV flat at {{ gainInNotchDb.toFixed(1) }} dB. The only model whose
        gain depends on the drive level, which is what <em>Set generator by output voltage</em> was
        built to cope with.
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
        ['Display', 'Units %, THD, D3 and D5 checked; X axis dBV'],
      ]"
    />
    <ManualChart
      :series="thdSeries"
      :markers="thdMarkers"
      :x-min="0.0008"
      :x-max="1.25"
      :y-min="0.0001"
      :y-max="10"
      y-log
      x-label="V"
      y-label="%"
      :height="260"
      caption="Expected THD against level: a hump peaking near 28 mV, falling both ways — into the notch on the left, out of it on the right. D3 carries almost all of it; D5 follows a decade down."
    />
    <ManualChart
      :series="gainSeries"
      :x-min="0.0008"
      :x-max="1.25"
      :y-min="-2.5"
      :y-max="0.5"
      x-label="V"
      y-label="Gain (dB)"
      :height="180"
      caption="The same sweep's Magnitude trace: −1.9 dB at the bottom, where the whole signal sits inside the notch's reduced gain, climbing to 0 dB as the signal grows out of it."
    />
    <p>
      The hump is the whole lesson of this model, and its left side is the part real measurements
      miss: a sweep that starts at 100 mV sees only distortion falling with level, which looks like a
      well-behaved amplifier. Below the peak the falling THD is <em>not</em> improvement — the
      signal is now small enough to live entirely in the notch, where the stage is a linear amplifier
      of gain 0.8. The Magnitude trace says so: {{ gainInNotchDb.toFixed(1) }} dB at 1 mV, and that
      is the same 20 % the notch is deep. Compare the
      <button type="button" class="manual-link" @click="showPage('sim-triode')">SE triode</button>,
      whose line falls all the way down for the opposite reason.
    </p>

    <h2>The transfer curve</h2>
    <ManualChart
      :series="transferSeries"
      :x-log="false"
      :x-min="-100"
      :x-max="100"
      :y-min="-12"
      :y-max="12"
      x-label="input (mV)"
      y-label="output − input (mV)"
      :height="200"
      caption="What the stage takes from the signal, against the signal: nothing beyond ±60 mV, a 20 % bite in the middle. A 28 mV-peak sine spends most of its cycle on the slopes of that bite, which is where the hump comes from."
    />
    <p>
      On the FFT screen at −32 dBV the oscilloscope view shows the kink at every zero crossing if
      you zoom the vertical axis; the spectrum shows D3 around −30 dBc with D5, D7 and D9 marching
      down behind it. Raise the amplitude to −6 dBV and every harmonic drops into the noise, which is
      why a single 1 kHz THD figure at a comfortable level says nothing about crossover distortion,
      and the amplitude sweep does.
    </p>
  </div>
</template>
