<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { impedanceCurves } from '../../sim/curves'
import { FIXTURE, fixtureResistive } from '../../sim/models'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11 (chirp, 24 kHz span, −20 dBV, 1/48 octave, 2 averages,
// no calibration): 8.00 Ω from 20 Hz to 20 kHz, phase 0.0° everywhere but a 1.4° wobble near
// 100 Hz where the hum sits, median 7.994 Ω, coherence 1.000, drive current 0.93 mA.
const R = FIXTURE.referenceOhms
const load = FIXTURE.resistiveOhms
const z = computed(() => impedanceCurves(fixtureResistive(48_000), R, 10, 22_800))
const dividerDb = 20 * Math.log10(load / (R + load))

const magnitudeSeries = computed<ManualSeries[]>(() => [{ name: '|Z| (Ω)', points: z.value.magnitudeOhms, color: 'left' }])
const phaseSeries = computed<ManualSeries[]>(() => [{ name: 'Phase (°)', points: z.value.phaseDeg, color: 'phase' }])
const markers: ManualMarker[] = [{ y: load, label: `${load} Ω`, color: 'grey', dashed: false }]
</script>

<template>
  <div>
    <h1>Fixture: 8 Ω</h1>

    <p>
      The impedance fixture with a plain <strong>{{ load }} Ω resistor</strong> across its terminals:
      a <strong>{{ R }} Ω</strong> reference resistor in series with the load, the generator driving the
      pair, the <strong>left</strong> input on the generator side and the <strong>right</strong> input
      on the junction — the way a bench fixture is wired, and the way the impedance screen's default
      (reference channel Left) expects. There is no filter in the path at all: a resistive divider is
      a frequency-independent ratio, {{ dividerDb.toFixed(1) }} dB on the right channel.
    </p>
    <p>
      It is the impedance measurement's <strong>calibration target</strong>, and here for the same
      reason the <button type="button" class="manual-link" @click="showPage('sim-bandpass')">Bandpass</button>
      is: the one fixture whose answer needs no modelling to predict. A resistor has the same
      impedance at every frequency and no reactance whatever, so a correct measurement reads
      <strong>{{ load }}.000 Ω at 0.00°</strong> from one end of the sweep to the other — through a
      chirp, a two-channel ratio, an open calibration and a divider inversion. Anything that bends
      that line is a defect in one of those, not a property of the load.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['|Z|', `${load}.000 Ω at every frequency; the app read 8.00 Ω across the band, median 7.994 Ω`],
        ['Phase', '0.00° everywhere'],
        ['Right channel level', `${dividerDb.toFixed(1)} dB below the left: the divider’s ratio, ${load}/${R + load}`],
        ['Coherence', '1.000 across the band'],
        ['Drive current', '0.93 mA rms at −20 dBV — 0.1 V across 108 Ω'],
        ['With a short calibration applied', '7.88 Ω: the simulated loads have no leads, so subtracting the short standard’s 0.12 Ω takes off resistance that was never there'],
        ['Stepped sine', 'the same 8.000 Ω at every point; chirp and stepped agreeing is the check that the calibration chain is right'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — the only measurement a fixture is for.</li>
      <li>
        Everything else measures the <em>left</em> input, which for a fixture carries the generator
        untouched: the response screens read a straight wire, the THD screens nothing (or the
        generator's stack, with Harmonics ticked).
        Select the fixture on those screens and you are measuring nothing.
      </li>
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
        ['Resolution', '1.46 Hz'],
        ['Smoothing', '1/48 octave'],
        ['Averages', '2'],
        ['Calibration', 'Open and Short both not captured'],
      ]"
    />
    <p>These are the screen's defaults; press <strong>SINGLE</strong>.</p>
    <ManualChart
      :series="magnitudeSeries"
      :markers="markers"
      :x-min="10"
      :x-max="24000"
      :y-min="1"
      :y-max="100"
      y-log
      x-label="Hz"
      y-label="Ω"
      :height="220"
      caption="Expected |Z|: a flat line at 8.000 Ω. Autofit will stretch the noise to fill the plot — on a noise-free simulator run that is a few milliohms of ripple from the bottom of the band."
    />
    <ManualChart
      :series="phaseSeries"
      :x-min="10"
      :x-max="24000"
      :y-min="-90"
      :y-max="90"
      :y-step="45"
      x-label="Hz"
      y-label="Phase (°)"
      :height="180"
      caption="Expected phase: 0.00° across the band. With Hum on, a degree or two of wobble at 50 and 100 Hz is the hum leaking into the quieter channel, not reactance."
    />
    <p>
      What to try, because each is a check on a part of the chain rather than on the resistor:
    </p>
    <ul>
      <li>
        <strong>Flip the reference channel to Right.</strong> The louder channel is now the "DUT",
        the divider inverts to a <em>negative</em> impedance, and the chart shows a flat line at
        108 Ω with the phase pinned at ±180° — with an amber warning under the status line naming
        the other channel. A passive load cannot do that, which is how the app knows.
      </li>
      <li>
        <strong>Enter 50 Ω as the reference resistor.</strong> Every reading halves: 4.000 Ω. The
        screen scales by the ratio of what you typed to what the fixture has, and the simulator's is
        always {{ R }} Ω.
      </li>
      <li>
        <strong>Run the calibrations</strong> — open on
        <button type="button" class="manual-link" @click="showPage('sim-fixture-standards')">Fixture: open</button>,
        short on <em>Fixture: short (leads)</em> — then measure this fixture with <em>Subtract for the
        leads</em> set to <em>Measured short</em>. It reads 7.88 Ω: correct arithmetic against an
        idealised load that has no leads to subtract. A real 8 Ω resistor on real leads reads
        8.00 Ω only <em>after</em> that step.
      </li>
      <li>
        <strong>Switch Stimulus to Stepped sine</strong> and RUN SWEEP. Fifty points, all at
        8.000 Ω, in about a minute. Chirp and stepped sine reading the same fixture the same way is
        the second opinion the impedance page describes; on a real fixture their disagreeing is the
        tell for a calibration fault.
      </li>
    </ul>
  </div>
</template>
