<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { burstDecaySlice, csdSlices, impulseCurve, magnitudeCurve, phaseCurve } from '../../sim/curves'
import { BANDPASS_CENTRE_HZ, BANDPASS_DB_PER_PERIOD, BANDPASS_Q, bandpassFilter } from '../../sim/models'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Measured through the app on 2026-09-11 (24 kHz span, 1/24-octave smoothing, Peak reference):
// peak −0.34 dB at 1000.5 Hz, −3 dB 972 – 1029 Hz (57 Hz), ±1 dB 985 – 1015 Hz, −46 dB at 100 Hz
// and −47 dB at 10 kHz; recovered delay 1.44 samples. Smoothing is what lowers the peak and widens
// the −3 dB band from the exact 50 Hz: a 1/24-octave band at 1 kHz is 29 Hz wide.
const dut = bandpassFilter(48_000)
const bandwidthHz = BANDPASS_CENTRE_HZ / BANDPASS_Q

const frSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain, no smoothing (dB)', points: magnitudeCurve(dut, 4.4, 22_800, 1500), color: 'gain' },
  { name: 'Gain, 1/24 octave', points: magnitudeCurve(dut, 800, 1250, 400, 24), color: 'grey', dashed: true },
  { name: 'Phase (°)', points: phaseCurve(dut, 4.4, 22_800, 1500, 1.44), color: 'phase', axis: 'y2' },
])
const zoomSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain, no smoothing (dB)', points: magnitudeCurve(dut, 900, 1100, 400), color: 'gain' },
  { name: 'Gain, 1/24 octave', points: magnitudeCurve(dut, 900, 1100, 400, 24), color: 'grey', dashed: true },
])
const zoomMarkers: ManualMarker[] = [
  { y: -3, label: '−3 dB', color: 'left' },
  { x: BANDPASS_CENTRE_HZ - bandwidthHz / 2, label: '975 Hz', color: 'left' },
  { x: BANDPASS_CENTRE_HZ + bandwidthHz / 2, label: '1025 Hz', color: 'left' },
]

const impulseSeries = computed<ManualSeries[]>(() => [{ name: 'Impulse (relative to peak)', points: impulseCurve(dut, 1, 12), color: 'left' }])

const burstSeries = computed<ManualSeries[]>(() => [
  { name: '1 kHz', points: burstDecaySlice(dut, 1000, 30), color: 'left' },
  { name: `${BANDPASS_DB_PER_PERIOD.toFixed(2)} dB per period`, points: [[3, -2.4], [30, -2.4 - 27 * BANDPASS_DB_PER_PERIOD]], color: 'grey', dashed: true },
])

// A longer CSD window than the default, since the ring is 1 kHz and 20 ms long: 2048 samples at
// 48 kHz is 43 ms, 23 Hz per bin.
const csd = computed(() => csdSlices(dut, 2048, 0.2, [0, 2, 4, 6, 8, 10], 500, 2000, 200))
const csdSeries = computed<ManualSeries[]>(() =>
  csd.value.map((s, i) => ({ name: `${s.timeMs} ms`, points: s.points, color: (['gain', 'left', 'phase', 'coherence', 'amber', 'right'] as const)[i % 6] })),
)
</script>

<template>
  <div>
    <h1>Bandpass</h1>

    <p>
      One second-order band-pass at <strong>1 kHz</strong> with a Q of <strong>{{ BANDPASS_Q }}</strong>
      — {{ bandwidthHz.toFixed(0) }} Hz wide between its −3 dB points, unity at the centre, falling
      6 dB per octave either side — and nothing else in the path. It is not a picture of a device;
      it is a <strong>calibration target</strong>, the one model whose every reading you can work out
      on paper and then check the app against.
    </p>
    <p>
      The number it exists for is its decay. A second-order resonance's envelope falls as
      <code>e^(−ω₀t / 2Q)</code>, which over one period of its own centre frequency is
      <code>e^(−π/Q)</code>: <strong>27.29 / Q dB per period</strong>, here
      <strong>{{ BANDPASS_DB_PER_PERIOD.toFixed(2) }} dB</strong>, at every Q and every frequency.
      The burst decay must draw that slope. Because a period of 1 kHz is a millisecond, the
      cumulative spectral decay reads the same figure per millisecond — a coincidence of the choice
      of 1 kHz, and a convenient one.
    </p>

    <h2>What to expect, in numbers</h2>
    <SettingsTable
      :headings="['Reading', 'Value']"
      :rows="[
        ['Peak', '0.00 dB at 1000 Hz unsmoothed; −0.34 dB through 1/24-octave smoothing'],
        ['−3 dB points', `975 and 1025 Hz exactly (${bandwidthHz.toFixed(0)} Hz); 972 – 1029 Hz through 1/24-octave smoothing`],
        ['Skirts', '−29.6 dB at 500 Hz and 2 kHz, −46 dB at 100 Hz, −47 dB at 10 kHz'],
        ['Phase', '+90° well below, 0° at the centre, −90° well above'],
        ['Burst decay', `${BANDPASS_DB_PER_PERIOD.toFixed(2)} dB per period at 1 kHz; the app reads 1.369`],
        ['CSD', `${BANDPASS_DB_PER_PERIOD.toFixed(2)} dB per millisecond along the 1 kHz ridge`],
        ['THD', 'the generator’s 0.51 % at 1 kHz; elsewhere the fundamental is attenuated and the harmonics are not, so THD climbs steeply either side'],
      ]"
    />

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — the burst decay and CSD, checked against arithmetic.</li>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — a peak, and the case for the <em>Peak</em> bandwidth reference.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — the same peak, and a reason to raise steps per octave.</li>
      <li><a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> — a 1 kHz tone passes untouched; move it to 500 Hz and it comes back 29.6 dB down with its harmonics 6 dB per octave less attenuated.</li>
      <li>
        <a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — a deep V
        with its floor at 1 kHz: away from the centre the fundamental is attenuated far more than
        its harmonics, so the ratio grows. Not distortion; the response acting on the ratio.
      </li>
      <li><a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable.</li>
    </ul>

    <h2>Frequency response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Chirp start (Hz)', '2'],
        ['Resolution (Hz)', '1.46'],
        ['Right channel is reference', 'On'],
        ['Smoothing', 'None (the default 1/24 octave lowers the peak by 0.3 dB — see below)'],
        ['Averages', '1'],
        ['Bandwidth reference', 'Peak'],
      ]"
    />
    <ManualChart
      :series="frSeries"
      :x-min="4"
      :x-max="24000"
      :y-min="-60"
      :y-max="6"
      :y2-min="-180"
      :y2-max="180"
      :y2-step="90"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Phase (°)"
      :height="280"
      caption="Expected Bode plot: a single peak with 6 dB per octave skirts and a phase that turns through 180° across it. The dashed grey is the same peak through the default 1/24-octave smoothing."
    />
    <ManualChart
      :series="zoomSeries"
      :markers="zoomMarkers"
      :x-min="900"
      :x-max="1100"
      :y-min="-12"
      :y-max="1"
      x-label="Hz"
      y-label="Gain (dB)"
      :height="200"
      caption="The same peak between 900 and 1100 Hz: the −3 dB points at exactly 975 and 1025 Hz unsmoothed; through 1/24-octave smoothing the top is 0.34 dB lower and the −3 dB band 57 Hz wide."
    />
    <p>
      This is the model that makes the case for the <strong>Peak</strong> bandwidth reference: there
      is no passband to take a median over. Under the default passband median the reference lands at
      −25 dB (the median of the skirts between 200 Hz and 2 kHz) and the "−3 dB band" reads
      549 Hz – 1.8 kHz, which describes the skirts. Under Peak, the ±1 dB and −3 dB rows are the
      resonance's own widths — 985 – 1015 Hz and 972 – 1029 Hz through the default smoothing — and
      the symmetric ±1 dB search is the same as a one-sided one, since nothing in a trace exceeds
      its own maximum.
    </p>
    <p>
      And it is the model that shows what <strong>Smoothing</strong> costs on a sharp feature. The
      default 1/24 octave is 29 Hz wide at 1 kHz, against a {{ bandwidthHz.toFixed(0) }} Hz peak:
      the top reads −0.34 dB instead of 0.00 and the −3 dB band 57 Hz instead of 50. That is the
      power average of a peak with its own shoulders. Set Smoothing to None and both numbers come
      back exactly; on the marginally stable model, whose peak is 2 Hz wide, the same setting hides
      10 dB of it.
    </p>

    <h2>Impulse response</h2>
    <SettingsTable
      :rows="[
        ['Set generator by', 'Output voltage, 0 dBV'],
        ['Frequency span (Hz)', '24000'],
        ['Divide by', 'Nothing (the generated sweep)'],
        ['Gate: Before (ms) / After (ms)', '1 / 20 — long enough to hold the whole ring'],
        ['Gate window', 'Hann'],
        ['Burst decay', 'Prefer frequency, 30 periods, 8 points per period'],
        ['Cumulative spectral decay', 'Apodizing window, 2048 samples (43 ms), 0.2 ms rise'],
      ]"
    />
    <ManualChart
      :series="impulseSeries"
      :x-log="false"
      :x-min="-1"
      :x-max="12"
      :y-min="-1.05"
      :y-max="1.05"
      x-label="ms"
      y-label="relative to peak"
      :height="220"
      caption="Expected impulse response: a 1 kHz ring decaying 1.36 dB per cycle, down to about 15 % of its start after 12 cycles."
    />
    <ManualChart
      :series="burstSeries"
      :x-log="false"
      :x-min="0"
      :x-max="30"
      :y-min="-45"
      :y-max="2"
      x-label="periods"
      y-label="dB"
      :height="220"
      caption="Expected burst decay at 1 kHz (Prefer frequency) against a line of exactly 1.36 dB per period: the wavelet opens up over the first three periods, then the two are parallel. The screen's readout for this slope is 1.369."
    />
    <p>
      The straight part is the number. Read the slope anywhere after the first three periods — the
      wavelet is four periods wide at half maximum, so the first few points are the analysis opening
      rather than the filter decaying — and it is {{ BANDPASS_DB_PER_PERIOD.toFixed(2) }} dB per
      period within a hundredth. Nothing else in the app can be checked this directly, which is why
      any change to this model (a second section, a roll-off, a reflection) destroys the only number
      in it that is verified against arithmetic rather than against itself.
    </p>
    <ManualChart
      :series="csdSeries"
      :x-min="500"
      :x-max="2000"
      :y-min="-40"
      :y-max="2"
      x-label="Hz"
      y-label="dB"
      :height="240"
      caption="Expected CSD slices at 0, 2, 4, 6, 8 and 10 ms under a 43 ms apodizing window: one ridge at 1 kHz, stepping down 2.7 dB every 2 ms — 1.36 dB per millisecond, the same figure, because a period of 1 kHz is a millisecond."
    />
    <p>
      The default 5 ms CSD window is too short for this: at 187 Hz per bin the ridge is a broad
      hump and the slices blur the decay over the window's own length. Lengthen it to 2048 samples
      and the ridge sharpens to its true width while each slice still steps down the same
      {{ BANDPASS_DB_PER_PERIOD.toFixed(2) }} dB per millisecond. That trade — resolution against
      time blur — is the CSD's only real setting, and this model is where you can see both sides of
      it against a known answer.
    </p>
  </div>
</template>
