<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { TRIODE_K2, TRIODE_K3, TRIODE_RAIL_VOLTS, harmonicsOfSine, thdVsAmplitude, triode } from '../../sim/waveshapers'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11, THD vs amplitude at 1 kHz, 1 mV – 1 V: 0.0115 % at
// 16 mV, 0.173 % at 256 mV, 0.716 % at 1 V (D2 0.709, D3 0.100); THD vs frequency at 0 dBV a
// flat 0.716 % from 19 Hz to 17 kHz. The polynomial predicts 0.734 % at 1.02 V; the app's points
// sit 2 % below it throughout, which is the ADC's soft input saturation easing the peaks.
const curves = computed(() => thdVsAmplitude(triode, 0.001, 1))
const at1V = computed(() => harmonicsOfSine(triode, Math.SQRT2, 1000, 192_000))

const thdSeries = computed<ManualSeries[]>(() => [
  { name: 'THD', points: curves.value.thd, color: 'gain' },
  { name: 'D2', points: curves.value.d2, color: 'left' },
  { name: 'D3', points: curves.value.d3, color: 'right' },
  { name: 'k2·A/2 (paper)', points: [[0.001, 100 * (TRIODE_K2 * 0.001 * Math.SQRT2) / 2], [1, 100 * (TRIODE_K2 * Math.SQRT2) / 2]], color: 'grey', dashed: true },
])

// The transfer curve itself, ±1.5 V, against a straight line.
const transferSeries = computed<ManualSeries[]>(() => {
  const points: [number, number][] = []
  for (let i = 0; i <= 200; i++) {
    const x = -1.5 + (3 * i) / 200
    points.push([x, triode(x) - x])
  }
  return [{ name: 'Output minus input (V)', points, color: 'left' }]
})
</script>

<template>
  <div>
    <h1>SE triode</h1>

    <p>
      A single-ended triode stage: flat, with a soft asymmetric transfer curve
      <code>y = v + k₂v² − k₃v³</code>, k₂ = {{ TRIODE_K2 }}, k₃ = {{ TRIODE_K3 }}, applied to the
      input limited at ±{{ TRIODE_RAIL_VOLTS }} V. The square term is the triode: an asymmetric curve
      makes <strong>second</strong> harmonic, and its size grows in proportion to the drive. The
      small cubic is the compression every real stage has, and the rail is where it runs out of
      swing.
    </p>
    <p>
      This is the first model whose distortion depends on level, and therefore the first that gives a
      THD-vs-amplitude sweep anything to draw. Every linear model reads the generator's fixed 0.51 %
      at every level; selecting this one (or any of the three that follow) switches that stack
      <em>off</em>, so the left channel carries only what the model does and the right channel is a
      clean generator tap, as on a real bench. And its answer is on paper: for a polynomial,
      <strong>H2/H1 = k₂A/2</strong> and <strong>H3/H1 = k₃A²/4</strong> with A the peak drive, so
      H2 is 0.5 % at 1 V peak and climbs exactly 1 dB per dB of level.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Gain', '0.00 dB at any ordinary level: the curve has unit slope through the origin'],
        ['THD at 1 V rms (1.41 V peak)', `${at1V.thdPercent.toFixed(3)} % — D2 ${at1V.harmonicsPercent[0]!.toFixed(3)} %, D3 ${at1V.harmonicsPercent[1]!.toFixed(3)} %; the app reads 0.716 %`],
        ['THD at 100 mV rms', `${harmonicsOfSine(triode, 0.1 * Math.SQRT2, 1000, 192_000).thdPercent.toFixed(4)} % — a tenth of the level, a tenth of the distortion`],
        ['Slope', 'D2 rises 1 dB per dB of drive (20 dB per decade); D3 rises 2 dB per dB'],
        ['Frequency', 'the same at every frequency: the curve has no memory'],
        ['Right channel', '0.000 % — the stack is off, and the generator is clean'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('thd-vs-amplitude')">THD vs amplitude</a> — the one it is for: a straight line.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — flat, at whatever level you choose; the level, not the frequency, sets it.</li>
      <li><a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> — a clean H2-dominant spectrum that tracks the amplitude field as you type.</li>
      <li>
        <a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> and
        <a href="#" @click.prevent="showPage('impulse-response')">impulse response</a> — a flat line;
        but the sweep's distortion packets now exist, so the impulse screen's <em>Harmonics</em> view
        draws this model's D2 and D3 and the <em>gate the sweep's harmonic packets</em> option finally
        has something to remove.
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
        ['Display', 'Units %, THD, D2 and D3 checked'],
      ]"
    />
    <ManualChart
      :series="thdSeries"
      :x-min="0.0008"
      :x-max="1.25"
      :y-min="0.0001"
      :y-max="10"
      y-log
      x-label="V"
      y-label="%"
      :height="260"
      caption="Expected THD against level: THD and D2 on one straight line rising 1 dB per dB, D3 two decades below rising twice as fast. The dashed grey is k₂A/2 on paper; the model sits on it."
    />
    <p>
      The straight line is the single-ended signature, and it is what a THD-vs-power plot of a real
      triode stage looks like until the stage runs out of swing. Two places the measured points leave
      the line, both the analyzer's and not the model's: at the bottom, below about 5 mV, the
      measured THD flattens at 0.02 % and then falls more slowly — the harmonics have met the
      simulated noise floor, which the <strong>Measure noise floor first</strong> pass draws as a
      separate trace so you can see it happen; and across the top decade the points sit about 2 %
      under the model, because the simulated ADC's soft input saturation is easing the peaks of a
      1.4 V-peak signal on the range the sweep selected. On the right channel, if you enable it, THD
      reads 0.000 % — the generator is clean once the stack is off.
    </p>

    <h2>The transfer curve</h2>
    <ManualChart
      :series="transferSeries"
      :x-log="false"
      :x-min="-1.5"
      :x-max="1.5"
      :y-min="-0.03"
      :y-max="0.03"
      x-label="input (V)"
      y-label="output − input (V)"
      :height="200"
      caption="What the stage adds to the signal, against the signal: a parabola (k₂v²) with a small cubic tilt. Asymmetric about zero — larger for positive swings than negative — which is what makes even harmonics."
    />
    <p>
      The oscilloscope view on the FFT screen shows this as a sine whose positive half is very
      slightly taller than its negative half; you will not see it by eye at any level the sweep uses,
      which is the usual state of affairs with 0.5 % distortion and why the spectrum, not the
      waveform, is where distortion is read.
    </p>

    <h2>THD vs frequency</h2>
    <SettingsTable
      :rows="[
        ['Start / End (Hz)', '20 / 20000'],
        ['Steps per octave', '5'],
        ['Level mode', 'Generator voltage, 0 dBV'],
        ['Left / Right', 'Left'],
        ['Averages per point', '4'],
        ['Adaptive resolution', 'On'],
      ]"
    />
    <p>
      A flat line at <strong>0.716 %</strong> from 20 Hz to 20 kHz (the app measured that figure at
      every one of its 50 points), with D2 at 0.709 % and D3 at 0.100 %. The curve has no memory, so
      frequency cannot enter into it; only the level does, and at −20 dBV the same sweep reads a flat
      0.072 %. The one thing frequency changes is which harmonics fit below the band's top: above
      31 kHz's third of 94 kHz, D3 leaves the plot, and D2 at 47 kHz — which this 20 kHz sweep never
      reaches.
    </p>
  </div>
</template>
