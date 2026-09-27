<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import SettingsTable from '../../components/SettingsTable.vue'
import { BUTTERWORTH, FILTER_CORNER_HZ } from '../../sim/filters'
import { useManual } from '../../useManual'
import { evaluateField, marchingSquares } from '../../../response/surfaceField'
import type { RationalModel } from '../../../response/rationalFit'

const { showPage } = useManual()

/**
 * The simulator's fifth-order Butterworth as a rational model, built from the same prototype table
 * the DUT itself is built from — so the picture below is the filter, not an impression of one.
 *
 * The prototype is normalised to 1 rad/s, so with `omegaScale = 2π·FILTER_CORNER_HZ` the
 * coefficients are exactly the normalised polynomial's: a pole pair (σ, ω) contributes
 * s² − 2σs + (σ² + ω²), and the real pole contributes s + p.
 */
const model = computed<RationalModel>(() => {
  // Multiply the second-order sections out, lowest power first.
  let den = [1]
  const multiply = (by: number[]) => {
    const out = new Array(den.length + by.length - 1).fill(0)
    for (let i = 0; i < den.length; i++) for (let j = 0; j < by.length; j++) out[i + j] += den[i]! * by[j]!
    den = out
  }
  for (const [sigma, omega] of BUTTERWORTH.polePairs) multiply([sigma * sigma + omega * omega, -2 * sigma, 1])
  multiply([BUTTERWORTH.realPole, 1])
  // Unity at DC, which is how the sections are realised in the DUT.
  const scale = den[0]!
  return { num: [1], den: den.map((c) => c / scale), omegaScale: 2 * Math.PI * FILTER_CORNER_HZ }
})

/** The plane the figure covers, in hertz: a little over twice the corner, so the roots sit well inside. */
const EXTENT_HZ = 5000
const GRID = 132

/**
 * Iso-dB contours of |H(s)|, computed here exactly as the chart computes them — `evaluateField` and
 * `marchingSquares` are the app's own, so the figure cannot drift from what the screen draws.
 */
const contours = computed<ManualSeries[]>(() => {
  const viewport = { xMin: -EXTENT_HZ, xMax: EXTENT_HZ, yMin: -EXTENT_HZ, yMax: EXTENT_HZ }
  const field = evaluateField(model.value, { scale: 'Linear', refHz: 1 }, viewport, GRID, GRID)

  const toHz = (col: number, row: number) =>
    [
      viewport.xMin + ((col + 0.5) / GRID) * (viewport.xMax - viewport.xMin),
      viewport.yMax - ((row + 0.5) / GRID) * (viewport.yMax - viewport.yMin),
    ] as const

  const series: ManualSeries[] = []
  for (const [levelDb, color] of [
    [12, 'right'],
    [0, 'left'],
    [-12, 'grey'],
    [-24, 'grey'],
  ] as const) {
    const flat = marchingSquares(field, levelDb, 1)
    const points: (readonly [number, number])[] = []
    for (let i = 0; i < flat.length; i += 4) {
      points.push(toHz(flat[i]!, flat[i + 1]!), toHz(flat[i + 2]!, flat[i + 3]!))
    }
    if (points.length > 0) series.push({ name: `${levelDb > 0 ? '+' : ''}${levelDb} dB`, points, color, kind: 'segments' })
  }

  series.push({
    name: 'Poles',
    points: [
      ...BUTTERWORTH.polePairs.flatMap(([sigma, omega]) => [
        [sigma * FILTER_CORNER_HZ, omega * FILTER_CORNER_HZ] as const,
        [sigma * FILTER_CORNER_HZ, -omega * FILTER_CORNER_HZ] as const,
      ]),
      [-BUTTERWORTH.realPole * FILTER_CORNER_HZ, 0] as const,
    ],
    color: 'phase',
    kind: 'points',
  })
  return series
})

// Equal aspect, the same argument the pole–zero page's SQUARE_HEIGHT rests on: 640 wide less
// 52 + 16 of padding leaves 572 px of plot, so a square span needs 572 px of plot height.
const SQUARE_HEIGHT = 618
const markers: ManualMarker[] = [{ x: 0, color: 'grey' }]
</script>

<template>
  <h1>The |H(s)| surface</h1>

  <p>
    Every other view draws what was <em>measured</em>. This one cannot: a measurement gives the
    response only on the jω axis — one tone at a time, or one chirp bin at a time — and there is no
    such thing as a measured value anywhere else in the plane. What the surface draws is the
    <strong>fitted model</strong> from the <button class="manual-link" @click="showPage('view-pole-zero')">Poles
    and zeros</button> panel, evaluated over the whole plane instead of along one line of it.
  </p>

  <p>
    That is not a weakness of the picture, it is what the picture is for. A pole is a place where the
    model's denominator vanishes and |H| runs to infinity; a zero is where the numerator vanishes and
    it falls to nothing. On the jω axis you see the frequency response as a ridge running up the
    vertical; step off the axis and the reason for its shape appears as the hills it is draped over.
  </p>

  <h2>The check that keeps it honest</h2>

  <p>
    The jω axis <strong>is</strong> the Bode magnitude. Read a level off the colour key anywhere on
    the vertical axis and it is the gain the Bode plot shows at that frequency — the one line of the
    plane where the model and the measurement are the same thing. If those two disagree, the surface
    is wrong, and that is the first thing to check.
  </p>

  <p>
    Below, the simulator's fifth-order Butterworth low pass, contoured from its own prototype: five
    poles on a circle of radius 2 kHz in the left half-plane, which is what a Butterworth alignment
    <em>is</em>. The passband fills the region near the origin, and the 0 dB contour bulges up the jω
    axis to the corner and then turns away — that turn is the roll-off.
  </p>

  <ManualChart
    :series="contours"
    :height="SQUARE_HEIGHT"
    :markers="markers"
    :x-min="-EXTENT_HZ"
    :x-max="EXTENT_HZ"
    :y-min="-EXTENT_HZ"
    :y-max="EXTENT_HZ"
    x-label="σ / 2π (Hz)"
    y-label="jω / 2π (Hz)"
    caption="Contours of |H(s)| for the 2 kHz Butterworth, at +12, 0, −12 and −24 dB. The dots are its five poles."
  />

  <h2>Reading it</h2>

  <SettingsTable
    :headings="['What you see', 'What it means']"
    :rows="[
      ['A bright core with contours crowding into it', 'A pole. The contours tighten because the level is climbing fast; at the pole itself it is unbounded, so the colour saturates.'],
      ['A dark funnel', 'A zero. The model goes to nothing there.'],
      ['A core to the right of the vertical axis', 'A right-half-plane root. See the warning below — it is usually the fit, not the DUT.'],
      ['The heavy amber segment on the vertical axis', 'The measured band: the only part of the plane the sweep spoke for. The two faint arcs mark its ends.'],
      ['Everything else', 'Extrapolation. The further from that segment, the more of it is the model talking.'],
    ]"
  />

  <h2>Map and relief</h2>

  <p>
    <strong>Map</strong> is the plane seen from above, as colour with iso-dB contour lines.
    <strong>Relief</strong> is the same field as an oblique surface, where a pole is a spike — the
    textbook picture, and the one that reads immediately. They share one evaluated field, one colour
    window and one colour key, so they cannot disagree about a level; the map is the one to take a
    number off, and the relief is the one to look at.
  </p>

  <h2>Linear and log radius</h2>

  <p>
    <strong>s-plane radius</strong> applies to this view and to the pole–zero map alike, so a root
    sits in the same place on both. <strong>Linear</strong> is the plane as a textbook draws it.
    <strong>Log</strong> scales the distance from the origin logarithmically and leaves the angle
    exactly alone — so damping still reads as an angle, but every decade of roots is visible at once.
  </p>

  <p>
    That matters more than it sounds. An amplifier with a 12 Hz coupling pole and a 46 kHz
    transformer pole has to show both, and on a linear plane fitted to the higher one the lower sits
    at 0.03 % of the way out — inside the origin marker. On a log radius it lands at about a tenth of
    the way out, which is a place you can see. Linear is the default, because switching the scale
    must change where a root is drawn and nothing whatever about what it is.
  </p>

  <h2>The surface is only as good as the fit</h2>

  <p>
    This is the one warning worth repeating, because a surface is a far more persuasive picture than
    a table of numbers. The chart carries the fitted order and the residual on its top line, and it
    turns amber when the model is more than half a decibel from the measurement, when every root is
    an extrapolation, or when any root has landed in the right half-plane.
  </p>

  <p>
    On a chirp run, <strong>tick Restore removed delay before reading any of this</strong>. The chirp
    estimator removes a bulk delay from every record, and on a filter that delay is the filter's own,
    so leaving it out hands the fit a phase lead that is not a rational function of s — and the fit
    spends poles imitating it, usually by putting a pair in the right half-plane. Measured on the
    Butterworth above: delay left out, Q 0.616 and 1.057 with a right-half-plane pair, at 1.94 dB
    rms; delay restored, Q 0.616 and 1.621 against the alignment's own 0.618 and 1.618, every root in
    the left half-plane, at 0.013 dB rms.
  </p>

  <h2>The formula</h2>

  <p>
    The <strong>Transfer function</strong> panel writes the same model out as H(s), as a product of
    first- and second-order sections whose numbers you can check one by one against the pole–zero
    table. Each section is 1 at s = 0, so the leading number is the DC gain — signed, so an inverting
    stage shows its minus. A model with a zero at the origin has no DC gain to normalise on, and the
    panel switches to a root form and says why.
  </p>

  <p>
    Two conventions are printed under the formula because both are places a reader assumes the other
    thing: frequencies are in <strong>hertz</strong> while the coefficient arrays are in
    <strong>rad/s</strong>, and a second-order section's middle coefficient is <strong>2ζ</strong> —
    not ζ, and not 1/Q. The copy button offers plain text, LaTeX, SciPy and MATLAB; the coefficient
    arrays are highest power first, which is what both of those tools expect, and they round-trip
    exactly rather than being rounded for display.
  </p>

  <p>
    See also the <button class="manual-link" @click="showPage('view-pole-zero')">pole–zero map</button>,
    which draws the same roots as points in the same plane, and the
    <button class="manual-link" @click="showPage('view-nyquist')">Nyquist</button> and
    <button class="manual-link" @click="showPage('view-nichols')">Nichols</button> views, which draw
    the measured response itself rather than a model of it.
  </p>
</template>
