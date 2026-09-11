<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { impedanceCurves } from '../../sim/curves'
import { FIXTURE, fixtureReactive } from '../../sim/models'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11 (chirp, 24 kHz span, −20 dBV, 1/48 octave, 2 averages):
// 6.00 Ω at 1.2° at 20 Hz, 8.49 Ω at 45.1° at 955 Hz, 73.8 Ω at 85.3° at 10 kHz, 350 Ω at 88.8°
// at 20 kHz; median 89 Ω, coherence 1.000. The model agrees to 0.1 % and 0.2° — including the
// 350 Ω, which is the un-prewarped bilinear mapping's tan(πf/fs) growth of the inductance at
// 48 kHz, not the analog 126 Ω.
const R = FIXTURE.referenceOhms
const rs = FIXTURE.reactiveOhms
const L = FIXTURE.reactiveHenry
const cornerHz = rs / (2 * Math.PI * L)
const z = computed(() => impedanceCurves(fixtureReactive(48_000), R, 10, 22_800))

const series = computed<ManualSeries[]>(() => [
  { name: '|Z| (Ω)', points: z.value.magnitudeOhms, color: 'left' },
  { name: 'Phase (°)', points: z.value.phaseDeg, color: 'phase', axis: 'y2' },
])
const markers = computed<ManualMarker[]>(() => [
  { x: cornerHz, label: `45° at ${formatHz(cornerHz)}`, color: 'amber' },
  { y: rs, label: `${rs} Ω`, color: 'grey' },
])
/** The analog RL, for comparison: the simulator's discrete fixture departs from it towards Nyquist. */
const analogAt = (f: number) => {
  const w = 2 * Math.PI * f
  return { ohms: Math.hypot(rs, w * L), deg: (Math.atan2(w * L, rs) * 180) / Math.PI }
}
/** What the simulator's fixture actually presents at a 48 kHz rate, read off the curve. */
const zAt = (f: number) => {
  const pts = z.value.magnitudeOhms
  let b = 0
  for (let i = 0; i < pts.length; i++) if (Math.abs(pts[i]![0] - f) < Math.abs(pts[b]![0] - f)) b = i
  return { ohms: pts[b]![1], deg: z.value.phaseDeg[b]![1] }
}
const row = (f: number, label: string) =>
  [label, `${zAt(f).ohms.toFixed(zAt(f).ohms < 100 ? 2 : 0)} Ω at ${zAt(f).deg.toFixed(1)}° (analog RL: ${analogAt(f).ohms.toFixed(analogAt(f).ohms < 100 ? 2 : 0)} Ω at ${analogAt(f).deg.toFixed(1)}°)`] as const
</script>

<template>
  <div>
    <h1>Fixture: 6 Ω + 1 mH</h1>

    <p>
      The same {{ R }} Ω fixture with a <strong>{{ rs }} Ω resistor in series with {{ L * 1000 }} mH</strong>
      across it — the simplest load that has a phase angle, and so the one that says the measurement
      recovers <em>reactance</em> and not just magnitude. Its impedance is
      <code>Z = R + jωL</code>: {{ rs }} Ω at DC, rising through 45° where the reactance equals the
      resistance, at <strong>{{ formatHz(cornerHz) }}</strong>, and on up at 6 dB per octave towards
      90°.
    </p>
    <p>
      On a log-log |Z| plot that is a straight line above the corner, and on the phase axis a
      monotonic climb — and the two must agree. Reading a rising magnitude with a <em>flat</em>
      phase is the classic symptom of the two channels being taken from different records, which
      this fixture catches: a passive RL cannot have one without the other.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        row(20, '20 Hz'),
        row(100, '100 Hz'),
        row(cornerHz, formatHz(cornerHz)),
        row(1000, '1 kHz'),
        row(5000, '5 kHz'),
        row(10_000, '10 kHz'),
        row(20_000, '20 kHz'),
        ['Median |Z|', 'about 89 Ω over the band — the median of a curve that spends most of its decades on the rising part'],
        ['Coherence', '1.000 across the band'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — the only measurement a fixture is for; the left input carries the generator untouched on every other screen.</li>
    </ul>

    <h2>Impedance</h2>
    <SettingsTable
      :rows="[
        ['Stimulus', 'Chirp sweep'],
        ['Reference resistor (Ω)', `${R}`],
        ['Reference channel', 'Left (generator side)'],
        ['Frequency span', '24000'],
        ['Chirp start (Hz)', '10'],
        ['Drive level', '−20 dBV'],
        ['Smoothing', '1/48 octave'],
        ['Averages', '2'],
        ['Calibration', 'Open and Short both not captured'],
      ]"
    />
    <ManualChart
      :series="series"
      :markers="markers"
      :x-min="10"
      :x-max="24000"
      :y-min="1"
      :y-max="1000"
      y-log
      :y2-min="0"
      :y2-max="90"
      :y2-step="45"
      x-label="Hz"
      y-label="Ω"
      y2-label="Phase (°)"
      :height="260"
      caption="Expected impedance at a 24 kHz span: 6 Ω flat at the bottom, a corner at 955 Hz, then a 6 dB per octave climb that steepens towards the top of the band — 357 Ω at 20 kHz — with the phase rising from 0° through 45° at the corner towards 90°."
    />
    <p>
      <strong>The curve bends upward above 5 kHz, and that is the simulator, not the measurement.</strong>
      An analog 1 mH is 126 Ω at 20 kHz; the simulated fixture reads 357 Ω there, and the app
      measures exactly that. The simulator maps its analog circuits onto the sample rate with a
      bilinear transform and does not prewarp it, so a reactance grows by
      <code>tan(πf/fs)/(πf/fs)</code> — invisible below a few kilohertz at 48 kHz, a factor 2.85 at
      20 kHz. The measurement is reporting the discrete circuit it was given, correctly. Run the same
      fixture at a <strong>96 kHz span</strong> (192 kHz) and the 20 kHz reading falls to within 4 %
      of the analog value; that is the honest test of the warping, and the reason the analog figures
      sit beside the simulated ones in the table above.
    </p>
    <p>
      The top of the band is also where this fixture works the measurement hardest. Where the load
      exceeds the {{ R }} Ω reference — above about 12 kHz here — the junction carries most of the
      generator's voltage and the two channels draw close together; the divider inversion
      <code>Z = R·H/(1−H)</code> is then dividing by a small number, and any error in H is multiplied.
      That is what the impedance page's advice on <em>choosing the resistor</em> is about: the
      measurement is most accurate where the load and the reference are comparable. On the simulator
      it still reads true at 20 kHz, because there is nothing wrong with H; on a real fixture this is
      where the analyzer's input impedance and the leads' capacitance start to show, and the
      Advanced section's <em>Analyzer input impedance</em> field exists for it.
    </p>
    <p>
      The <strong>Nyquist</strong> view of the impedance plane draws this as a vertical line starting
      at {{ rs }} Ω on the real axis and heading straight up — a pure series RL has a constant real
      part. Compare the
      <button type="button" class="manual-link" @click="showPage('sim-fixture-capacitor')">47 µF fixture</button>, whose line heads straight down from the same kind of point, and the
      <button type="button" class="manual-link" @click="showPage('sim-fixture-loudspeaker')">loudspeaker fixture</button>, which draws a circle.
    </p>
  </div>
</template>
