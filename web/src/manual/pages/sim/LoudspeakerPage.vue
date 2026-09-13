<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import {
  burstDecaySlice,
  csdSlices,
  gatedMagnitudeCurve,
  impulseCurve,
  magnitudeCurve,
  phaseCurve,
} from '../../sim/curves'
import { LOUDSPEAKER_REFLECTION, loudspeaker } from '../../sim/models'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app against this model on 2026-09-11 (24 kHz span, 1/24-octave smoothing):
// +4.59 dB at 230 Hz, −2.67 at 2 kHz, +7.25 at 4.18 kHz, −0.24 at 8 kHz, +1.24 at 15 kHz, −15.3
// at 22 kHz; recovered delay 0.34 samples. The smoothed curves below agree to within 0.3 dB; the
// unsmoothed response differs by several dB above 5 kHz, and the difference is the reflection's
// comb, which 1/24 octave averages away above the frequency where its band spans a comb period.
const dut = loudspeaker(48_000)
const SMOOTHING = 24
const FR_LOW = 4.4
const FR_HIGH = 22_800

const frSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain, 1/24 octave (dB)', points: magnitudeCurve(dut, FR_LOW, FR_HIGH, 500, SMOOTHING), color: 'gain' },
  { name: 'Gain, unsmoothed', points: magnitudeCurve(dut, 2000, FR_HIGH, 1200), color: 'grey' },
  { name: 'Phase (°)', points: phaseCurve(dut, FR_LOW, FR_HIGH, 500, 0.34), color: 'phase', axis: 'y2' },
])
const frMarkers = computed<ManualMarker[]>(() => [
  { x: 230, label: 'panel 230 Hz', color: 'amber' },
  { x: 4200, label: 'breakup 4.2 kHz', color: 'amber' },
])

const combSpacingHz = 1000 / LOUDSPEAKER_REFLECTION.delayMs

// Impulse: the direct sound, then the wall 3 ms later, inverted and 12 dB down.
const impulseSeries = computed<ManualSeries[]>(() => [
  { name: 'Impulse (relative to peak)', points: impulseCurve(dut, 1, 8), color: 'left' },
])
const impulseMarkers: ManualMarker[] = [{ x: LOUDSPEAKER_REFLECTION.delayMs, label: 'reflection', color: 'amber' }]

// Gated response: a gate that stops short of the wall.
const GATE_PRE_MS = 1
const GATE_POST_MS = 2.5
const gateResolutionHz = 1000 / (GATE_PRE_MS + GATE_POST_MS)
const gatedSeries = computed<ManualSeries[]>(() => [
  { name: `Gated, 1 / 2.5 ms`, points: gatedMagnitudeCurve(dut, GATE_PRE_MS, GATE_POST_MS, 100, FR_HIGH, 300), color: 'left' },
  { name: 'Ungated, 1/24 octave', points: magnitudeCurve(dut, 100, FR_HIGH, 400, SMOOTHING), color: 'grey', dashed: true },
])
const gatedMarkers = computed<ManualMarker[]>(() => [{ x: gateResolutionHz, label: `resolution limit ${gateResolutionHz.toFixed(0)} Hz`, color: 'amber' }])

// CSD: the default 5 ms apodizing window at 48 kHz (256 samples, 0.2 ms rise), five of its slices.
const csd = computed(() => csdSlices(dut, 256, 0.2, [0, 0.5, 1, 2, 3.5], 300, 20_000, 160))
const csdSeries = computed<ManualSeries[]>(() =>
  csd.value.map((s, i) => ({
    name: `${s.timeMs} ms`,
    points: s.points,
    color: (['gain', 'left', 'phase', 'coherence', 'amber'] as const)[i % 5],
  })),
)

// Burst decay: the two resonances' own slices, thirty periods deep.
const burstSeries = computed<ManualSeries[]>(() => [
  { name: '230 Hz panel', points: burstDecaySlice(dut, 230, 30), color: 'left' },
  { name: '4.2 kHz breakup', points: burstDecaySlice(dut, 4200, 30), color: 'right' },
  { name: '1 kHz (nothing rings)', points: burstDecaySlice(dut, 1000, 30), color: 'grey', dashed: true },
])
const reflectionPeriodsAt4k2 = (LOUDSPEAKER_REFLECTION.delayMs / 1000) * 4200

// No THD chart: with the simulator's Harmonics box off (the default) this linear model reads
// 0.000 % everywhere, and the figures quoted for the box ticked are the generator's stack bent by
// the response above, not a property of the loudspeaker.
</script>

<template>
  <div>
    <h1>Loudspeaker</h1>

    <p>
      A two-way loudspeaker in a room, as a chain of the things a loudspeaker measurement has to
      cope with: a sealed-box roll-off below <strong>45 Hz</strong>; a cabinet panel resonance at
      <strong>230 Hz</strong> (Q 20, +4 dB) that rings long after the signal has gone; the
      crossover's dip at 900 Hz; the cone's first breakup mode at <strong>4.2 kHz</strong> (Q 12,
      +6 dB); a tweeter roll-off above 19 kHz — and a <strong>wall reflection 3 ms</strong> behind the
      direct sound, inverted and 12 dB down.
    </p>
    <p>
      It is the model for the <strong>impulse-response screen</strong>. The reflection is what the
      display gate exists to remove, and the two resonances are what the decay surfaces exist to
      show; on the frequency-response screen the same reflection appears as a comb across the whole
      band, {{ combSpacingHz.toFixed(0) }} Hz between teeth, which is why no single frequency of this
      model reads its own section's gain. Linear, so it has no distortion of its own.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Peak', '+7.2 dB at 4.18 kHz (1/24-octave smoothed); the panel reads +4.6 dB at 230 Hz'],
        ['Reflection comb', `${combSpacingHz.toFixed(0)} Hz spacing, ±2.2 dB unsmoothed, averaging to +0.26 dB under 1/24-octave smoothing above about 5 kHz`],
        ['Impulse', 'the direct spike at t = 0, an inverted copy 12 dB down at 3 ms'],
        ['Burst decay', 'the panel loses 1.1 dB per period of 230 Hz; the breakup 1.6 dB per period of 4.2 kHz until the reflection arrives'],
        ['THD', '0.000 %; with Harmonics ticked, the generator’s 0.51 % bent by the response at the resonances'],
        ['DUT delay', 'about 0.3 samples'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — the one it is for: the gate, the gated response, both decay surfaces.</li>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — the ungated picture, comb and all.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — the same curve, each point landing somewhere on the comb.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — zero; with Harmonics ticked, the generator's stack bent by the response.</li>
      <li>
        <a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> and
        <a href="#" @click.prevent="showPage('thd-vs-amplitude')">THD vs amplitude</a> — as the
        <button type="button" class="manual-link" @click="showPage('sim-tube-amp')">Tube amp</button>, with this model's gain at the tone's frequency in place of that one's.
      </li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable; the <button type="button" class="manual-link" @click="showPage('sim-fixture-loudspeaker')">loudspeaker fixture</button> is the impedance model.</li>
    </ul>

    <h2>Frequency response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Chirp start (Hz)', '2'],
        ['Resolution (Hz)', '1.46'],
        ['Right channel is reference', 'On'],
        ['Smoothing', '1/24 octave'],
        ['Averages', '1'],
        ['Bandwidth reference', 'Passband median, 200 – 2000 Hz'],
      ]"
    />
    <ManualChart
      :series="frSeries"
      :markers="frMarkers"
      :x-min="4"
      :x-max="24000"
      :y-min="-24"
      :y-max="12"
      :y2-min="-180"
      :y2-max="180"
      :y2-step="90"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Phase (°)"
      :height="280"
      caption="Expected Bode plot at 1/24-octave smoothing (black), with the unsmoothed response above 2 kHz in grey to show the comb the smoothing removes, and phase with the recovered delay taken out."
    />
    <p>
      Three things to notice. <strong>The smoothing is doing real work here.</strong> Below about
      3 kHz the 1/24-octave band is narrower than the comb's {{ combSpacingHz.toFixed(0) }} Hz, so
      the trace still wobbles with it; above, the band spans a whole tooth and the comb averages to
      a steady +0.26 dB. Set Smoothing to None and the comb is drawn tooth by tooth to 22 kHz.
      <strong>The bandwidth rows are nonsense</strong>, correctly: a passband median over
      200 Hz – 2 kHz of a response that swings from +4.6 to −2.7 dB across that band lands at
      −1.2 dB, the ±1 dB row finds a band a bin wide around 630 Hz, and the −3 dB row reads
      37 Hz – 666 Hz. The screen has no way to know a loudspeaker has no passband; you do. And
      <strong>the peak the panel reports is the breakup, not the panel</strong>: +7.2 dB at
      4.18 kHz, against +4.6 dB at 230 Hz — the 230 Hz section's own +4 dB reads higher than its
      coefficient because a comb tooth sits on it, which is the loudspeaker's answer to "why does no
      section read its own gain".
    </p>

    <h2>Impulse response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Divide by', 'Nothing (the generated sweep)'],
        ['Gate: Before (ms) / After (ms)', '1 / 2.5'],
        ['Gate window', 'Hann'],
        ['Extract harmonic distortion', 'On, up to 5'],
        ['Display', 'Impulse response, volts; then Gated response, Cumulative spectral decay and Burst decay in turn'],
      ]"
    />
    <ManualChart
      :series="impulseSeries"
      :markers="impulseMarkers"
      :x-log="false"
      :x-min="-1"
      :x-max="8"
      :y-min="-0.6"
      :y-max="1.05"
      x-label="ms"
      y-label="relative to peak"
      :height="220"
      caption="Expected impulse response: the direct sound at t = 0, ringing briefly at 4.2 kHz, then the wall's inverted copy at 3 ms, 12 dB down and ringing the same way. The 230 Hz panel is the slow undulation underneath both."
    />
    <p>
      The gate above — 1 ms before, 2.5 ms after — stops just short of the reflection, which is the
      whole reason for choosing it: the <strong>Gate</strong> panel prints how far a reflection can
      travel and still fall inside the window (sound covers about 34 cm per millisecond), and 3 ms
      is about a metre of extra path. It also prints the price, a resolution limit of
      <strong>{{ gateResolutionHz.toFixed(0) }} Hz</strong>, below which the gated response describes
      the window rather than the loudspeaker.
    </p>

    <h3>Gated response</h3>
    <ManualChart
      :series="gatedSeries"
      :markers="gatedMarkers"
      :x-min="100"
      :x-max="24000"
      :y-min="-24"
      :y-max="12"
      x-label="Hz"
      y-label="Gain (dB)"
      :height="240"
      caption="Expected gated response (blue) against the ungated, smoothed chirp (dashed): the comb is gone, the breakup peak is broadened and lowered by the 286 Hz resolution, and below the limit the curve says nothing about the loudspeaker — the 230 Hz panel has vanished, not because it is not there."
    />
    <p>
      This is the anechoic measurement made in a room. The comb has gone because its cause never
      reached the window; what it cost is everything below {{ gateResolutionHz.toFixed(0) }} Hz and
      some of the breakup's height (a Q-12 peak is 350 Hz wide, and a 286 Hz window cannot quite
      resolve it — it reads about +3.8 dB rather than +6.5). Widen <em>After</em> past 3 ms and the
      reflection comes back into the window: the comb reappears, tooth by tooth, at exactly
      {{ combSpacingHz.toFixed(0) }} Hz spacing.
    </p>

    <h3>Cumulative spectral decay</h3>
    <ManualChart
      :series="csdSeries"
      :x-min="300"
      :x-max="20000"
      :y-min="-50"
      :y-max="2"
      x-label="Hz"
      y-label="dB"
      :height="260"
      caption="Five slices of the expected CSD under the default 5 ms window (0.2 ms rise) at 48 kHz: the front slice is the response; the breakup at 4.2 kHz outlives everything around it; at 3.5 ms the reflection arrives and lifts the whole slice again."
    />
    <p>
      The screen draws forty of these as a waterfall. Read it for what survives: the broadband
      response falls away within a millisecond, the 4.2 kHz ridge stays, and at 3 ms the reflection
      re-excites the whole band — a second impulse reads as a second front on the surface. The
      230 Hz panel is <em>not</em> visible here, and that is the CSD's resolution trade: a 5 ms
      window resolves 187 Hz, which cannot separate a 230 Hz ridge from the response around it.
      Lengthen the window to see it, at the cost of blurring the decay over the window's own
      length; or use the burst decay, which is built for exactly this.
    </p>

    <h3>Burst decay</h3>
    <ManualChart
      :series="burstSeries"
      :x-log="false"
      :x-min="0"
      :x-max="30"
      :y-min="-60"
      :y-max="2"
      x-label="periods"
      y-label="dB"
      :height="240"
      caption="Expected burst decay at three frequencies (Prefer frequency, 30 periods): the panel decays 1.1 dB per period of 230 Hz; the breakup 1.6 dB per period of 4.2 kHz until the reflection re-excites it 12.6 periods in; 1 kHz is the analysis wavelet alone and drops away at once."
    />
    <p>
      Each frequency is normalised to its own maximum and counted in its own periods, which is what
      lets a 230 Hz ring and a 4.2 kHz ring be compared at all — in milliseconds the panel's decay
      would look eighteen times slower. Two features that are not defects: the maximum sits a period
      or two in, because a symmetric wavelet centred on the impulse sees only half of a causal ring
      and has to open up; and the 4.2 kHz curve <em>rises</em> again at
      {{ reflectionPeriodsAt4k2.toFixed(1) }} periods, which is the 3 ms reflection arriving in the
      breakup's band. The panel's slope is 27.29 / Q for the section's pole Q of 25 — a +4 dB peaking
      section at Q 20 has its poles at Q 25 — i.e. 1.1 dB per period.
    </p>

    <h2>THD vs frequency</h2>
    <p>
      Reads <strong>0.000 %</strong> across the band: the model is linear. Tick the simulator's
      <strong>Harmonics</strong> box and the sweep (20 Hz – 20 kHz at −20 dBV, five steps per
      octave) draws the generator's 0.51 % stack read through a response that treats each harmonic
      differently from its fundamental — the response's shape folded onto itself. THD reads
      <em>high</em> at 115 Hz, whose H2 lands on the 230 Hz panel, low at 230 Hz itself, high again
      at 2.1 kHz, whose H2 lands on the breakup, and climbs at the bottom where the fundamental is
      rolling off faster than its harmonics. None of it is distortion in the loudspeaker — the
      right channel, which taps the generator, reads a flat 0.51 % throughout, and switching to a
      nonlinear model is the only way to make the two channels differ.
    </p>
  </div>
</template>
