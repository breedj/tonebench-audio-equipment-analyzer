<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { impedanceCurves } from '../../sim/curves'
import { FIXTURE, fixtureOpen, fixtureShort } from '../../sim/models'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11 (chirp, 24 kHz span, 0 dBV, 1/48 octave, 2 averages).
// Short: 0.120 Ω at −0.1° at 20 Hz, 0.120 Ω at 1.6° at 1 kHz, 0.128 Ω at 20.0° at 10 kHz,
// 0.243 Ω at 60.5° at 20 kHz, median 0.131 Ω. Open: tens of megohms at the bottom falling to
// 88 kΩ at 20 kHz with the phase wandering between −40° and −70°, median 86 kΩ — whatever the
// noise floor says, which is what an open is.
const R = FIXTURE.referenceOhms
const shortOhms = FIXTURE.shortOhms
const shortHenry = FIXTURE.shortHenry
const strayFarad = FIXTURE.openStrayFarad
const shortCornerHz = shortOhms / (2 * Math.PI * shortHenry)
const zShort = computed(() => impedanceCurves(fixtureShort(48_000), R, 10, 22_800))
const zOpen = computed(() => impedanceCurves(fixtureOpen(48_000), R, 10, 22_800))

const shortSeries = computed<ManualSeries[]>(() => [
  { name: '|Z| (Ω)', points: zShort.value.magnitudeOhms, color: 'left' },
  { name: 'Phase (°)', points: zShort.value.phaseDeg, color: 'phase', axis: 'y2' },
])
const shortMarkers = computed<ManualMarker[]>(() => [
  { y: shortOhms, label: `${shortOhms} Ω`, color: 'grey' },
  { x: shortCornerHz, label: `45° at ${formatHz(shortCornerHz)}`, color: 'amber' },
])
const openSeries = computed<ManualSeries[]>(() => [{ name: '|Z| (Ω), noise-free', points: zOpen.value.magnitudeOhms, color: 'left' }])
const shortAt = (f: number) => {
  const pts = zShort.value.magnitudeOhms
  let b = 0
  for (let i = 0; i < pts.length; i++) if (Math.abs(pts[i]![0] - f) < Math.abs(pts[b]![0] - f)) b = i
  return `${pts[b]![1].toFixed(3)} Ω at ${zShort.value.phaseDeg[b]![1].toFixed(1)}°`
}
const dividerDb = 20 * Math.log10(shortOhms / (R + shortOhms))
</script>

<template>
  <div>
    <h1>Fixture: short and open</h1>

    <p>
      The two calibration standards. <strong>Fixture: short (leads)</strong> is the {{ R }} Ω
      fixture with its leads shorted together: <strong>{{ shortOhms }} Ω and {{ shortHenry * 1e6 }} µH</strong>,
      which is what a real pair of test leads measured on the bench. <strong>Fixture: open</strong>
      is the same fixture with nothing across it but <strong>{{ strayFarad * 1e12 }} pF</strong> of
      stray capacitance. Together they let the impedance screen's whole calibration flow be walked
      through on the simulator: capture the open, capture the short, then measure a load with
      <em>Subtract for the leads</em> set to <em>Measured short</em>.
    </p>
    <p>
      A <button type="button" class="manual-link" @click="showPage('sim-straight-wire')">Straight wire</button>
      is also an open — nothing across the terminals — and works as the open standard. The explicit
      open exists because a straight wire's divider ratio is <em>exactly</em> one, and
      <code>Z = R·H/(1−H)</code> of exactly one is a division by zero on a noise-free run. 100 pF
      puts the ratio 0.07° off unity at 20 kHz, which reads as open on every plot and is finite
      everywhere.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Short, 20 Hz – 1 kHz', `${shortAt(100)} — the leads’ resistance, flat`],
        ['Short, 10 kHz', `${shortAt(10_000)}: the leads’ inductance beginning to show`],
        ['Short, 20 kHz', `${shortAt(20_000)} (the simulator’s bilinear warping lifts this above the analog 0.14 Ω)`],
        ['Short, 45° point', `${formatHz(shortCornerHz)}, where ωL = R — above the band at a 24 kHz span`],
        ['Short, right channel', `${dividerDb.toFixed(0)} dB below the left: the hardest case the measurement has`],
        ['Open', 'tens of megohms at the bottom of the band, tens of kilohms at the top, phase wandering: whatever the noise floor says, which is what an open is'],
        ['8 Ω fixture with the short subtracted', '7.88 Ω — the simulated loads have no leads, so the subtraction takes off 0.12 Ω that was never there'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — the calibration steps, and a measurement of the leads themselves.</li>
    </ul>

    <h2>The short</h2>
    <SettingsTable
      :rows="[
        ['DUT', 'Fixture: short (leads)'],
        ['Stimulus', 'Chirp sweep'],
        ['Reference resistor (Ω)', `${R}`],
        ['Reference channel', 'Left (generator side)'],
        ['Frequency span', '24000'],
        ['Drive level', '0 dBV — see below'],
        ['Smoothing', '1/48 octave'],
        ['Averages', '2'],
      ]"
    />
    <ManualChart
      :series="shortSeries"
      :markers="shortMarkers"
      :x-min="10"
      :x-max="24000"
      :y-min="0.01"
      :y-max="10"
      y-log
      :y2-min="0"
      :y2-max="90"
      :y2-step="45"
      x-label="Hz"
      y-label="Ω"
      y2-label="Phase (°)"
      :height="240"
      caption="Expected impedance of the shorted leads, measured as a load: 0.120 Ω flat, with the 0.6 µH lifting it and turning the phase upward from about 5 kHz."
    />
    <p>
      A shorted fixture is the worst case the measurement has. The junction sits at the lead
      impedance over the reference resistor — {{ dividerDb.toFixed(0) }} dB down on the generator —
      and the one shared input range is set by the loud channel, so the drive level is the only
      lever left. That is why the calibration has its own <strong>Calibration drive level</strong>,
      0 dBV by default and louder than the measurement's: measured on a real fixture, the same leads
      read 0.207 Ω at −22.5° with coherence 0.80 at −20 dBV and a flat 0.127 Ω at +0.8° with
      coherence 0.998 at 0 dBV. Try it here: measure this fixture at −20 dBV with
      <strong>Noise</strong> on, then at 0 dBV, and watch the bottom of the band settle. Raising the
      drive costs no accuracy, since a lead set is linear and presents the same impedance at any
      level — the reason the two levels are separate controls rather than one.
    </p>

    <h2>The open</h2>
    <ManualChart
      :series="openSeries"
      :x-min="10"
      :x-max="24000"
      :y-min="10000"
      :y-max="1e9"
      y-log
      x-label="Hz"
      y-label="Ω"
      :height="200"
      caption="What 100 pF across 100 Ω would read with no noise at all: 1/(2πfC), from 160 MΩ at 10 Hz to 28 kΩ at 22 kHz. With the simulator's noise on, the app reads a noisy version of this — tens of megohms at the bottom, 88 kΩ at the top, phase drifting between −40° and −70° — which is the correct picture of nothing."
    />
    <p>
      There is no useful number in an open's own impedance, and that is fine: the open standard is
      not measured for its value but captured so that whatever the analyzer's two channels disagree
      about with nothing connected — a gain mismatch, a phase offset, the leads' capacitance — can be
      divided out of every later measurement. On the simulator the two channels agree exactly, so
      the captured open is a straight line at unity and applying it changes nothing measurable.
    </p>

    <h2>The calibration flow, end to end</h2>
    <ol>
      <li>Select <strong>Fixture: open</strong> in the status bar. In the Calibration section press <strong>Measure</strong> beside <em>Open</em>; it captures at the calibration drive level and reports <em>captured</em>.</li>
      <li>Select <strong>Fixture: short (leads)</strong>. Press <strong>Measure</strong> beside <em>Short</em>.</li>
      <li>Select <strong>Fixture: 8 Ω</strong>, set <em>Subtract for the leads</em> to <em>Measured short</em>, and press Single. It reads <strong>7.88 Ω</strong>.</li>
    </ol>
    <p>
      That last figure is right and looks wrong, which is the point of doing it once on the
      simulator: the short standard says the leads are 0.12 Ω, so 0.12 Ω is subtracted from the
      load — but the simulated loads are ideal and have no leads, so the subtraction takes off
      resistance that was never there. On a real fixture the same step is what turns 8.12 Ω into
      8.00. Set <em>Subtract for the leads</em> back to <em>Nothing</em> and the 8 Ω fixture reads 8.000 Ω
      again; or enter the leads by hand as <em>Fixed value</em>, 0.12 Ω and 0.0006 mH, and get the
      same 7.88 Ω, which confirms the two paths agree.
    </p>
  </div>
</template>
