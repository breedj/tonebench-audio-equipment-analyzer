<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { impedanceCurves } from '../../sim/curves'
import { FIXTURE, fixtureCapacitive } from '../../sim/models'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11 (chirp, 24 kHz span, −20 dBV, 1/48 octave, 2 averages):
// 165 Ω at −89.9° at 20 Hz, 30.6 Ω at −89.1° at 100 Hz, 3.39 Ω at −85.0° at 1 kHz, 0.416 Ω at
// −44.1° at 10 kHz, 0.306 Ω at −10.8° at 20 kHz; median 0.38 Ω, coherence 1.000. The model
// agrees to 1 % and 0.5°.
const R = FIXTURE.referenceOhms
const C = FIXTURE.capacitiveFarad
const esr = FIXTURE.capacitiveEsrOhms
const esrCornerHz = 1 / (2 * Math.PI * esr * C)
const crossHz = 1 / (2 * Math.PI * R * C)
const z = computed(() => impedanceCurves(fixtureCapacitive(48_000), R, 10, 22_800))

const series = computed<ManualSeries[]>(() => [
  { name: '|Z| (Ω)', points: z.value.magnitudeOhms, color: 'left' },
  { name: 'Phase (°)', points: z.value.phaseDeg, color: 'phase', axis: 'y2' },
])
const markers = computed<ManualMarker[]>(() => [
  { x: crossHz, label: `|Z| = R at ${formatHz(crossHz)}`, color: 'amber' },
  { x: esrCornerHz, label: `ESR corner ${formatHz(esrCornerHz)}`, color: 'amber' },
  { y: esr, label: `ESR ${esr} Ω`, color: 'grey' },
])
const zAt = (f: number) => {
  const pts = z.value.magnitudeOhms
  let b = 0
  for (let i = 0; i < pts.length; i++) if (Math.abs(pts[i]![0] - f) < Math.abs(pts[b]![0] - f)) b = i
  return { ohms: pts[b]![1], deg: z.value.phaseDeg[b]![1] }
}
const analogAt = (f: number) => {
  const xc = 1 / (2 * Math.PI * f * C)
  return { ohms: Math.hypot(esr, xc), deg: (-Math.atan2(xc, esr) * 180) / Math.PI }
}
const row = (f: number, label: string) =>
  [label, `${zAt(f).ohms.toFixed(zAt(f).ohms < 10 ? 3 : 1)} Ω at ${zAt(f).deg.toFixed(1)}° (analog: ${analogAt(f).ohms.toFixed(analogAt(f).ohms < 10 ? 3 : 1)} Ω at ${analogAt(f).deg.toFixed(1)}°)`] as const
</script>

<template>
  <div>
    <h1>Fixture: 47 µF</h1>

    <p>
      The {{ R }} Ω fixture with a <strong>{{ C * 1e6 }} µF capacitor</strong> and
      <strong>{{ esr }} Ω of ESR</strong> across it: |Z| falling 6 dB per octave from
      {{ zAt(20).ohms.toFixed(0) }} Ω at 20 Hz to under half an ohm at 10 kHz, at a phase heading
      for −90° and stopping short of it where the ESR takes over. It is the mirror of the
      <button type="button" class="manual-link" @click="showPage('sim-fixture-rl')">6 Ω + 1 mH fixture</button>, and the test that the phase axis has its <em>sign</em> right: an inductor and a
      capacitor draw the same 6 dB per octave slope on a log-magnitude plot in opposite directions,
      and only the phase says which is which.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        row(20, '20 Hz'),
        row(crossHz, `${formatHz(crossHz)} (|Z| = ${R} Ω)`),
        row(100, '100 Hz'),
        row(1000, '1 kHz'),
        row(esrCornerHz, `${formatHz(esrCornerHz)} (ESR corner, −45°)`),
        row(20_000, '20 kHz'),
        ['Median |Z|', 'about 0.38 Ω — most of the band is below the ESR corner'],
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
      :y-min="0.1"
      :y-max="1000"
      y-log
      :y2-min="-90"
      :y2-max="0"
      :y2-step="45"
      x-label="Hz"
      y-label="Ω"
      y2-label="Phase (°)"
      :height="260"
      caption="Expected impedance: a 6 dB per octave fall at −90° until the ESR corner near 11 kHz, where |Z| flattens onto 0.3 Ω and the phase swings back towards 0°."
    />
    <p>
      Two regions, and each teaches something. Below the ESR corner the fixture is very nearly a
      pure capacitor: its phase reads −89.9° at 20 Hz, the missing tenth of a degree being the
      {{ esr }} Ω of ESR against 170 Ω of reactance, and it drifts to −85° by 1 kHz as the reactance
      falls towards the ESR. Above the corner the ESR dominates: |Z| settles onto {{ esr }} Ω and the
      phase climbs back towards 0°. Reading the ESR of a real capacitor is exactly this — the floor
      the curve flattens onto — and the phase is what confirms it is resistance and not the bottom of
      the measurement's range. The simulator's own curve departs a little from the analog one at the
      top of the band, for the reason the RL fixture's page explains: the bilinear mapping is not
      prewarped, so the capacitance appears to grow towards Nyquist at a 48 kHz rate.
    </p>
    <p>
      The bottom of the band is where this load makes the measurement work: at 20 Hz the junction
      carries most of the drive and the channels are close, and at the chirp's 10 Hz start |Z| is
      340 Ω against {{ R }}. With <strong>Noise</strong> on, that is where the coherence dips first
      if the drive is lowered — try −40 dBV — and the phase starts to wander off −90°. On a real
      electrolytic the same corner is where its leakage and dielectric absorption would appear; the
      simulated capacitor has neither.
    </p>
  </div>
</template>
