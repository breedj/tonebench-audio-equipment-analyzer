<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { logSpace, magnitudeCurve, phaseCurve } from '../../sim/curves'
import { phonoRiaa } from '../../sim/models'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11 (24 kHz span, −20 dBV, 1/24-octave smoothing): +19.25 dB
// at 20 Hz, +13.12 at 100 Hz, 0.00 at 1 kHz, −8.28 at 5 kHz, −13.94 at 10 kHz, −19.63 at 20 kHz;
// recovered delay 0.53 samples. The model below agrees to 0.03 dB at every one of them.
const dut = phonoRiaa(48_000)
const dut192 = phonoRiaa(192_000)

/** The analog RIAA curve relative to 1 kHz, for the comparison the model was designed against. */
function analogRiaaDb(freqHz: number): number {
  const t1 = 3180e-6
  const t2 = 318e-6
  const t3 = 75e-6
  const m = (f: number) => {
    const w = 2 * Math.PI * f
    return Math.hypot(1, w * t2) / (Math.hypot(1, w * t1) * Math.hypot(1, w * t3))
  }
  return 20 * Math.log10(m(freqHz) / m(1000))
}

const frSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain (dB)', points: magnitudeCurve(dut, 4.4, 22_800, 400, 24), color: 'gain' },
  { name: 'Analog RIAA', points: logSpace(4.4, 22_800, 200).map((f) => [f, analogRiaaDb(f)] as const), color: 'grey', dashed: true },
  { name: 'Phase (°)', points: phaseCurve(dut, 4.4, 22_800, 400, 0.53), color: 'phase', axis: 'y2' },
])
const frMarkers: ManualMarker[] = [
  { x: 50.05, label: '50 Hz (3180 µs)', color: 'amber' },
  { x: 500.5, label: '500 Hz (318 µs)', color: 'amber' },
  { x: 2122, label: '2122 Hz (75 µs)', color: 'amber' },
]

// The stepped points, at 5 per octave from 20 Hz.
const steppedFreqs = logSpace(20, 20_000, 50)
const steppedSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain (dB)', points: steppedFreqs.map((f) => [f, dut.magnitudeDb(f)] as const), color: 'gain', kind: 'points' },
])

// FFT: a 1 kHz tone at −6 dBV passes at unity; a 20 Hz one comes back +19 dB. Just the two
// fundamentals — with the simulator's Harmonics box off (the default) there is nothing else.
const fftSeries = computed<ManualSeries[]>(() => [
  { name: '1 kHz tone at −6 dBV', points: [[1000, -6 + dut192.magnitudeDb(1000)]], color: 'left', kind: 'stem' },
  { name: '20 Hz tone at −20 dBV', points: [[20, -20 + dut192.magnitudeDb(20)]], color: 'amber', kind: 'stem' },
])
</script>

<template>
  <div>
    <h1>Phono stage (RIAA)</h1>

    <p>
      A phono preamplifier: the RIAA playback curve from its three time constants (3180, 318 and
      75 µs), normalised to unity at 1 kHz — <strong>+19.3 dB at 20 Hz</strong>,
      <strong>−19.6 dB at 20 kHz</strong>, 40 dB of tilt across the band. Every phono stage is
      specified against this curve, which is why the model is referenced to 1 kHz and not made unity
      across its passband like every other DUT here: the curve is only recognisable that way.
    </p>
    <p>
      That makes it the <strong>one DUT with gain</strong>, and the consequence matters: a tone at
      the bottom of the band comes back nearly ten times larger than it went in. The frequency-response
      and stepped-sine screens range their input for it; the FFT screen does not, so a loud low tone
      through this model meets the simulated ADC's clip exactly as it would on the hardware. It is
      the model for the two Bode screens, and for seeing what a 40 dB tilt does to a THD sweep.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['20 Hz', `${dut.magnitudeDb(20).toFixed(2)} dB (analog: ${analogRiaaDb(20).toFixed(2)})`],
        ['100 Hz', `${dut.magnitudeDb(100).toFixed(2)} dB`],
        ['1 kHz', '0.00 dB by definition'],
        ['10 kHz', `${dut.magnitudeDb(10_000).toFixed(2)} dB (analog: ${analogRiaaDb(10_000).toFixed(2)})`],
        ['20 kHz', `${dut.magnitudeDb(20_000).toFixed(2)} dB (analog: ${analogRiaaDb(20_000).toFixed(2)}) — the model is solved to hit this figure at every sample rate`],
        ['Phase', 'about −20° at 20 Hz, −55° near 100 Hz, −45° at 1 kHz, rising back through 0° above 10 kHz'],
        ['Bandwidth rows', 'meaningless — there is no passband; see below'],
        ['THD', '0.000 %; with Harmonics ticked, the generator’s stack tilted: D2 reads 2.6 dB low at 1 kHz because the second harmonic is attenuated relative to its fundamental, D3 4.7 dB low'],
        ['THD, frequency normalized', 'with Harmonics ticked, the stack’s own figures back: nD2 −46 dBc (0.50 %) and nD3 −60 dBc (0.10 %), flat across the band — the tilt cancels once each harmonic is divided by the fundamental at its own frequency'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — the one it is for.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — the same curve, and the screen that shows the input ranging working point by point.</li>
      <li><a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> — a low tone comes back large; the only model on which the two channels read different levels.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — zero; with Harmonics ticked, the stack read through a 6 dB per octave tilt: D2 reads about 2.6 dB low and D3 about 4.7 dB low at 1 kHz, and more the further up the slope the fundamental sits. Set <strong>THD method</strong> to <strong>Frequency normalized</strong> and the tilt cancels: nD2 reads the stack’s own −46 dBc and nD3 its −60 dBc, flat from end to end. This model is the cleanest demonstration of what that method is for.</li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — a spike with a long low-frequency tail; the gated response repeats the curve above the gate's limit.</li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>Frequency response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Input voltage, −20 dBV — not output voltage: with 40 dB of tilt there is no one output level to aim at'],
        ['Frequency span (Hz)', '24000'],
        ['Chirp start (Hz)', '2'],
        ['Resolution (Hz)', '1.46'],
        ['Right channel is reference', 'On'],
        ['Smoothing', '1/24 octave'],
        ['Averages', '1'],
        ['Display', 'Gain (dB), Top +25, Bottom −25'],
      ]"
    />
    <ManualChart
      :series="frSeries"
      :markers="frMarkers"
      :x-min="4"
      :x-max="24000"
      :y-min="-25"
      :y-max="25"
      :y2-min="-90"
      :y2-max="90"
      :y2-step="45"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Phase (°)"
      :height="280"
      caption="Expected Bode plot: the RIAA playback curve with its three corners marked, the analog curve dashed underneath it (they differ by less than 0.35 dB anywhere at 48 kHz), and the phase with the recovered half-sample of delay removed."
    />
    <p>
      The curve should sit on the dashed analog one to within a third of a decibel at a 24 kHz span
      and to a hundredth at 48 or 96 kHz — the model is built to read the analog figure at 20 kHz at
      every sample rate, which a textbook bilinear design does not (it reads −28 dB at 48 kHz). Two
      things the panel does that are worth understanding rather than believing:
    </p>
    <ul>
      <li>
        <strong>The bandwidth rows describe nothing.</strong> The passband median over
        200 Hz – 2 kHz lands at about −0.3 dB (near 1 kHz, as it happens); the ±1 dB row then finds a
        band a bin wide somewhere between 600 and 800 Hz, and the −3 dB row reads "&lt; 4.4 Hz – 2.3 kHz" — the lower
        corner clamped because the response never falls below the reference going down. Every
        figure is a correct search of a curve that has no passband. Quote a phono stage's accuracy
        as deviation from RIAA, which is what the dashed line is for; the CSV export gives you the
        numbers to subtract.
      </li>
      <li>
        <strong>The input range follows the peak, not the tone.</strong> The runner starts on its
        widest range and settles on the one that fits the chirp's loudest return — the bottom of
        the band, +19 dB up. The status bar's Attenuation reads accordingly higher than it does for a
        unity DUT at the same drive. That is also why <em>Set generator by output voltage</em> is
        the wrong mode here: the probe would set the drive for the median gain, which is roughly
        1 kHz's, and the bottom of the band would then arrive 9× larger than the level asked for.
      </li>
    </ul>

    <h2>Stepped sine response</h2>
    <SettingsTable
      :rows="[
        ['Start / End (Hz)', '20 / 20000'],
        ['Steps per octave', '5'],
        ['Level mode', 'Generator voltage, −20 dBV'],
        ['Right channel is reference', 'On'],
        ['Averages per point', '1'],
        ['Adaptive resolution', 'On'],
      ]"
    />
    <ManualChart
      :series="steppedSeries"
      :x-min="15"
      :x-max="25000"
      :y-min="-25"
      :y-max="25"
      x-label="Hz"
      y-label="Gain (dB)"
      :height="220"
      caption="Expected stepped response: the same curve at 50 points, from +19.3 dB at 20 Hz to −19.6 dB at 20 kHz."
    />
    <p>
      Watch the status bar during the run: the input range steps down as the sweep climbs the
      curve, since each point is predicted from the last and a stage whose output falls 6 dB per
      octave needs a more sensitive range every few points. This is the screen on which the ranging
      is visible as it happens; the chirp does it once, before the record.
    </p>

    <h2>FFT analysis</h2>
    <SettingsTable
      :rows="[
        ['Stimulus', 'Sine tones: 1000 Hz at −6 dBV, then a second run with 20 Hz at −20 dBV'],
        ['Left / Right', 'Both'],
        ['Sample rate / FFT size / Window', '192000 / 65536 / Hann'],
        ['Averaging', 'Linear, 8 spectra'],
      ]"
    />
    <ManualChart
      :series="fftSeries"
      :x-min="5"
      :x-max="96000"
      :y-min="-120"
      :y-max="10"
      x-label="Hz"
      y-label="dBV"
      :height="240"
      caption="Expected left-channel tones: 1 kHz at −6 dBV, passed at unity, and a 20 Hz tone sent at −20 dBV coming back at −0.7 dBV. With the Harmonics box ticked each tone would also carry the generator's stack, tilted by the curve's slope at each harmonic's own frequency."
    />
    <p>
      At 1 kHz the left and right <em>levels</em> agree — unity is unity. Tick
      <strong>Harmonics</strong> and the harmonics tables do not: left reads H2 at −48.6 dBc and H3
      at −64.7 dBc against the right's −46 and −60, because each harmonic is attenuated by the
      curve's slope at its own frequency. The 20 Hz tone
      is the one to try with care: sent at −20 dBV it comes back at −0.7 dBV; sent at −6 dBV it
      would come back at +13 dBV, which is 4.5 V rms and past the simulated ADC's clip on the range
      the FFT screen sits on. The metric tiles would then report clipping-shaped distortion that is
      entirely the analyzer's — exactly what a real phono stage does to an unwary bench at 20 Hz.
    </p>
  </div>
</template>
