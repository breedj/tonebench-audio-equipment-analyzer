<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { CORE_CORNER_HZ, CORE_FLUX_LIMIT_VOLTS, coreSaturation, coreSaturationThdVsFrequency, harmonicsOfSine } from '../../sim/waveshapers'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11, THD vs frequency at 0 dBV, 20 Hz – 20 kHz: 4.87 % at
// 19 Hz, 2.52 % at 35 Hz, 0.75 % at 61 Hz, 0.184 % at 106 Hz, 0.039 % at 184 Hz, 0.0073 % at
// 320 Hz, nothing measurable above 500 Hz; D2 zero, THD entirely D3. The model below agrees to
// within 10 % of the figure at every point, the difference being the ADC's own soft clip on the
// 1.4 V peaks at the range the sweep chose.
const at0dBV = computed(() => coreSaturationThdVsFrequency(1, 20, 20_000))
const atMinus20 = computed(() => coreSaturationThdVsFrequency(0.1, 20, 20_000))
const at20Hz = computed(() => harmonicsOfSine(coreSaturation(48_000), Math.SQRT2, 20, 48_000, 8, Infinity, 0.25))

const thdSeries = computed<ManualSeries[]>(() => [
  { name: 'THD at 0 dBV', points: at0dBV.value.thd, color: 'gain' },
  { name: 'D3 at 0 dBV', points: at0dBV.value.d3, color: 'right', dashed: true },
  { name: 'THD at −20 dBV', points: atMinus20.value.thd, color: 'grey' },
])
const thdMarkers: ManualMarker[] = [{ x: CORE_CORNER_HZ, label: `core corner ${CORE_CORNER_HZ} Hz`, color: 'amber' }]

// THD against level at 20 Hz: the classic 2 dB per dB of a cubic, since the flux is proportional
// to the drive below the corner. Computed at 48 kHz — the model's flux corner is a frequency, not
// a sample count, so the rate does not enter, and a 20 Hz tone needs a quarter the record there.
const amplitudeSeries = computed<ManualSeries[]>(() => {
  const points: [number, number][] = []
  for (let i = 0; i <= 20; i++) {
    const vrms = 0.01 * Math.pow(10, (2 * i) / 20)
    points.push([vrms, harmonicsOfSine(coreSaturation(48_000), vrms * Math.SQRT2, 20, 48_000, 8, Infinity, 0.25).thdPercent])
  }
  return [{ name: 'THD at 20 Hz', points, color: 'gain' }]
})
</script>

<template>
  <div>
    <h1>Output transformer (saturating)</h1>

    <p>
      An output transformer whose core saturates. The linear response is flat; what is limited is
      the <strong>flux</strong>, which is the signal integrated — modelled as the signal through a
      one-pole low-pass at <strong>{{ CORE_CORNER_HZ }} Hz</strong> — and the core passes only
      <code>Φ·tanh(flux/Φ)</code> of it, Φ = {{ CORE_FLUX_LIMIT_VOLTS }} V. Below the corner the
      flux follows the signal and a loud tone drives the core into its curve; above it the flux falls
      6 dB per octave, and the cubic distortion that comes from it falls three times as fast —
      <strong>18 dB per octave</strong>. So third-harmonic distortion climbs steeply towards the
      bottom of the band and is absent above a couple of hundred hertz.
    </p>
    <p>
      This is the model for a THD-vs-<em>frequency</em> sweep, and the curve every
      transformer-coupled amplifier draws in real life. The
      <button type="button" class="manual-link" @click="showPage('sim-tube-amp')">Tube amp</button>
      model deliberately has no saturation, so its THD curve is the generator's flat 0.51 % bent by
      the response; this one is the missing half of a real output stage. Like the other three
      nonlinear models it switches the generator's harmonic stack off, so what you measure is its
      own.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['THD at 20 Hz, 0 dBV', `${at20Hz.thdPercent.toFixed(1)} % — almost entirely D3 (${at20Hz.harmonicsPercent[1]!.toFixed(1)} %), with D5 at ${at20Hz.harmonicsPercent[3]!.toFixed(2)} %; the app reads 4.87 % at 19 Hz`],
        ['Slope', '18 dB per octave falling: about 2.5 % at 35 Hz, 0.75 % at 60 Hz, 0.18 % at 105 Hz, 0.04 % at 185 Hz'],
        ['Above 500 Hz', 'below 0.001 %: nothing the sweep can measure'],
        ['Level dependence', 'as A² — 20 dB less drive is 40 dB less D3, so at −20 dBV the whole curve sits under 0.05 %'],
        ['Gain', '0.00 dB: the small-signal response is flat, and even at 20 Hz and 0 dBV the fundamental loses only 0.2 dB'],
        ['Harmonics', 'odd only; D2 reads 0.000 %, because the tanh is symmetric'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — the one it is for.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-amplitude')">THD vs amplitude</a> — set the frequency to 20 Hz and it climbs 2 dB per dB; leave it at 1 kHz and there is nothing to see, which is the point.</li>
      <li><a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> — a 1 kHz tone is clean; a 20 Hz tone at 0 dBV carries D3 at −27 dBc and D5 at −45.</li>
      <li>
        <a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — flat.
        But this is the model on which <em>Chirp start</em> earns its place: the bottom of a sweep
        started at 2 Hz drives the core ten times harder than the band you read, and the packets
        the distortion leaves in the deconvolution ripple the response above it. Raise the start
        to 8 Hz and watch the ripple go — the reason that control exists.
      </li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — the <em>Harmonics</em> view draws D3 rising towards the bottom of the band, read out of the same sweep.</li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>THD vs frequency</h2>
    <SettingsTable
      :rows="[
        ['Start / End (Hz)', '20 / 20000'],
        ['Steps per octave', '5'],
        ['Level mode', 'Generator voltage, 0 dBV — at the default −20 dBV the curve is under 0.05 % everywhere'],
        ['Left / Right', 'Left'],
        ['Averages per point', '4'],
        ['Adaptive resolution', 'On'],
        ['Measure noise floor first', 'On'],
        ['Display', 'Units %, THD and D3 checked'],
      ]"
    />
    <ManualChart
      :series="thdSeries"
      :markers="thdMarkers"
      :x-min="15"
      :x-max="25000"
      :y-min="0.0001"
      :y-max="10"
      y-log
      x-label="Hz"
      y-label="%"
      :height="260"
      caption="Expected THD against frequency at 0 dBV (black; D3 dashed underneath, indistinguishable) and at −20 dBV (grey): 18 dB per octave down from the bottom of the band, gone by 500 Hz. Below 300 Hz the −20 dBV curve is the same shape 40 dB lower; above it both are noise."
    />
    <p>
      The slope is the number to check: 18 dB per octave, i.e. THD falls by eight for every doubling
      of frequency — 4.7 % at 20 Hz, 0.75 % at 60 Hz (a factor of three in frequency, 27 in
      distortion, since 3³ = 27), 0.04 % at 185 Hz. It is exactly three times the 6 dB per octave the
      flux falls at, because the distortion is cubic in the flux. Above 500 Hz the trace is the
      simulated noise floor; the D3 of a 1 kHz tone is below one part in a million.
    </p>
    <p>
      The 0 dBV drive is what makes this visible, and it is a real choice: a transformer's
      saturation depends on volt-seconds, so a THD sweep run at a polite −20 dBV shows a clean
      transformer that is not, while the same core at full drive is at 5 %. A real measurement is
      made at the amplifier's rated output for exactly this reason.
    </p>

    <h2>THD vs amplitude, at 20 Hz</h2>
    <SettingsTable
      :rows="[
        ['Frequency (Hz)', '20'],
        ['Start / End (V)', '0.01 / 1'],
        ['Steps per octave', '5'],
        ['Averages per point', '4'],
      ]"
    />
    <ManualChart
      :series="amplitudeSeries"
      :x-min="0.008"
      :x-max="1.25"
      :y-min="0.0001"
      :y-max="10"
      y-log
      x-label="V"
      y-label="%"
      :height="220"
      caption="Expected THD against level at 20 Hz: a straight line rising 2 dB per dB — the cubic — bending over only as the tanh reaches its limit near the top."
    />
    <p>
      At 1 kHz the same sweep is a flat line at nothing. That contrast is the whole diagnosis of
      core saturation on a real amplifier: distortion that depends on level <em>and</em> on
      frequency together, and disappears entirely above the bass. The sweep's fixed 192 kHz /
      131072-point record puts 20 Hz at bin 14, comfortably clear of the fundamental's own skirts,
      so D3 at 60 Hz is read cleanly.
    </p>
  </div>
</template>
