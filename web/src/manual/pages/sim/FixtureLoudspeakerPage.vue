<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { impedanceCurves } from '../../sim/curves'
import { FIXTURE, fixtureLoudspeaker } from '../../sim/models'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11 (chirp, 24 kHz span, −20 dBV, 1/48 octave, 2 averages):
// 23.4 Ω at +52.2° at 20 Hz, 59.92 Ω at −2.3° at 45.4 Hz (the peak, at the nearest bin),
// 23.3 Ω at −51.1° at 100 Hz, 6.49 Ω at −8.6° at 1 kHz, 6.40 Ω at −0.7° at 10 kHz; median
// 6.40 Ω, coherence 1.000. The model agrees to 0.1 % and 0.5°.
const R = FIXTURE.referenceOhms
const re = FIXTURE.speakerReOhms
const peak = FIXTURE.speakerPeakOhms
const f0 = FIXTURE.speakerResonanceHz
const q = FIXTURE.speakerQ
const z = computed(() => impedanceCurves(fixtureLoudspeaker(48_000), R, 10, 22_800, 800))

const series = computed<ManualSeries[]>(() => [
  { name: '|Z| (Ω)', points: z.value.magnitudeOhms, color: 'left' },
  { name: 'Phase (°)', points: z.value.phaseDeg, color: 'phase', axis: 'y2' },
])
const markers: ManualMarker[] = [
  { x: f0, label: `fs ${f0} Hz`, color: 'amber' },
  { y: peak, label: `${peak} Ω`, color: 'grey' },
  { y: re, label: `Re ${re} Ω`, color: 'grey' },
]
const zAt = (f: number) => {
  const pts = z.value.magnitudeOhms
  let b = 0
  for (let i = 0; i < pts.length; i++) if (Math.abs(pts[i]![0] - f) < Math.abs(pts[b]![0] - f)) b = i
  return { ohms: pts[b]![1], deg: z.value.phaseDeg[b]![1] }
}
const row = (f: number, label: string) => [label, `${zAt(f).ohms.toFixed(2)} Ω at ${zAt(f).deg.toFixed(1)}°`] as const
// The −3 dB (√2) points of the motional peak: f0 (1 ± 1/2Q).
const halfPowerLow = f0 * (1 - 1 / (2 * q))
const halfPowerHigh = f0 * (1 + 1 / (2 * q))
</script>

<template>
  <div>
    <h1>Fixture: loudspeaker</h1>

    <p>
      The {{ R }} Ω fixture with a loudspeaker driver across it: <strong>{{ re }} Ω</strong> of
      voice-coil resistance in series with a motional resonance at <strong>{{ f0 }} Hz</strong>
      that lifts the impedance to <strong>{{ peak }} Ω</strong> at its peak, Q {{ q }}. It is the
      shape an impedance sweep is normally run to see, and it is still exactly predictable: the
      voice coil in series with a parallel RLC makes the divider one second-order section, so the
      peak really is <code>Re + Rres</code> at resonance and <code>Re</code> at DC, with nothing else
      in the path to comb it.
    </p>
    <p>
      Deliberately <em>without</em> the voice coil's own inductance. A real driver's |Z| rises again
      above a few kilohertz as <code>Re + jωLe</code>; adding that here would make the impedance a
      ratio of cubics, which needs a factorisation and would cost the one property that makes this
      fixture checkable — that the peak is a number you can write down. So above resonance this
      driver settles onto {{ re }} Ω and stays there, which no real one does. The
      <button type="button" class="manual-link" @click="showPage('sim-fixture-rl')">6 Ω + 1 mH fixture</button> is the rising part on its own.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        row(20, '20 Hz'),
        row(halfPowerLow, `${halfPowerLow.toFixed(0)} Hz (lower half-power point)`),
        [`${f0} Hz`, `${peak}.00 Ω at 0.0° — the peak, exactly Re + Rres; the app reads 59.92 Ω at the nearest bin, 45.4 Hz`],
        row(halfPowerHigh, `${halfPowerHigh.toFixed(0)} Hz (upper half-power point)`),
        row(100, '100 Hz'),
        row(1000, '1 kHz'),
        row(10_000, '10 kHz'),
        ['Median |Z|', `${re}.40 Ω — the voice coil, since most of the band is far above resonance`],
        ['Phase', `+52° below resonance (inductive), 0° at fs, −52° just above (capacitive), back to 0° by a few kilohertz`],
        ['Coherence', '1.000 across the band'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — the only measurement a fixture is for; the left input carries the generator untouched on every other screen.</li>
      <li>For the same driver's <em>acoustic</em> side — the response and decay of a loudspeaker — use the <button type="button" class="manual-link" @click="showPage('sim-loudspeaker')">Loudspeaker</button> DUT model instead; the two are unrelated circuits that share a name.</li>
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
        ['Display', 'Frequency range 10 Hz – 24 kHz, Autofit on the ohms axis'],
      ]"
    />
    <ManualChart
      :series="series"
      :markers="markers"
      :x-min="10"
      :x-max="24000"
      :y-min="1"
      :y-max="100"
      y-log
      :y2-min="-90"
      :y2-max="90"
      :y2-step="45"
      x-label="Hz"
      y-label="Ω"
      y2-label="Phase (°)"
      :height="280"
      caption="Expected impedance: the classic driver curve — 6.4 Ω of voice coil, a 60 Ω peak at 45 Hz with the phase swinging from +52° to −52° through it, and (unlike a real driver) a flat 6.4 Ω all the way to the top of the band."
    />
    <p>
      The phase is the part of this curve worth understanding. Below resonance the motional
      impedance is inductive (the cone is mass-controlled), so the phase is positive; at
      {{ f0 }} Hz it passes through zero with the impedance real and at its maximum; above,
      compliance takes over and the phase goes negative, before the voice-coil resistance wins and
      it returns to zero. That zero crossing <em>at the peak</em> is the definition of fs, and the
      two half-power points either side, {{ halfPowerLow.toFixed(0) }} and {{ halfPowerHigh.toFixed(0) }} Hz,
      give the Q: fs divided by their spacing, {{ q }}.
    </p>
    <p>
      Three things worth trying on it:
    </p>
    <ul>
      <li>
        <strong>Nyquist view.</strong> A series resistance plus a parallel resonance is a
        <em>circle</em> in the impedance plane, sitting on the real axis between {{ re }} and
        {{ peak }} Ω. The chirp draws it at once; the stepped sine draws it point by point, and the
        points crowd where the phase changes slowest.
      </li>
      <li>
        <strong>Drive level.</strong> This is the fixture that argues for measuring gently: a real
        driver's compliance depends on excursion, so its fs and Q drift with drive. The simulated one
        is linear and reads the same at −40 dBV as at 0 — the difference between the two runs on a
        real driver is the nonlinearity, and on the simulator it is exactly nothing.
      </li>
      <li>
        <strong>Stepped sine at 12 steps per octave</strong>, 20 – 200 Hz. Five points per octave
        put only two or three points on a Q-{{ q }} peak and can miss its top by an ohm; twelve draw
        it properly, in about the same time as the whole default sweep since the range is narrow.
        The chirp's 1.46 Hz bins had it all along — which is the chirp's argument.
      </li>
    </ul>
  </div>
</template>
