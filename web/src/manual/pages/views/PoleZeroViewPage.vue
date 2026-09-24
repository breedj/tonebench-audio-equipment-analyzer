<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { BUTTERWORTH, ELLIPTIC, FILTER_CORNER_HZ } from '../../sim/filters'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// The roots of the simulator's fifth-order filters, from the same prototype table the DUTs are
// built from — i.e. what a good fit of one of them should find, drawn where the map would put it.
function roots(prototype: typeof BUTTERWORTH): (readonly [number, number])[] {
  const out: (readonly [number, number])[] = []
  for (const [sigma, omega] of prototype.polePairs) {
    const re = sigma * FILTER_CORNER_HZ
    const im = omega * FILTER_CORNER_HZ
    out.push([re, im], [re, -im])
  }
  out.push([-prototype.realPole * FILTER_CORNER_HZ, 0])
  return out
}

const cornerCircle = Array.from({ length: 241 }, (_, i) => {
  const a = (i / 240) * 2 * Math.PI
  return [FILTER_CORNER_HZ * Math.cos(a), FILTER_CORNER_HZ * Math.sin(a)] as const
})

const butterworth = computed<ManualSeries[]>(() => [
  { name: `${FILTER_CORNER_HZ / 1000} kHz circle`, points: cornerCircle, color: 'grey', dashed: true },
  { name: 'Poles', points: roots(BUTTERWORTH), color: 'right', kind: 'points' },
])

const elliptic = computed<ManualSeries[]>(() => [
  { name: `${FILTER_CORNER_HZ / 1000} kHz circle`, points: cornerCircle, color: 'grey', dashed: true },
  { name: 'Poles', points: roots(ELLIPTIC), color: 'right', kind: 'points' },
  {
    name: 'Zeros',
    points: ELLIPTIC.zeroOmegas.flatMap((z) => [
      [0, z * FILTER_CORNER_HZ] as const,
      [0, -z * FILTER_CORNER_HZ] as const,
    ]),
    color: 'left',
    kind: 'points',
  },
])

// Equal aspect, the way the app's map is drawn: the chart is 640 wide with 52 + 16 of padding, so
// a 5000 Hz span across needs 572 px of plot and 572 px of plot height — 618 px of chart — for the
// circle to come out a circle. It is the same argument the Nyquist view's aspect lock rests on.
const SQUARE_HEIGHT = 618
const markers: ManualMarker[] = [{ x: 0, color: 'grey' }]
</script>

<template>
  <div>
    <h1>The pole–zero map</h1>

    <p>
      The third projection on the
      <button type="button" class="manual-link" @click="showPage('frequency-response')">Frequency response</button>
      and
      <button type="button" class="manual-link" @click="showPage('stepped-response')">Stepped sine response</button>
      screens, and the one that does <em>not</em> plot the measurement.
      <button type="button" class="manual-link" @click="showPage('view-nyquist')">Nyquist</button>
      and
      <button type="button" class="manual-link" @click="showPage('view-nichols')">Nichols</button>
      both draw G(jω), the response itself, in different coordinates. This one draws where the
      fitted model's <strong>poles and zeros</strong> lie in the s-plane — a different plane, which
      is why they are a projection of their own rather than markers on the contour. A point on a
      Nyquist curve is a complex gain at one frequency; a point here is a frequency and a damping,
      and no gain at all.
    </p>

    <h2>What it plots</h2>
    <p>
      The axes are in <strong>hertz</strong>: σ/2π across and jω/2π up, so a pole at 2 kHz sits
      2 kHz from the origin and the numbers read like the Bode plot's. Poles are red ×, zeros blue
      ○, and conjugate pairs are drawn as the pair they are — one above the axis and one below.
      Both axes share one scale, as on the Nyquist view, because the shape matters: a Butterworth's
      poles lie on a circle, and on unequal axes they would not look like it.
    </p>

    <ManualChart
      :series="butterworth"
      :markers="markers"
      :x-log="false"
      :x-min="-2500"
      :x-max="2500"
      :y-min="-2500"
      :y-max="2500"
      x-label="σ / 2π (Hz)"
      y-label="jω / 2π (Hz)"
      :height="SQUARE_HEIGHT"
      caption="The simulator’s fifth-order Butterworth at 2 kHz, as its designer placed it: five poles evenly spaced on the 2 kHz circle, all in the left half-plane, no zeros at all — Q 0.618 for the outer pair, 1.618 for the inner one, and a real pole at the corner. This is the answer a fit of that DUT is trying to find; what it does find is below."
    />

    <h2>Reading it</h2>
    <ul>
      <li>
        <strong>Left of the vertical axis is stable.</strong> A pole in the right half-plane is a
        growing exponential — an oscillator. The panel flags one as <em>RHP</em>, and on a
        measurement of a device that plainly is not oscillating it means the fit, not the DUT: too
        high an order, too narrow a window, or a phase sign the measurement could not know.
      </li>
      <li>
        <strong>Distance from the origin is the natural frequency</strong>, |s|/2π — what the panel
        lists as <em>f</em>. Distance from the jω axis is the damping: a pole close to the axis
        rings for a long time, one on the real axis does not ring at all.
      </li>
      <li>
        <strong>Q is the angle.</strong> A conjugate pair at ±45° from the real axis has Q = 0.71;
        one near the jω axis has a high Q and a tall, narrow peak in the response. The simulator's
        <button type="button" class="manual-link" @click="showPage('sim-marginally-stable')">Marginally stable</button>
        model is the extreme case, a Q of 500.
      </li>
      <li>
        <strong>Zeros on the jω axis are notches.</strong> An elliptic filter's stopband nulls are
        exactly that, and nothing else in this app's DUTs has them.
      </li>
      <li>
        <strong>The amber segment on the jω axis is the measured band</strong> — the stretch of
        frequency the sweep actually covered, and so the only stretch the fit can speak for.
      </li>
    </ul>

    <ManualChart
      :series="elliptic"
      :markers="markers"
      :x-log="false"
      :x-min="-2500"
      :x-max="7000"
      :y-min="-4500"
      :y-max="4500"
      x-label="σ / 2π (Hz)"
      y-label="jω / 2π (Hz)"
      :height="360"
      caption="The elliptic filter of the same order and corner, which is the one simulator DUT with zeros: the poles have moved close to the jω axis (the passband ripple) and two zero pairs sit on the axis itself at 3.48 and 5.31 kHz, which is where its stopband nulls are. Drawn on a wider window than the Butterworth above, so the axes here are not square."
    />

    <h2>Where the roots come from</h2>
    <p>
      They are <strong>fitted</strong>, not measured. The <strong>Poles and zeros</strong> panel
      fits a rational s-domain model to exactly the curve on screen — windowed, sign-corrected,
      delay-corrected — and the map draws that model's roots, so the table and the picture can
      never describe different things. The panel's residual is what makes either of them
      falsifiable: it says plainly when the model does not follow the measurement, and the fitted
      model's dashed purple contour on the Nyquist and Nichols views is the same check by eye.
    </p>
    <SettingsTable
      :headings="['Control', 'What it does']"
      :rows="[
        ['Choose order automatically', 'Searches orders and takes the lowest that fits about as well as the best one tried, with as many zeros as poles. It stops climbing once the model is already inside the measurement’s own ripple (0.1 dB rms), so a flat amplifier is described by a low order rather than by roots invented to chase noise. Untick it to set the two separately.'],
        ['Poles', 'The denominator order: how many poles the model may have, 1 to 10.'],
        ['Zeros', 'The numerator order, never more than Poles — a transfer function is proper. Set it to 0 for an all-pole DUT (any of the four classic filters, most amplifiers): with as many zeros as poles the fitter spends them on noise, and invents right-half-plane poles to go with them.'],
        ['Remove out of band', 'A display option on the map: leaves out the roots the fit placed outside the swept band and rescales the plot to what is left. The fit is unchanged and the table goes on listing them, flagged.'],
      ]"
    />

    <h2>On a chirp run, restore the delay first</h2>
    <p>
      The chirp estimator removes a bulk delay from every record, and on a filter that delay is the
      filter's <em>own</em>: the Butterworth's impulse peaks 256 µs in, so that is what gets
      removed. What is left is the response with a phase <em>lead</em> on it, which is not a
      rational function of s at all — and the fitter, told to use no zeros, distorts the poles
      instead to imitate it.
    </p>
    <p>
      Measured on the simulator's Butterworth over 100 Hz – 2 kHz at Poles 5, Zeros 0, with the
      delay left removed and then restored:
    </p>
    <SettingsTable
      :headings="['Restore removed delay', 'What the fit reports']"
      :rows="[
        ['Off', '1.98 kHz Q 0.61 in the right half-plane, 2.16 kHz Q 1.33, a real pole at 3.91 kHz — 0.053 dB rms'],
        ['On', '1.85 kHz Q 0.589, 1.98 kHz Q 1.652, a real pole at 2.60 kHz, all in the left half-plane — 0.006 dB rms'],
      ]"
    />
    <p>
      The second row is the filter: Q 0.618 and 1.618 are what a fifth-order Butterworth has, and
      the right-half-plane pole in the first row is an artefact of the missing delay rather than
      anything the DUT does. So on a chirp run, <strong>tick Restore removed delay before reading
      the map</strong>. A stepped run removes nothing and needs no such care. Even at its best the
      fit is an <em>equivalent</em> model over the fitted band — the real pole lands at 2.6 kHz
      rather than 2.0 — which is what 0.006 dB of residual buys and what a root list is worth.
    </p>

    <h2>Right-half-plane roots: delay, or too high an order</h2>
    <p>
      Both produce them, and the removed delay is only one of the two. On a chirp of a filter the
      delay is large — the Butterworth's 14 samples above — and restoring it moves the roots a long
      way. On a measurement whose removed delay is a fraction of a sample the checkbox changes
      nothing, and a right-half-plane pair then means the order is too high: measured on a push-pull
      EL84 amplifier (chirp 13 Hz–91 kHz, 0.213 samples removed, about a microsecond), a hand-set
      order of 6 produced two right-half-plane pairs at 74.9 and 84.7 kHz whose Q changed by 0.011
      with the delay restored. The tell is the residual: that fit was already flat to 0.05 dB at
      order 1, so the extra roots bought nothing.
    </p>

    <div class="manual-note">
      <p>
        <strong>The DC gain is a reading only when every root is above the band.</strong> Then the
        response is flat from the lowest root down to DC and the figure is the passband gain. A root
        <em>below</em> the band means the model bends on the way down through a corner the sweep
        never reached, and the panel marks the figure <strong>≈ … · extrapolated</strong> and names
        the root. On the EL84 above, an order-6 fit put a pole at 7.65 Hz against a zero at 3.00 Hz
        — the amplifier's coupling, half seen — and quoted a DC gain of 18.43 dB against a measured
        passband gain of 26.58 dB, next to a fit error of 0.008 dB rms. An excellent fit and a
        confidently wrong headline number are not in conflict; only one of them is about the
        measurement.
      </p>
    </div>

    <div class="manual-note">
      <p>
        <strong>A root outside the measured band is an extrapolation.</strong> The sweep constrains
        the response between its endpoints and says nothing beyond them, so a pole placed out there
        is whatever the fit needed to bend the in-band curve — often roughly right, sometimes a
        decade out, and never a measurement. Widen the sweep rather than trusting one. Because the
        window is fitted to the roots, a single wild one also squeezes every real root into a pixel
        at the origin, which is what <strong>Remove out of band</strong> is for.
      </p>
    </div>

    <h2>Hover and zoom</h2>
    <p>
      Hovering a root gives its kind, natural frequency, Q, damping ratio ζ and both coordinates,
      plus a note when it is in the right half-plane. Dragging a box zooms, and
      <strong>Reset zoom</strong> returns to the window the map opens on — fitted to the roots that
      are drawn, which is why hiding the out-of-band ones changes the scale.
      <strong>Invert loop sign</strong> and <strong>Restore removed delay</strong> change the curve
      the fit is taken over, so they move the roots too — see above for which way to set the second
      one.
    </p>
  </div>
</template>
