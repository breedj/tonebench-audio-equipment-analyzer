<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../components/ManualChart.vue'
import SettingsTable from '../components/SettingsTable.vue'
import { useManual } from '../useManual'

const { showPage } = useManual()

/**
 * Three measurements of this bench's QA403, each in dB relative to its own 1 kHz reading — which is
 * what makes the columns comparable at all. One table feeds the prose table and both charts,
 * deliberately: these are measured figures, and a figure quoted twice is one that can disagree with
 * itself. `null` is a tone that column could not reach, never a measurement that was skipped.
 *
 * `input` is the one that answers the question the other two cannot. The analyzer's own generator
 * was silent and an external generator drove the input directly, so the reading is the INPUT PATH
 * ALONE: four sweeps recorded to WAV at 384 kHz on 2026-10-05, serial 1077527945 firmware 60 —
 * two from one generator (1 kHz upwards, and 50 kHz → 197 kHz) and two from a *second* generator at
 * two further levels, all four agreeing to 0.00–0.02 dB from 110 to 170 kHz over a 10.4 dB spread
 * of level. The second generator is what rules out the source: a sweep measures source × input, and
 * two unrelated sources do not share a roll-off. Quoted to 0.1 dB, well inside that spread.
 *
 * `at192` and `at384` are loopbacks — generator and input in series — measured 2026-10-03 on serial
 * 4039_C589 by Qa40x384kHzHardwareTests.Loopback_At384kHz_ReportsTheAnalogBandwidth.
 */
const BANDWIDTH: readonly { hz: number; input: number | null; at192: number | null; at384: number | null }[] = [
  { hz: 1_000, input: 0.0, at192: 0.0, at384: 0.0 },
  { hz: 20_000, input: 0.0, at192: -0.04, at384: -0.72 },
  { hz: 50_000, input: -0.1, at192: -0.23, at384: -1.42 },
  { hz: 80_000, input: -0.3, at192: -0.58, at384: -2.26 },
  { hz: 90_000, input: -0.4, at192: -1.14, at384: -8.08 },
  { hz: 120_000, input: -0.6, at192: null, at384: -32.01 },
  { hz: 150_000, input: -0.8, at192: null, at384: -31.28 },
  { hz: 180_000, input: -1.2, at192: null, at384: -50.81 },
  { hz: 189_000, input: -3.0, at192: null, at384: null },
]

function legPoints(leg: 'input' | 'at192' | 'at384'): (readonly [number, number])[] {
  return BANDWIDTH.filter((r) => r[leg] !== null).map((r) => [r.hz, r[leg]!] as const)
}

/**
 * The same external sweep captured at both rates, each referenced to its own 50 kHz — which is what
 * separates the analog roll-off from the digital one. At 384 kHz a 90 kHz tone sits at 0.47 of
 * Nyquist and at 192 kHz at 0.94 of it, so a loss that agrees between the columns is fixed in hertz
 * and one that appears only in the 192 kHz column is a fraction of Nyquist.
 *
 * Referenced to 50 kHz rather than 1 kHz because the 192 kHz sweep started at 50 kHz; the analog
 * loss already present there is 0.1 dB, so add that to put these on the table above's basis.
 */
const TWO_RATES: readonly { hz: number; at384: number; at192: number }[] = [
  { hz: 50_000, at384: 0.0, at192: 0.0 },
  { hz: 60_000, at384: -0.04, at192: -0.03 },
  { hz: 70_000, at384: -0.09, at192: -0.08 },
  { hz: 80_000, at384: -0.15, at192: -0.12 },
  { hz: 85_000, at384: -0.18, at192: -0.15 },
  { hz: 90_000, at384: -0.21, at192: -0.22 },
  { hz: 91_000, at384: -0.22, at192: -0.29 },
  { hz: 92_000, at384: -0.22, at192: -0.48 },
  { hz: 93_000, at384: -0.23, at192: -0.88 },
  { hz: 94_000, at384: -0.24, at192: -1.41 },
  { hz: 95_000, at384: -0.24, at192: -2.44 },
  { hz: 95_800, at384: -0.25, at192: -3.0 },
]

const twoRateSeries = computed<ManualSeries[]>(() => [
  {
    name: 'Captured at 384 kHz (Nyquist 192 kHz)',
    points: TWO_RATES.map((r) => [r.hz, r.at384] as const),
    color: 'coherence',
  },
  {
    name: 'Captured at 192 kHz (Nyquist 96 kHz)',
    points: TWO_RATES.map((r) => [r.hz, r.at192] as const),
    color: 'phase',
  },
])

const twoRateMarkers: ManualMarker[] = [
  { x: 90_000, label: '0.94 × Nyquist at 192 kHz', color: 'amber' },
  { x: 96_000, label: 'Nyquist at 192 kHz', color: 'grey' },
]

const bandwidthSeries = computed<ManualSeries[]>(() => [
  { name: 'Input alone, driven externally', points: legPoints('input'), color: 'coherence' },
  { name: '192 kHz loopback (generator + input)', points: legPoints('at192'), color: 'left' },
  { name: '384 kHz loopback (through a broken DAC)', points: legPoints('at384'), color: 'right', dashed: true },
])

const wideMarkers: ManualMarker[] = [
  { y: -3, label: '−3 dB', color: 'grey' },
  { x: 96_000, label: '96 kHz', color: 'amber' },
]
const closeMarkers: ManualMarker[] = [
  { y: -1, label: '−1 dB', color: 'grey' },
  { x: 96_000, label: '96 kHz', color: 'amber' },
]

function formatDb(v: number | null, decimals = 2): string {
  return v === null ? '—' : `${v < 0 ? '−' : ''}${Math.abs(v).toFixed(decimals)} dB`
}

function formatTone(hz: number): string {
  return hz >= 1000 ? `${hz / 1000} kHz` : `${hz} Hz`
}
</script>

<template>
  <div>
    <h1>The analyzer's own figures</h1>

    <p>
      Every number on this page was measured <em>here</em>, through this app, on a real QA403 — not
      copied from a datasheet. They are worth knowing because an analyzer's own limits are what every
      measurement of a device under test sits on top of: where a DUT's figure comes within a few dB
      of one of these, what you are reading is the pair of them and not the DUT.
    </p>

    <h2>Analog bandwidth</h2>
    <p>
      The filters either side of the converters are analog, so their corners are fixed in
      <em>hertz</em> and do not move when you change the sample rate. Which of them you are looking
      at depends entirely on how you measure: a <strong>loopback</strong> reads the generator and the
      input in series and cannot tell them apart, while an <strong>external generator</strong> driving
      the input with this analyzer's own generator silent reads the input path on its own. Both are
      below, each column relative to its own 1 kHz reading:
    </p>

    <table>
      <thead>
        <tr>
          <th>Tone</th>
          <th>Input alone</th>
          <th>192 kHz loopback</th>
          <th>384 kHz loopback</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in BANDWIDTH" :key="row.hz">
          <td>{{ formatTone(row.hz) }}</td>
          <td>{{ formatDb(row.input, 1) }}</td>
          <td>{{ formatDb(row.at192) }}</td>
          <td>{{ formatDb(row.at384) }}</td>
        </tr>
      </tbody>
    </table>
    <p>
      A dash is a tone that column could not reach — above a 192 kHz capture's own Nyquist, or past
      where the loopback had already fallen into its noise — and not a measurement that was skipped.
    </p>

    <ManualChart
      :series="bandwidthSeries"
      :markers="wideMarkers"
      :x-min="900"
      :x-max="200000"
      :y-min="-55"
      :y-max="3"
      x-label="Hz"
      y-label="Relative to 1 kHz (dB)"
      :height="280"
      caption="The same analyzer measured three ways, each curve relative to its own 1 kHz reading. The two loopbacks fall off a cliff above 90 kHz; the input, driven by an external generator with this analyzer's own generator silent, does not. The whole of that cliff therefore belongs to the DAC."
    />

    <ManualChart
      :series="bandwidthSeries"
      :markers="closeMarkers"
      :x-min="900"
      :x-max="200000"
      :y-min="-4"
      :y-max="0.5"
      x-label="Hz"
      y-label="Relative to 1 kHz (dB)"
      :height="240"
      caption="The same three measurements zoomed to the passband. Through the audio band all three agree and all three are flat; they separate above about 40 kHz, where the generator's reconstruction filter starts to roll off and the input does not."
    />

    <h3>The roll-off is the generator's, not the input's</h3>
    <p>
      A loopback measures the DAC and the ADC in series and cannot say which end lost what. Driving
      the input from an <em>external</em> generator, with this analyzer's own generator silent,
      removes the DAC from the product — and it turns out the DAC was the whole story:
    </p>
    <ul>
      <li>
        <strong>The input path is flat within about 1.2 dB to 180 kHz.</strong> It has no corner
        anywhere near 90 kHz. At 120 kHz it is 0.6 dB down where the loopback is 32 dB down; the
        difference, nearly 31 dB, is the reconstruction filter after the DAC.
      </li>
      <li>
        <strong>What does stop it is the ADC's digital decimation filter, right at Nyquist.</strong>
        The response is still only 1.2 dB down at 180 kHz and then falls hard, through −3 dB at about
        <strong>189 kHz</strong> — 98 % of the 192 kHz Nyquist of a 384 kHz capture. That is a
        digital brick wall, not an analog corner, and it moves with the sample rate rather than
        staying put. The section below separates the two and measures where each one lives.
      </li>
      <li>
        <strong>It also proves the converter really is clocking at 384 kHz.</strong> A tone at
        150 kHz read back as a tone at 150 kHz; a device secretly still sampling at 192 kHz would
        have folded it to 42 kHz. (The frame rate measuring 382.7 kHz was the first evidence; this is
        the direct one.)
      </li>
    </ul>

    <div class="manual-note">
      <p>
        <strong>How that was measured.</strong> Four sweeps from an external generator, recorded to
        WAV with <em>Record every acquisition</em> on and the analyzer's own generator silent at
        384 kHz: two from one generator (1 kHz upwards, carrying the 1 kHz reference, and 50 kHz to
        197 kHz) and two from a <strong>second, different generator</strong>, at two further levels.
      </p>
      <p>
        The second generator is the control, and it is what makes the figures above a measurement of
        the <em>analyzer</em> rather than of the generator. A sweep reads the source and the input in
        series, so a droop in one is indistinguishable from a droop in the other — until two
        unrelated sources produce the same droop. They do, to <strong>0.00 – 0.02 dB from 110 to
        170 kHz</strong>, with the knee at 185 and 189 kHz matching to within 0.08 dB. Generators do
        not share a roll-off by coincidence, so the roll-off is the input's. The three levels span
        <strong>10.4 dB</strong> and the curve does not move across them, which also rules out an
        input stage that compresses or slews at the top of the band — that would read as exactly this
        kind of HF droop, but only at the loud end.
      </p>
    </div>

    <h3>Which part is analog and which is digital</h3>
    <p>
      Two things roll the input off and they behave differently: an <strong>analog</strong> filter is
      fixed in hertz and sits where it sits, while the ADC's <strong>digital</strong> decimation
      filter is a fraction of Nyquist and therefore moves when you change the sample rate. Capturing
      the same external sweep at both rates tells them apart, because 90 kHz is 0.47 of Nyquist at
      384 kHz and 0.94 of it at 192 kHz. Each column below is referenced to its own 50 kHz:
    </p>

    <table>
      <thead>
        <tr>
          <th>Tone</th>
          <th>Captured at 384 kHz</th>
          <th>Captured at 192 kHz</th>
          <th>Difference</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in TWO_RATES" :key="row.hz">
          <td>{{ formatTone(row.hz) }}</td>
          <td>{{ formatDb(row.at384) }}</td>
          <td>{{ formatDb(row.at192) }}</td>
          <td>{{ formatDb(row.at192 - row.at384) }}</td>
        </tr>
      </tbody>
    </table>

    <ManualChart
      :series="twoRateSeries"
      :markers="twoRateMarkers"
      :x-min="48000"
      :x-max="100000"
      :y-min="-3.5"
      :y-max="0.3"
      x-label="Hz"
      y-label="Relative to 50 kHz (dB)"
      :height="260"
      caption="One sweep, two sample rates, the same real frequencies. The curves lie on top of each other until 90 kHz and then separate completely — so everything up to there is analog, and the cliff after it belongs to the 96 kHz Nyquist of the lower rate alone."
    />

    <p>
      Below <strong>90 kHz the two rates agree to 0.03 dB</strong>, so that gentle droop is fixed in
      hertz: analog, and present whatever rate you capture at. From 91 kHz the 192 kHz capture dives
      while the 384 kHz one carries on gliding, reaching <strong>−3 dB at 95.8 kHz</strong> — 99.8 %
      of its own Nyquist. That cliff is the decimation filter and nothing else.
    </p>
    <p>
      So the rule for either rate is the same: the input is honest to about
      <strong>0.94 of Nyquist</strong> — <strong>90 kHz</strong> when capturing at 192 kHz,
      <strong>180 kHz</strong> at 384 kHz — and falls off a cliff in the last few per cent. The
      analog part only ever costs a decibel or so, and never a corner.
    </p>

    <h3>What it means for a measurement</h3>
    <ul>
      <li>
        <strong>Whenever the QA403 drives the DUT</strong> — every sweep, chirp, THD and IMD
        measurement — the pair is what you get, so the 192 kHz loopback column is the right number:
        flat through the audio band, 0.6 dB down at 80 kHz, 1.1 dB at 90 kHz. A sweep to 80 kHz is
        honest as it stands; 80 – 96 kHz carries up to a decibel of the analyzer's own roll-off,
        unless the right channel is the reference, in which case it cancels.
      </li>
      <li>
        <strong>When something else drives the DUT, the reach is real.</strong> At 384 kHz the input
        will show you an ultrasonic source, a switching supply's residue or a transmitter out to
        about 180 kHz within a decibel. That is the whole point of the
        <button type="button" class="manual-link" @click="showPage('settings')">
          experimental 384 kHz rate
        </button>, and it is why that rate is input-only rather than merely a wider empty axis.
      </li>
      <li>
        <strong>At 192 kHz a capture is honest to about 90 kHz</strong> and then falls off its own
        cliff — 1.4 dB down by 94 kHz, 3 dB by 95.8 kHz. Read the top few kilohertz of a 192 kHz
        spectrum with that in mind; it is the decimation filter, not your DUT, and the analog front
        end is not what limits you at either rate.
      </li>
    </ul>

    <div class="manual-note">
      <p>
        <strong>Why the loopback frequency response reads flat to 0.01 dB.</strong> A chirp run with
        <em>Right channel is reference</em> on divides one input by the other, and both inputs sit
        behind the same converters and the same analog filters — so the shared path cancels and what
        is left is the two channels' <em>match</em>, not the analyzer's absolute bandwidth. That is a
        feature, and it is also how to measure a DUT out to 96 kHz without the roll-off above riding
        along: see
        <button type="button" class="manual-link" @click="showPage('frequency-response')">
          Frequency response
        </button>.
      </p>
    </div>

    <h2>Residuals, noise and ranges</h2>
    <p>
      Measured in loopback on serial 1077527945, firmware 60, on 2026-09-23 — the reference set this
      bench compares against. Your own unit will differ in the last decibel or two; what matters is
      the order of magnitude, which is what tells you whether a DUT reading is the DUT.
    </p>
    <SettingsTable
      :headings="['Figure', 'Measured']"
      :rows="[
        ['THD, 1 kHz at −20 dBV, 8 averages', '−118.6 dBc left, −113.9 dBc right (H2 −125.7, H5 −122.9 dBc)'],
        ['Noise density', '−159.3 / −158.7 dBV/√Hz — about 11 nV/√Hz'],
        ['Noise floor, median 200 Hz – 20 kHz', '−161 dBV/√Hz at input range 0, output range −12'],
        ['Two-tone IMD, SMPTE 60 Hz + 7 kHz at −6 dBV', '−110.2 dB relative to the carrier'],
        ['Multitone TD+N, 5 tones/octave at −20 dBV', '−91.0 dB left, −89.0 dB right'],
        ['Crosstalk', '−100.2 dB L→R and −99.8 dB R→L at 20 kHz; −118.6 / −128.0 dB at 1 kHz'],
        ['Input ranges', '0, 6, 12, 18, 24, 30, 36, 42 dBV full scale'],
        ['Output ranges', '−12, −2, 8, 18 dBV, chosen for you from the generator level'],
        ['Sample rates', '48, 96, 192 kHz — plus 384 kHz, input only, when it is enabled'],
        ['Generator ceiling', '192 kHz; above it both outputs are held silent'],
      ]"
    />
    <p>
      Both attenuators set the noise floor, so those two range rows are part of the noise figure and
      not a convenience: the output range in particular moves the floor by about 12 dB across its
      four settings, which is why a floor that jumps when you change the generator level is usually
      the range crossing a boundary rather than anything wrong. See
      <button type="button" class="manual-link" @click="showPage('settings')">
        Configuration and status bar
      </button>.
    </p>

    <div class="manual-note">
      <p>
        <strong>After changing the sample rate, give it half a minute.</strong> A QA403 re-locks its
        own clock when the rate changes, and it does so in discrete 20.3 ns steps of sampling phase
        for <strong>15 to 30 seconds</strong> afterwards. Each step that lands inside an FFT record
        puts a skirt on the tone, so close-in figures are unreliable while it settles: measured
        across one such window, THD+N read 0.0037 % against 0.0006 % settled and SNR 88.6 dB against
        104.4 dB, while THD, the level and the broadband floor were untouched. It is the analyzer, not
        your DUT, and nothing in the app hides it — averaging dilutes it, and waiting removes it.
      </p>
    </div>
  </div>
</template>
