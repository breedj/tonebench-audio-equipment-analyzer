<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import {
  bandwidth,
  harmonicVsFrequencyCurve,
  impulseCurve,
  linearThdPercent,
  logSpace,
  magnitudeCurve,
  noiseFloorDbvPerBin,
  phaseCurve,
  thdVsFrequencyCurve,
  toneStems,
} from '../../sim/curves'
import { HUM_HARMONICS, tubeAmplifier } from '../../sim/models'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// Every curve on this page is computed from the model's own transfer function at the sample rate
// the example settings imply — see sim/models.ts, which mirrors SimulatedDut.For coefficient for
// coefficient. Numbers quoted in the text were also measured through the app against the
// simulator on 2026-09-11 and agree with these curves to within a bin.

// --- Frequency response: 96 kHz span, i.e. 192 kHz, analysed 4.4 Hz – 91.2 kHz. --------------
const fr = tubeAmplifier(192_000)
const FR_LOW = 4.4
const FR_HIGH = 91_200
const frBandwidth = bandwidth(fr, FR_LOW, FR_HIGH)
// The screen removes the delay it recovers from the impulse peak — about 0.97 samples against this
// model, the shift the 46 kHz low-pass puts on the peak — and its phase trace is the model's phase
// with exactly that linear term taken back out.
const FR_REMOVED_DELAY_SAMPLES = 0.97

const frSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain (dB)', points: magnitudeCurve(fr, FR_LOW, FR_HIGH), color: 'gain' },
  {
    name: 'Phase (°)',
    points: phaseCurve(fr, FR_LOW, FR_HIGH, 400, FR_REMOVED_DELAY_SAMPLES),
    color: 'phase',
    axis: 'y2',
  },
])
const frMarkers = computed<ManualMarker[]>(() => [
  { y: frBandwidth.referenceDb, label: 'ref', color: 'grey', dashed: false },
  { y: frBandwidth.referenceDb - 3, color: 'left' },
  { y: frBandwidth.referenceDb + 1, color: 'amber' },
  { y: frBandwidth.referenceDb - 1, color: 'amber' },
  { x: frBandwidth.threeDbLowHz!, label: `−3 dB ${formatHz(frBandwidth.threeDbLowHz!)}`, color: 'left' },
  { x: frBandwidth.threeDbHighHz!, label: `−3 dB ${formatHz(frBandwidth.threeDbHighHz!)}`, color: 'left' },
  { x: frBandwidth.oneDbLowHz!, label: `±1 dB ${formatHz(frBandwidth.oneDbLowHz!)}`, color: 'amber' },
  { x: frBandwidth.oneDbHighHz!, label: `±1 dB ${formatHz(frBandwidth.oneDbHighHz!)}`, color: 'amber' },
])

// The same amplifier at 48 kHz — a 24 kHz span on the chirp, and what adaptive resolution picks for
// a stepped sweep ending at 20 kHz. The model will not place a section above 45 % of the sample
// rate (Biquad.Prepare clamps it), so at 48 kHz both high-frequency sections land at 21.6 kHz and
// the resonance is squashed against the top of the band instead of disappearing above it.
const fr48 = tubeAmplifier(48_000)
const fr48Bandwidth = bandwidth(fr48, FR_LOW, 22_800)
const fr24Series = computed<ManualSeries[]>(() => [
  { name: '24 kHz span', points: magnitudeCurve(fr48, FR_LOW, 22_800), color: 'left' },
  { name: '96 kHz span', points: magnitudeCurve(fr, FR_LOW, FR_HIGH), color: 'gain', dashed: true },
])

// --- Stepped sine: 20 Hz – 20 kHz, 5 steps per octave, adaptive resolution (48 kHz). ---------
// The stepped screen's phase is the plain left/right phase, with no delay removed.
const steppedFreqs = logSpace(20, 20_000, Math.round(Math.log2(1000) * 5) + 1)
const steppedSeries = computed<ManualSeries[]>(() => [
  { name: 'Gain (dB)', points: steppedFreqs.map((f) => [f, fr48.magnitudeDb(f)] as const), color: 'gain', kind: 'points' },
  {
    name: 'Phase (°)',
    points: phaseCurve(fr48, 20, 20_000, steppedFreqs.length),
    color: 'phase',
    axis: 'y2',
    kind: 'points',
  },
])

// --- FFT: 1 kHz at −6 dBV, 192 kHz, 65536, Hann, 8 linear averages. --------------------------
const FFT_RATE = 192_000
const FFT_SIZE = 65536
const fftFloorDbv = noiseFloorDbvPerBin(FFT_RATE, FFT_SIZE)
const fftSeries = computed<ManualSeries[]>(() => [
  { name: 'Tone and its harmonics', points: toneStems(fr, 1000, -6, FFT_RATE / 2), color: 'left', kind: 'stem' },
  { name: 'Hum', points: HUM_HARMONICS.map((h) => [h.freqHz, h.dbv] as const), color: 'amber', kind: 'stem' },
  {
    name: 'Noise floor (per bin)',
    points: [
      [5, fftFloorDbv + 6],
      [30, fftFloorDbv + 2],
      [300, fftFloorDbv],
      [FFT_RATE / 2, fftFloorDbv],
    ],
    color: 'grey',
  },
])

// --- THD vs frequency: 20 Hz – 20 kHz at −20 dBV; the sweep picks 192 kHz for that end frequency,
//     and counts harmonics up to 98 % of Nyquist. ---------------------------------------------
const THD_MAX_HARMONIC_HZ = 0.98 * 96_000
const thdSeries = computed<ManualSeries[]>(() => [
  { name: 'THD', points: thdVsFrequencyCurve(fr, 20, 20_000, THD_MAX_HARMONIC_HZ), color: 'gain' },
  { name: 'D2', points: harmonicVsFrequencyCurve(fr, 2, 20, 20_000, THD_MAX_HARMONIC_HZ), color: 'left' },
  { name: 'D3', points: harmonicVsFrequencyCurve(fr, 3, 20, 20_000, THD_MAX_HARMONIC_HZ), color: 'right' },
])

// --- THD vs amplitude: 1 kHz, 1 mV – 1 V. A linear DUT under a level-independent stack. ------
const amplitudeVolts = logSpace(0.001, 1, Math.round(Math.log2(1000) * 5) + 1)
const thdAmplitudeSeries = computed<ManualSeries[]>(() => {
  const thdAt1k = linearThdPercent(fr, 1000, THD_MAX_HARMONIC_HZ)
  return [
    { name: 'THD', points: amplitudeVolts.map((v) => [v, thdAt1k] as const), color: 'gain' },
    { name: 'D2', points: amplitudeVolts.map((v) => [v, 100 * Math.pow(10, -46 / 20)] as const), color: 'left' },
    { name: 'D3', points: amplitudeVolts.map((v) => [v, 100 * Math.pow(10, -60 / 20)] as const), color: 'right' },
  ]
})

// --- Impulse response: the same 96 kHz sweep, drawn ±0.3 ms around the peak. ------------------
const impulseSeries = computed<ManualSeries[]>(() => [
  { name: 'Impulse (relative to peak)', points: impulseCurve(fr, 0.05, 0.3), color: 'left' },
])

function db(v: number, digits = 1): string {
  return `${v >= 0 ? '+' : '−'}${Math.abs(v).toFixed(digits)} dB`
}
</script>

<template>
  <div>
    <h1>Tube amp</h1>

    <p>
      A push-pull valve amplifier with an output transformer, reduced to the four things a transformer
      does to a response. At the bottom, the primary inductance rolls the response off below
      <strong>12 Hz</strong> and the feedback loop around it puts a small lift of about
      <strong>+1.8 dB at 45 Hz</strong>. At the top, the transformer's leakage inductance resonates
      against its winding capacitance <strong>4 dB up at 38 kHz</strong>, and above that the response
      falls away past <strong>46 kHz</strong>. In between it is flat and unity: a 1 kHz tone comes
      back at the level it went in.
    </p>
    <p>
      It is a <em>linear</em> model — it has a frequency response and nothing else. Its distortion
      is therefore the simulated generator's own harmonic stack (H2 −46 dBc, H3 −60 dBc), the same at
      every level, passed through that response. The interesting part of it sits
      <strong>above 24 kHz</strong>, which is the argument for measuring an amplifier at a 96 kHz
      span in one picture: at 24 kHz the resonance is invisible and the amplifier looks flat to the
      top of the band.
    </p>

    <h2>What to expect, in numbers</h2>
    <table>
      <thead>
        <tr>
          <th>Reading</th>
          <th>Value</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Gain in the passband</td>
          <td>0 dB, 100 Hz – 10 kHz within 0.1 dB.</td>
        </tr>
        <tr>
          <td>−3 dB corners</td>
          <td>{{ formatHz(frBandwidth.threeDbLowHz!) }} and {{ formatHz(frBandwidth.threeDbHighHz!) }}, from the passband median.</td>
        </tr>
        <tr>
          <td>±1 dB band</td>
          <td>
            {{ formatHz(frBandwidth.oneDbLowHz!) }} – {{ formatHz(frBandwidth.oneDbHighHz!) }}. The lower end
            is the 45 Hz lift leaving the band upward, not roll-off.
          </td>
        </tr>
        <tr>
          <td>Peak</td>
          <td>{{ db(fr.magnitudeDb(38_000)) }} at 38 kHz.</td>
        </tr>
        <tr>
          <td>THD, 1 kHz, any level</td>
          <td>0.51 % — the generator's stack. About 0.6 % at 20 Hz and 0.7 % at 20 kHz, see below.</td>
        </tr>
        <tr>
          <td>DUT delay</td>
          <td>About 1 sample: the 46 kHz low-pass shifts the impulse peak by that much.</td>
        </tr>
      </tbody>
    </table>

    <h2>Measurements</h2>
    <ul>
      <li><a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> — the one it is for.</li>
      <li><a href="#" @click.prevent="showPage('stepped-response')">Stepped sine response</a> — the same curve, slowly.</li>
      <li><a href="#" @click.prevent="showPage('fft-spectrum')">FFT analysis</a> — a tone through a flat passband.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — flat, with the response acting on the harmonics at each end.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-amplitude')">THD vs amplitude</a> — flat, and why.</li>
      <li><a href="#" @click.prevent="showPage('impulse-response')">Impulse response</a> — a spike with a very short ring.</li>
      <li>
        <a href="#" @click.prevent="showPage('impedance')">Impedance</a> — not applicable: this is a device, not a fixture.
        Selecting it on the impedance screen measures the right input, which carries a straight wire.
      </li>
    </ul>

    <h2>Frequency response</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>Setting</th>
        </tr>
      </thead>
      <tbody>
        <tr><td>Set generator by</td><td>Output voltage</td></tr>
        <tr><td>Amplitude</td><td>0 dBV</td></tr>
        <tr><td>Frequency span (Hz)</td><td>96000</td></tr>
        <tr><td>Chirp start (Hz)</td><td>2</td></tr>
        <tr><td>Resolution (Hz)</td><td>1.46</td></tr>
        <tr><td>Right channel is reference</td><td>On</td></tr>
        <tr><td>Smoothing</td><td>1/24 octave</td></tr>
        <tr><td>Averages</td><td>1</td></tr>
        <tr><td>Bandwidth reference</td><td>Passband median, 200 – 2000 Hz</td></tr>
        <tr><td>Display</td><td>Projection Bode, view Gain (dB), phase and bandwidth markers on</td></tr>
      </tbody>
    </table>
    <p>These are the screen's defaults; press <strong>SINGLE</strong>. One record of 0.68 s is measured after one is discarded.</p>

    <ManualChart
      :series="frSeries"
      :markers="frMarkers"
      :x-min="4"
      :x-max="96000"
      :y-min="-24"
      :y-max="8"
      :y2-min="-180"
      :y2-max="180"
      :y2-step="90"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Phase (°)"
      :height="280"
      caption="Expected Bode plot: gain (black) with the reference line and the corners the screen marks, and phase (purple) with the recovered delay removed."
    />

    <p>The measurements panel should read, to within a bin:</p>
    <table>
      <thead>
        <tr>
          <th>Row</th>
          <th>Lower</th>
          <th>Upper</th>
          <th>Width</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>±1 dB</td>
          <td>{{ formatHz(frBandwidth.oneDbLowHz!) }}</td>
          <td>{{ formatHz(frBandwidth.oneDbHighHz!) }}</td>
          <td>{{ formatHz(frBandwidth.oneDbHighHz! - frBandwidth.oneDbLowHz!) }}</td>
        </tr>
        <tr>
          <td>−3 dB</td>
          <td>{{ formatHz(frBandwidth.threeDbLowHz!) }}</td>
          <td>{{ formatHz(frBandwidth.threeDbHighHz!) }}</td>
          <td>{{ formatHz(frBandwidth.threeDbHighHz! - frBandwidth.threeDbLowHz!) }}</td>
        </tr>
        <tr>
          <td>Peak</td>
          <td colspan="3">{{ db(fr.magnitudeDb(38_000)) }} at about 37.8 kHz; reference {{ db(frBandwidth.referenceDb, 2) }}</td>
        </tr>
      </tbody>
    </table>
    <p>What to notice:</p>
    <ul>
      <li>
        <strong>The two rows disagree about the top end, correctly.</strong> The ±1 dB row stops at
        {{ formatHz(frBandwidth.oneDbHighHz!) }}, where the response climbs <em>out</em> of the band
        towards the resonance; the −3 dB row walks over the resonance, because the response has not
        fallen 3 dB anywhere before {{ formatHz(frBandwidth.threeDbHighHz!) }}.
      </li>
      <li>
        <strong>The ±1 dB lower corner is the 45 Hz lift, not roll-off.</strong> The response is
        still above the reference there. Set the reference band to something that includes it and
        the median moves; that is what the band is for.
      </li>
      <li>
        <strong>Switch Bandwidth reference to Peak</strong> and every corner lands on the resonance's
        own skirts: a "bandwidth" about 1.5 kHz wide around 38 kHz. That is a correct measurement of
        the resonance and says nothing about the amplifier, which is why the default is the passband
        median.
      </li>
      <li>
        <strong>Coherence reads 1.000 across the band</strong> with the simulator's −150 dBV/√Hz
        floor; it only means something from two averages up, and even then this DUT gives it nothing
        to disagree about.
      </li>
      <li>
        <strong>The phase has a slight upward tilt through the passband</strong> because the screen
        removed about a sample of delay. Restore removed delay on the Nyquist view puts it back.
      </li>
    </ul>

    <h3>The same amplifier at a 24 kHz span</h3>
    <ManualChart
      :series="fr24Series"
      :x-min="4"
      :x-max="96000"
      :y-min="-24"
      :y-max="8"
      x-label="Hz"
      y-label="Gain (dB)"
      :height="200"
      caption="Frequency span 24000 (solid) against 96000 (dashed): at 48 kHz the model's two high-frequency sections are both placed at 21.6 kHz, so the resonance is squashed against the top of the band rather than being out of sight above it."
    />
    <p>
      With <strong>Frequency span</strong> at 24000 the analyzer runs at 48 kHz, and the simulator
      does something a real amplifier would not: it refuses to place a filter section above 45 % of
      the sample rate, so the 38 kHz resonance and the 46 kHz roll-off are both moved to
      <strong>21.6 kHz</strong>. The trace then reads {{ db(fr48.magnitudeDb(20_000), 2) }} at 20 kHz,
      peaks {{ db(fr48.magnitudeDb(21_600)) }} at 21.6 kHz and falls off a cliff above it: the ±1 dB
      row ends at {{ formatHz(fr48Bandwidth.oneDbHighHz!) }} and the −3 dB row at
      {{ formatHz(fr48Bandwidth.threeDbHighHz!) }}. That is a property of the model, not of a
      transformer — a real one measured at 48 kHz simply reads flat to the top of the band with the
      resonance out of sight, which is the honest version of the same lesson: a 24 kHz span cannot
      tell you what this amplifier does at 38 kHz.
    </p>

    <h2>Stepped sine response</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>Setting</th>
        </tr>
      </thead>
      <tbody>
        <tr><td>Start / End (Hz)</td><td>20 / 20000</td></tr>
        <tr><td>Steps per octave</td><td>5</td></tr>
        <tr><td>Level mode</td><td>Generator voltage, −20 dBV</td></tr>
        <tr><td>Generator channels</td><td>Left and right</td></tr>
        <tr><td>Right channel is reference</td><td>On</td></tr>
        <tr><td>Averages per point</td><td>1</td></tr>
        <tr><td>Adaptive resolution</td><td>On</td></tr>
        <tr><td>Bandwidth reference</td><td>Passband median, 200 – 2000 Hz</td></tr>
      </tbody>
    </table>
    <p>
      Again the defaults. <strong>RUN SWEEP</strong> takes about a minute for 50 points, and the
      line under the settings says adaptive resolution chose <strong>48 kHz</strong> — a 20 kHz
      end needs nothing higher — which puts the model's clamped 21.6 kHz sections just above the
      sweep, as on the 24 kHz-span chirp above.
    </p>

    <ManualChart
      :series="steppedSeries"
      :x-min="15"
      :x-max="25000"
      :y-min="-6"
      :y-max="4"
      :y2-min="-90"
      :y2-max="90"
      :y2-step="45"
      x-label="Hz"
      y-label="Gain (dB)"
      y2-label="Phase (°)"
      :height="240"
      caption="Expected stepped response at 48 kHz: 50 points, gain reaching +1.8 dB at the 45 Hz lift and +0.95 dB at 20 kHz on the way up to the clamped resonance just above the sweep; phase falling to −39° at the top."
    />
    <p>
      The readings differ from the chirp's, and the difference is the lesson: a stepped sweep can
      only report what lies inside its range. The −3 dB row reads <strong>&lt; 19.0 Hz</strong> and
      <strong>&gt; 20.0 kHz</strong>, both clamped, because the response never falls 3 dB between
      the first point and the last (the points are snapped to FFT bins, so the sweep actually starts
      at 19.0 Hz). The ±1 dB row finds its lower corner at 52.6 Hz — interpolated between the
      45.4 Hz point, which reads +1.8 dB, and the 54.2 Hz point at +0.8 dB — and its upper corner
      clamped at 20 kHz. Widen the range to 5 Hz – 90 kHz to recover the chirp's figures: adaptive
      resolution then picks 192 kHz, the sections return to 38 and 46 kHz, and the points above
      20 kHz climb the real resonance one at a time.
    </p>

    <h2>FFT analysis</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>Setting</th>
        </tr>
      </thead>
      <tbody>
        <tr><td>Stimulus</td><td>Sine tones, one row: 1000 Hz, −6 dBV, Snap to bin on</td></tr>
        <tr><td>Left / Right</td><td>Both</td></tr>
        <tr><td>Sample rate</td><td>192000</td></tr>
        <tr><td>FFT size</td><td>65536</td></tr>
        <tr><td>Window</td><td>Hann</td></tr>
        <tr><td>Averaging</td><td>Linear, 8 spectra</td></tr>
        <tr><td>Simulator</td><td>Noise and Hum on</td></tr>
      </tbody>
    </table>

    <ManualChart
      :series="fftSeries"
      :x-min="5"
      :x-max="96000"
      :y-min="-160"
      :y-max="10"
      x-label="Hz"
      y-label="dBV"
      :height="260"
      caption="Expected spectrum: the tone at −6 dBV with its stack, hum at 50/100/150 Hz, and the noise floor near −144 dBV per bin with a little 1/f rise at the bottom. The real chart draws the floor as a band a few dB tall — the min/max spread of each column."
    />

    <table>
      <thead>
        <tr>
          <th>Tile</th>
          <th>Left reads</th>
        </tr>
      </thead>
      <tbody>
        <tr><td>Level</td><td>−6.00 dBV at 999.0 Hz — the bin Snap to bin moved the tone to</td></tr>
        <tr><td>THD</td><td>0.511 % (−45.8 dB)</td></tr>
        <tr><td>THD+N</td><td>0.511 % — the −150 dBV/√Hz floor integrates to −107 dBV over 20 Hz – 20 kHz, 100 dB under the tone</td></tr>
        <tr><td>SNR</td><td>About 101 dB</td></tr>
        <tr><td>Harmonics table</td><td>H2 −46.0 dBc, H3 −60.0, H4 −72.0, H5 −84.0 … each 12 dB lower, until they meet the floor near H9</td></tr>
      </tbody>
    </table>
    <p>
      At 1 kHz this is <strong>identical to the straight wire</strong>, and that is the point: the
      amplifier is unity there, and its distortion is not its own. Two things show that it is in the
      path at all. The right channel reads the same tone and the same stack, being the generator's
      tap — flip the DUT to the phono stage and the two channels part company at once. And a tone
      moved to the band edge picks up the response: at 20 Hz the fundamental reads −6.15 dBV while
      H2 at 40 Hz is lifted 1.2 dB by the 45 Hz bump, so THD reads about 0.59 %.
    </p>

    <h2>THD vs frequency</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>Setting</th>
        </tr>
      </thead>
      <tbody>
        <tr><td>Start / End (Hz)</td><td>20 / 20000</td></tr>
        <tr><td>Steps per octave</td><td>5</td></tr>
        <tr><td>Level mode</td><td>Generator voltage, −20 dBV</td></tr>
        <tr><td>Left / Right</td><td>Left</td></tr>
        <tr><td>Averages per point</td><td>4</td></tr>
        <tr><td>Adaptive resolution</td><td>On</td></tr>
        <tr><td>Measure noise floor first</td><td>On</td></tr>
        <tr><td>Display</td><td>Units %, THD, D2 and D3 checked</td></tr>
      </tbody>
    </table>

    <ManualChart
      :series="thdSeries"
      :x-min="15"
      :x-max="25000"
      :y-min="0.01"
      :y-max="10"
      y-log
      x-label="Hz"
      y-label="%"
      :height="240"
      caption="Expected THD (black), D2 (blue) and D3 (orange) against frequency: the generator's stack, with the response acting on each harmonic separately from its fundamental."
    />
    <p>
      The line is flat at <strong>0.51 %</strong> through the middle, and the two ends are not
      distortion. At 20 Hz the fundamental sits on the roll-off (−0.2 dB) while its second harmonic
      at 40 Hz sits on the +1.2 dB lift, so D2 <em>relative to the fundamental</em> reads 1.4 dB
      high and THD climbs to about 0.6 %; at 45 Hz it is the fundamental that is lifted and THD dips
      to 0.42 %. At the top, the second harmonic of anything above 15 kHz lands on the
      38 kHz resonance — the sweep runs at 192 kHz and counts harmonics up to 94 kHz — and THD rises
      to about 0.7 % at 20 kHz. D2 and D3 both run to the end of the sweep, since three times
      20 kHz is still inside that limit; a sweep ending higher would see D3 stop where 3·f left the
      band, and the legend says where.
    </p>
    <p>
      A real transformer-coupled amplifier draws a rising THD curve at the bottom for a different
      reason — core saturation — which this model deliberately does not have. The
      <strong>Output transformer (saturating)</strong> model does, and its page draws that curve.
    </p>

    <h2>THD vs amplitude</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>Setting</th>
        </tr>
      </thead>
      <tbody>
        <tr><td>Frequency (Hz)</td><td>1000</td></tr>
        <tr><td>Start / End (V)</td><td>0.001 / 1</td></tr>
        <tr><td>Steps per octave</td><td>5</td></tr>
        <tr><td>Left / Right</td><td>Left</td></tr>
        <tr><td>Averages per point</td><td>4</td></tr>
        <tr><td>Measure noise floor first</td><td>On</td></tr>
      </tbody>
    </table>

    <ManualChart
      :series="thdAmplitudeSeries"
      :x-min="0.0008"
      :x-max="1.25"
      :y-min="0.01"
      :y-max="10"
      y-log
      x-label="V"
      y-label="%"
      :height="220"
      caption="Expected THD against level: a flat line at 0.51 %, D2 at 0.50 % and D3 at 0.10 %, from 1 mV to 1 V."
    />
    <p>
      A flat line, and the reason is the simulator's, not the amplifier's: the harmonic stack is a
      fixed ratio on the generator, so it is the same fraction of a 1 mV tone as of a 1 V one. Every
      linear model draws this. The noise floor trace, if you check it, climbs from about 0.00001 % at 1 V
      to 0.01 % at 1 mV as the tone descends towards a fixed floor — still fifty times under the
      distortion, so nothing here is noise-limited; that trace is what <strong>Measure noise floor
      first</strong> exists to show, and it matters on the nonlinear models. The four nonlinear models each
      draw a different shape here, and are what this screen is for.
    </p>

    <h2>Impulse response</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>Setting</th>
        </tr>
      </thead>
      <tbody>
        <tr><td>Set generator by</td><td>Output voltage, 0 dBV</td></tr>
        <tr><td>Frequency span (Hz)</td><td>96000</td></tr>
        <tr><td>Divide by</td><td>Nothing (the generated sweep)</td></tr>
        <tr><td>Gate: Before (ms) / After (ms)</td><td>1 / 20</td></tr>
        <tr><td>Gate window</td><td>Hann</td></tr>
        <tr><td>Extract harmonic distortion</td><td>On, up to 5</td></tr>
      </tbody>
    </table>

    <ManualChart
      :series="impulseSeries"
      :x-log="false"
      :x-min="-0.05"
      :x-max="0.3"
      :y-min="-0.6"
      :y-max="1.05"
      x-label="ms"
      y-label="relative to peak"
      :height="220"
      caption="Expected impulse response, zoomed to the first 0.3 ms: one positive spike, one undershoot, and the 38 kHz resonance ringing out in about five cycles."
    />
    <p>
      On the screen's default 21 ms window this is a single vertical line at t = 0 — zoom in on
      the first half-millisecond to see the ring. The undershoot and ring are the 46 kHz low-pass
      and the 38 kHz resonance; the 12 Hz roll-off and 45 Hz lift are there too, as a slow negative
      tail a thousandth of the peak's height, invisible at this scale and irrelevant to a 20 ms gate
      — which is why the <strong>Gated response</strong> view repeats the chirp's Bode plot above
      about 50 Hz (a 21 ms gate resolves nothing finer) and nothing below it.
    </p>
    <ul>
      <li>
        <strong>Harmonics</strong> draws the generator's stack: D2 flat at −46 dB and D3 at −60 dB
        against fundamental frequency, each ending where its harmonic leaves the sweep's band. The
        gate has removed their packets from the impulse, which for a linear DUT changes nothing
        visible.
      </li>
      <li>
        <strong>Cumulative spectral decay</strong> and <strong>Burst decay</strong> are nearly
        empty. The only things that ring are the 45 Hz lift (Q 3, losing 27.29/3 ≈ 9 dB per period)
        and the 38 kHz resonance (Q 3.5, about 8 dB per period), and both are gone within two or
        three cycles. Use the <strong>Loudspeaker</strong> for the decay surfaces and the
        <strong>Bandpass</strong> to check them.
      </li>
    </ul>
  </div>
</template>
