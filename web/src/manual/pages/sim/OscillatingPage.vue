<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { noiseFloorDbvPerBin } from '../../sim/curves'
import { OSCILLATION_POLE_HZ, OSCILLATION_RAIL_VOLTS, oscillatorOutput, oscillatorSpectrum } from '../../sim/oscillator'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11: a 24 kHz-span chirp at −20 dBV against this model reads
// +33 dB of "gain" from 1000 to 1017 Hz and 0.0 dB elsewhere, coherence 1.000 with one average
// (there is nothing to disagree with itself yet), recovered delay 0.000 samples.
const FFT_RATE = 192_000
const spectrum = computed(() => oscillatorSpectrum(FFT_RATE))
const railDbv = 20 * Math.log10(OSCILLATION_RAIL_VOLTS / Math.SQRT2)
const floorDbv = noiseFloorDbvPerBin(FFT_RATE, 65536)

const fftSeries = computed<ManualSeries[]>(() => [
  { name: 'The oscillation and its harmonics', points: spectrum.value.lines.map((l) => [l.freqHz, l.dbv] as const), color: 'right', kind: 'stem' },
  { name: 'Noise floor (per bin)', points: [[5, floorDbv + 6], [300, floorDbv], [FFT_RATE / 2, floorDbv]], color: 'grey' },
])

// The first 600 ms of the waveform from the seed, showing the wind-up to the rail.
const windUp = computed<ManualSeries[]>(() => {
  const fs = 48_000
  const y = oscillatorOutput(fs, 0.6, 0)
  const points: [number, number][] = []
  for (let i = 0; i < y.length; i += 8) points.push([(i * 1000) / fs, y[i]!])
  return [{ name: 'Output (V)', points, color: 'right' }]
})
const railMarkers: ManualMarker[] = [
  { y: OSCILLATION_RAIL_VOLTS, label: '+rail', color: 'grey' },
  { y: -OSCILLATION_RAIL_VOLTS, label: '−rail', color: 'grey' },
]

// What the chirp screen makes of it: unity everywhere, a spike where the tone sits.
const frSeries = computed<ManualSeries[]>(() => {
  const f0 = spectrum.value.fundamentalHz
  const points: [number, number][] = []
  for (let i = 0; i <= 400; i++) {
    const f = 4 * Math.pow(10, (i / 400) * Math.log10(22_800 / 4))
    // The tone's energy against the chirp's per-bin energy: about +33 dB, spread over the bins the
    // analysis and its smoothing touch; drawn as the measured plateau.
    points.push([f, Math.abs(f - f0) < 10 ? 33 : 0])
  }
  return [{ name: 'Gain (dB), as measured', points, color: 'gain' }]
})
</script>

<template>
  <div>
    <h1>Oscillating</h1>

    <p>
      An amplifier that has gone the rest of the way past
      <button type="button" class="manual-link" @click="showPage('sim-marginally-stable')">marginally stable</button>: its pole pair sits <em>outside</em> the unit circle, so it sings on its own, at about
      <strong>{{ spectrum.fundamentalHz.toFixed(0) }} Hz</strong>, clipping against a
      <strong>±{{ OSCILLATION_RAIL_VOLTS }} V</strong> supply rail. The through path is unity — an
      amplifier that has gone unstable still amplifies — and the oscillation is <em>added</em> to
      whatever the generator sends.
    </p>
    <p>
      It is the only model whose output is not a response to its input, and that is what it is for.
      Every measurement in the app assumes otherwise: sweep deconvolution attributes the tone to the
      stimulus and reports it as gain, coherence collapses where it sits, and the input ranger steps
      for a signal nobody asked for. Expect every measurement to be wrong in a specific, recognisable
      way — that is the demonstration, and knowing the shape of it is what lets you recognise a real
      amplifier doing the same thing on the bench.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Frequency', `${spectrum.fundamentalHz.toFixed(1)} Hz at 192 kHz — about 1 % above the ${OSCILLATION_POLE_HZ} Hz pole, because clipping drops the loop gain to what sustains oscillation and the frequency moves to where the phase still closes`],
        ['Level', `${railDbv.toFixed(1)} dBV: ${OSCILLATION_RAIL_VOLTS} V peak, barely clipped, so nearly a sine`],
        ['Harmonics', `odd only — H3 about ${(spectrum.lines[2]!.dbv - spectrum.lines[0]!.dbv).toFixed(0)} dBc, H5 ${(spectrum.lines[4]!.dbv - spectrum.lines[0]!.dbv).toFixed(0)} dBc — since the clip is symmetric`],
        ['Start-up', 'about half a second from rest to the rail at 48 kHz, two seconds at 192 kHz'],
        ['Chirp gain', '+33 dB in a 20 Hz band around the tone at −20 dBV drive, 0.0 dB elsewhere'],
        ['Coherence', '1.000 with one average; collapses to a few hundredths around the tone with two or more'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> — where you can see what it actually is.</li>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — what a free-running tone does to a deconvolution.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> and <a href="#" @click.prevent="showPage('thd-vs-amplitude')">THD vs amplitude</a> — the harmonic analyzer finds a tone that is not a harmonic of anything.</li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — the deconvolved record carries a 1 kHz tone from end to end, and the decay surfaces show a ridge that never decays at all.</li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>FFT analysis</h2>
    <SettingsTable
      :rows="[
        ['Generator', 'Both channel checkboxes off — nothing driven, so the oscillation stands alone'],
        ['Left / Right', 'Both'],
        ['Sample rate', '192000'],
        ['FFT size', '65536'],
        ['Window', 'Hann'],
        ['Averaging', 'Off'],
        ['Simulator', 'Noise on, Hum off'],
      ]"
    />
    <ManualChart
      :series="windUp"
      :markers="railMarkers"
      :x-log="false"
      :x-min="0"
      :x-max="600"
      :y-min="-0.6"
      :y-max="0.6"
      x-label="ms after start"
      y-label="V"
      :height="200"
      caption="What the oscilloscope view shows in the first second after RUN at 48 kHz: the tone growing from the seed until the rail catches it, then a limit cycle at the rail. At 192 kHz the same wind-up takes about two seconds."
    />
    <ManualChart
      :series="fftSeries"
      :x-min="100"
      :x-max="96000"
      :y-min="-160"
      :y-max="10"
      x-label="Hz"
      y-label="dBV"
      :height="240"
      caption="Expected spectrum on the left channel once settled: the oscillation at −9 dBV with odd harmonics only, the first about 54 dB down. The right channel, which taps the generator, is silent but for the noise floor."
    />
    <p>
      With the generator off the left channel carries only this, and the right nothing: the
      cleanest possible statement that the signal is the DUT's own. Turn the generator's 1 kHz tone
      back on at −6 dBV and the two lines sit {{ (spectrum.fundamentalHz - 999).toFixed(0) }} Hz
      apart — resolved at 2.9 Hz per bin, and beating visibly in the oscilloscope view at
      {{ (spectrum.fundamentalHz - 999).toFixed(0) }} Hz. The metric tiles then pick the generator's
      tone as the fundamental and the oscillation appears in <em>Other peaks</em>, which is exactly
      where a spurious tone on a real amplifier shows up first.
    </p>

    <h2>Frequency response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Input voltage, −20 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Right channel is reference', 'On'],
        ['Smoothing', '1/24 octave'],
        ['Averages', '2 — so coherence means something'],
      ]"
    />
    <ManualChart
      :series="frSeries"
      :x-min="4"
      :x-max="24000"
      :y-min="-10"
      :y-max="40"
      x-label="Hz"
      y-label="Gain (dB)"
      :height="220"
      caption="Expected gain trace: 0 dB everywhere, and a +33 dB spike around 1010 Hz that is not gain at all — it is the free-running tone divided by the little stimulus energy the chirp puts in those bins."
    />
    <p>
      A chirp at −20 dBV spreads its energy over thirty thousand bins, so in any one of them the
      stimulus is tiny; a 0.35 V tone in the same bins is enormous by comparison, and the division
      that recovers a transfer function reports the ratio as +33 dB of gain. Raise the drive and the
      spike shrinks (the tone does not follow the stimulus, which is the tell); lower it and the
      spike grows. With two averages coherence collapses over exactly that band — the tone's phase
      relative to the sweep is different in every record — while it stays at 1.000 everywhere else,
      which is coherence doing precisely the job it exists for. On the Nyquist and Nichols views the
      spike is a wild excursion out of an otherwise unity contour, and the stability panel reports
      margins about a loop it never measured.
    </p>
    <p>
      Set generator by <strong>Output voltage</strong> and watch the level probe: it measures the
      DUT's "gain" from a chirp that comes back with this spike on it and sets the drive accordingly.
      The median it uses is robust to one narrow spike, so it usually lands near the right level —
      but the input ranger, which sees the peak, ranges for the 0.5 V oscillation rather than for
      the stimulus. Every probe in the app assumes the DUT answers only when spoken to; this is the
      model that shows what each of them does when that is false.
    </p>

    <h2>The other screens</h2>
    <p>
      The THD sweeps report a spurious "harmonic" whenever a harmonic bin happens to fall near
      1010 Hz — at a fundamental of 505 Hz, say, or 336.7 Hz — and elsewhere 0.000 %, since the
      through path is a straight wire (or the generator's 0.51 %, with Harmonics ticked). The impulse-response screen deconvolves a record
      that carries a continuous 1 kHz tone: the impulse sits on a sinusoidal carpet from end to end,
      the gated response shows the same +33 dB spike, and both decay surfaces show a 1 kHz ridge at
      constant level to the back of the chart, which is what "never decays" looks like there.
    </p>
  </div>
</template>
