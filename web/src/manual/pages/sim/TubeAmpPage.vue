<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import { bandwidth, impulseCurve, logSpace, magnitudeCurve, phaseCurve } from '../../sim/curves'
import { tubeAmplifier } from '../../sim/models'
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

// The FFT and THD sections below have no chart: a linear model with the simulator's Harmonics box
// off (the default) has nothing to draw there but the tone itself, and the figures quoted for the
// box ticked are the generator's stack bent by the response above, not a property of the amplifier.

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
      It is a <em>linear</em> model — it has a frequency response and nothing else, so its THD
      reads 0.000 % at every level and every frequency. (Tick the simulator's
      <strong>Harmonics</strong> box and it reads the generator's own stack, H2 −46 dBc and H3
      −60 dBc, passed through that response; the figures for that case are given below, marked as
      such.) The interesting part of it sits
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
          <td>THD, any level</td>
          <td>0.000 %. With Harmonics ticked, 0.51 % at 1 kHz, about 0.6 % at 20 Hz and 0.7 % at 20 kHz — see below.</td>
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
      <li><a href="#" @click.prevent="showPage('thd-vs-frequency')">THD vs frequency</a> — zero; with Harmonics ticked, flat with the response acting on the harmonics at each end.</li>
      <li><a href="#" @click.prevent="showPage('thd-vs-amplitude')">THD vs amplitude</a> — zero at every level, and why that is the wrong model for the screen.</li>
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
        <tr><td>Simulator</td><td>Noise, Hum and Harmonics off (the default)</td></tr>
      </tbody>
    </table>

    <table>
      <thead>
        <tr>
          <th>Tile</th>
          <th>Left reads</th>
        </tr>
      </thead>
      <tbody>
        <tr><td>Level</td><td>−6.00 dBV at 999.0 Hz — the bin Snap to bin moved the tone to</td></tr>
        <tr><td>THD</td><td>0.000 %</td></tr>
        <tr><td>THD+N</td><td>0.000 % — there is no floor to integrate; tick Noise and the −150 dBV/√Hz floor integrates to −107 dBV over 20 Hz – 20 kHz, 100 dB under the tone</td></tr>
        <tr><td>SNR</td><td>Meaningless without a floor; about 101 dB with Noise ticked</td></tr>
        <tr><td>Harmonics table</td><td>Nothing above the arithmetic's own floor; with Harmonics ticked, H2 −46.0 dBc, H3 −60.0, H4 −72.0 … each 12 dB lower</td></tr>
      </tbody>
    </table>
    <p>
      At 1 kHz this is <strong>identical to the straight wire</strong>, and that is the point: the
      amplifier is unity there and adds nothing. Two things show that it is in the path at all. A
      tone moved to the band edge picks up the response: at 20 Hz the fundamental reads
      −6.15 dBV on the left while the right channel, the generator's tap, still reads −6.00. And
      with <strong>Harmonics</strong> ticked the two channels' harmonics tables part company at the
      band edges even though their levels agree: at 20 Hz the left's H2 at 40 Hz is lifted 1.2 dB
      by the 45 Hz bump, so its THD reads about 0.59 % against the right's 0.51 %.
    </p>

    <h2>The THD sweeps</h2>
    <p>
      Both read <strong>0.000 %</strong> at every point: the model is linear, and there is nothing
      for a THD sweep to draw. That is not a shortcoming of the sweep — it is what a distortionless
      amplifier looks like on it — but it does make this the wrong model for learning either screen.
      The four nonlinear models each draw a different shape there, and are what those screens are for.
    </p>
    <p>
      Tick <strong>Harmonics</strong> and the sweeps show the generator's stack read through the
      response, which is instructive in its own right because none of what moves is distortion in
      the amplifier. Against frequency (20 Hz – 20 kHz at −20 dBV, five steps per octave) the line
      is flat at 0.51 % through the middle and bends at the ends: at 20 Hz the fundamental sits on
      the roll-off (−0.2 dB) while its second harmonic at 40 Hz sits on the +1.2 dB lift, so D2
      <em>relative to the fundamental</em> reads 1.4 dB high and THD climbs to about 0.6 %; at 45 Hz
      it is the fundamental that is lifted and THD dips to 0.42 %; at the top, the second harmonic
      of anything above 15 kHz lands on the 38 kHz resonance — the sweep runs at 192 kHz and counts
      harmonics up to 94 kHz — and THD rises to about 0.7 % at 20 kHz. Against amplitude (1 kHz,
      1 mV – 1 V) it is a flat line at 0.51 %, D2 at 0.50 % and D3 at 0.10 %, because the stack is a
      fixed ratio on the generator and so the same fraction of a 1 mV tone as of a 1 V one. The
      noise floor trace, with <strong>Measure noise floor first</strong> on and Noise ticked, climbs
      from about 0.00001 % at 1 V to 0.01 % at 1 mV as the tone descends towards a fixed floor —
      still fifty times under the stack, so nothing there is noise-limited.
    </p>
    <p>
      A real transformer-coupled amplifier draws a rising THD curve at the bottom for a different
      reason — core saturation — which this model deliberately does not have. The
      <strong>Output transformer (saturating)</strong> model does, and its page draws that curve.
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
        <strong>Harmonics</strong> draws nothing but the arithmetic's floor: a linear DUT has no
        harmonic packets in its impulse, so the gate has nothing to remove. With the simulator's
        Harmonics box ticked it draws the generator's stack instead — D2 flat at −46 dB and D3 at
        −60 dB against fundamental frequency, each ending where its harmonic leaves the sweep's
        band — and the gate removes their packets, which for a linear DUT changes nothing visible.
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
