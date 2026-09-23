<script setup lang="ts">
import { computed } from 'vue'
import ManualChart, { type ManualMarker, type ManualSeries } from '../../components/ManualChart.vue'
import { gainCrossover, loopGainAt, loopGainCurve, phaseCrossover } from '../../sim/loopGain'
import { formatHz } from '../../../response/format'
import { useManual } from '../../useManual'

const { showPage } = useManual()

// The illustration is a textbook three-pole loop gain (ILLUSTRATION_LOOP: 40 dB at DC; poles at
// 100 Hz, 10 kHz and 40 kHz), not a simulator DUT: these views were invented for a loop gain, and a
// loop needs three poles before it can reach −180° at all. Its margins are computed, not quoted.
const gain = gainCrossover()
const phase = phaseCrossover()

const unitCircle = Array.from({ length: 241 }, (_, i) => {
  const a = (i / 240) * 2 * Math.PI
  return [Math.cos(a), Math.sin(a)] as const
})

const contour = computed<ManualSeries[]>(() => [
  { name: 'Loop gain G(jω)', points: loopGainCurve(1, 1e6, 4000).map((p) => [p.re, p.im] as const), color: 'gain' },
  { name: 'Unit circle', points: unitCircle, color: 'left', dashed: true },
  {
    name: 'Crossings',
    points: [gain, phase].map((c) => {
      const p = loopGainAt(c.freqHz)
      return [p.re, p.im] as const
    }),
    color: 'amber',
    kind: 'points',
  },
  { name: '−1', points: [[-1, 0]], color: 'right', kind: 'points' },
])

const markers: ManualMarker[] = [{ y: 0, color: 'grey', dashed: true }]
</script>

<template>
  <div>
    <h1>The Nyquist view</h1>

    <p>
      One of three projections the
      <button type="button" class="manual-link" @click="showPage('frequency-response')">Frequency response</button>
      and
      <button type="button" class="manual-link" @click="showPage('stepped-response')">Stepped sine response</button>
      screens offer, beside
      <button type="button" class="manual-link" @click="showPage('view-nichols')">Nichols</button>
      and the
      <button type="button" class="manual-link" @click="showPage('view-pole-zero')">pole–zero map</button>.
      All three need a genuine complex response, so all three appear only when
      <strong>Right channel is reference</strong> is ticked — a run that measures its gain against
      the generator and its phase against the right input has two different ratios, not one
      transfer function.
    </p>

    <h2>What it plots</h2>
    <p>
      The measured response <strong>G(jω)</strong> as a single curve in the complex plane: real part
      across, imaginary part up. <strong>Frequency is not an axis here</strong> — it is the
      parameter that walks along the curve, from the lowest measured frequency to the highest. The
      same measurement drawn as a Bode plot has frequency along the bottom and needs two curves;
      here it needs one, and what it shows instead is how close the response comes to a particular
      point.
    </p>
    <p>
      That point is <strong>−1</strong>. An amplifier with feedback runs its loop gain round and
      back into the summing junction, so the loop is unstable where the returned signal is the same
      size as what went in and exactly out of phase, i.e. where G = −1. How far the contour passes
      from that point is the whole content of the picture, which is why the two axes are drawn to
      <strong>true equal aspect</strong>: the distance you see is the distance that matters.
    </p>

    <ManualChart
      :series="contour"
      :markers="markers"
      :x-log="false"
      :x-min="-2"
      :x-max="2"
      :y-min="-1.6"
      :y-max="0.4"
      x-label="Re"
      y-label="Im"
      :height="332"
      :caption="`A stable three-pole loop, drawn near the origin: it leaves DC far off to the right at ×100, crosses the unit circle at ${formatHz(gain.freqHz)} with ${gain.phaseMarginDeg.toFixed(0)}° to spare, passes to the right of −1 with ${phase.gainMarginDb.toFixed(1)} dB of headroom at ${formatHz(phase.freqHz)}, and spirals into the origin. The app draws the critical point as a red ×.`"
    />

    <h2>What is on the plot</h2>
    <p>A legend in the top-right corner names every mark, and lists only what is actually drawn:</p>
    <ul>
      <li><strong>Measured</strong> — the black contour, the run on screen.</li>
      <li><strong>Overlays</strong> — stored results ticked in History, dashed, each in its own pattern. They are drawn without the live run's delay correction, since a stored trace carries neither a latency nor a sample rate.</li>
      <li><strong>Fitted model</strong> — purple and dashed, the rational model from the Poles and zeros panel, and <strong>Show fitted model</strong> in the display rail turns it off. It is the check on that panel — a root list whose model does not track the measurement describes nothing — but a poor fit wanders a long way across the plane, so it is worth hiding again once it has been looked at.</li>
      <li><strong>Unit circle, |G| = 1</strong> — blue and dashed. Wherever the contour crosses it, the loop has unity gain.</li>
      <li><strong>−1 (critical point)</strong> — the red ×.</li>
      <li><strong>Crossings</strong> — two amber rings, at the frequencies the Stability panel quotes: where |G| crosses 1, and where the phase crosses −180°. They mark <em>frequencies on the curve</em>, not poles or zeros; those are a different plane, and a different projection.</li>
    </ul>

    <h2>Reading margins off it</h2>
    <ul>
      <li>
        <strong>Phase margin</strong> is measured at the unit-circle crossing: how many degrees of
        further phase lag the loop could take there before the contour reached −1. On the plot it is
        the angle between the crossing and the negative real axis.
      </li>
      <li>
        <strong>Gain margin</strong> is measured where the contour crosses the negative real axis:
        how many dB the loop gain could rise before that crossing landed on −1. A contour crossing
        at −0.2 has 14 dB in hand; one crossing at −2 has already enclosed the point.
      </li>
      <li>
        Both figures, with the frequency each occurs at, are in the <strong>Stability</strong> panel
        beside the chart. It reports the <em>worst</em> of each when a contour crosses more than
        once, which a real one often does.
      </li>
    </ul>
    <div class="manual-note">
      <p>
        Margins describe a <strong>loop gain</strong>. They mean stability only if the sweep measured
        one — the generator injected in series into the feedback path, with the two inputs either
        side of the injection. An amplifier measured input to output yields its <em>closed-loop</em>
        response, whose contour is perfectly real and whose margins are a number about nothing.
      </p>
    </div>

    <h2>Controls</h2>
    <ul>
      <li>
        <strong>Radius: Linear</strong> is the textbook plot — distances mean what they look like.
        <strong>dB</strong> maps magnitude logarithmically onto the radius, which keeps 60 dB of
        range on screen at the price of distance: the picture is still a valid contour, but how
        close it looks to −1 no longer is, so read the margins from the panel. The
        <strong>Floor (dB)</strong> field is where that map bottoms out; a point below it is drawn
        at the floor radius rather than at the origin.
      </li>
      <li>
        <strong>Invert loop sign (180°)</strong>, in the Stability panel, adds half a turn. Whether
        the summing junction's inversion sits inside the measured path depends on where the
        injection went and which way round the probes did — the analyzer cannot see it, so it is a
        switch rather than a guess. Check it against a case you know is stable.
      </li>
      <li>
        <strong>Restore removed delay</strong> puts back the bulk delay the chirp estimator removed
        per record, so the contour is the physical one. It is a pure rotation of 360°·f·τ — the
        magnitude never changes — and it is offered on the three projections only, on a chirp run
        with the right channel as reference. A stepped run removes nothing and has no checkbox.
        <strong>When to leave it off</strong> is a question of its own; see below.
      </li>
      <li>
        <strong>Show fitted model</strong> draws the Poles and zeros panel's model over the
        measurement. On by default; it costs nothing to leave on while the fit is good and it is
        the quickest way to see that it is not.
      </li>
      <li>
        <strong>Frequency range (Hz)</strong> in the display rail is what the contour is drawn
        from, and it matters more here than on a Bode plot: out-of-band bins where the DUT measured
        nothing are noise in both magnitude and phase, and unfiltered they draw as spaghetti across
        the whole plane and put phantom crossings into the margins. Bins whose coherence is below
        0.5 are dropped for the same reason, and so is anything more than <strong>120 dB below the
        trace's own peak</strong> — that is where the analyzer stops measuring phase at all, so
        what such a column carries is a magnitude and a placeholder, which is not a point in this
        plane. The curve therefore ends where the measurement does rather than at the edge of the
        frequency window, and on a DUT with a deep stopband those are not the same place.
      </li>
    </ul>

    <h2>When to restore the delay, and when not to</h2>
    <p>
      The estimator always removes <em>something</em>, so the useful question is what that something
      was. It finds the impulse's peak and de-rotates by it: on a DUT with a real transport delay —
      a processor, a digital crossover, anything with a buffer — that is the latency, and on a DUT
      with none it is the response's own group delay. The rule that follows sounds backwards and is
      not: <strong>restore it when there was no real delay to remove, and leave it removed when
      there was one.</strong>
    </p>
    <p><strong>Leave it off</strong> — the default — for:</p>
    <ul>
      <li>
        <strong>A DUT with real latency.</strong> The simulator's
        <button type="button" class="manual-link" @click="showPage('sim-dsp-processor')">DSP processor</button>
        is 12 ms, which is 4320° per kilohertz and 240 whole turns by 20 kHz. Restored, this contour
        becomes a 240-turn spiral, the Nichols phase axis runs to −86 400°, and past two dozen turns
        its −180° markers are dropped rather than drawn as a band. Nothing about the DUT is visible
        any more.
      </li>
      <li>
        <strong>Fitting poles and zeros of such a DUT.</strong> A pure delay, <em>e</em><sup>−sT</sup>,
        is not a rational function of s, so the fitter spends roots imitating it and the table fills
        with right-half-plane junk. With the delay removed, what is left <em>is</em> the rational
        part, which is what you want fitted.
      </li>
      <li>
        <strong>Comparing against overlays.</strong> A stored trace carries neither a latency nor a
        sample rate, so overlays are always drawn uncorrected. Restoring only the live curve puts
        the two in different conventions, and curves that should lie on top of each other do not.
      </li>
      <li>
        <strong>Just looking at what the DUT does.</strong> The delay-removed contour is the
        excess-phase picture, and it is the readable one.
      </li>
    </ul>
    <p><strong>Tick it</strong> when the phase itself is the answer:</p>
    <ul>
      <li>
        <strong>Margins on a loop whose delay is genuinely inside it.</strong> Delay eats phase
        margin, so a margin measured with it removed is optimistic. The Stability panel reads the
        corrected curve, so its numbers move with this checkbox.
      </li>
      <li>
        <strong>Fitting an analogue DUT that has no latency.</strong> There the estimator removed
        the DUT's own group delay and left a phase <em>lead</em> behind, which is no more rational
        than a delay — see
        <button type="button" class="manual-link" @click="showPage('view-pole-zero')">the pole–zero map</button>
        for the measured difference on a fifth-order Butterworth.
      </li>
    </ul>

    <h2>Zoom and readout</h2>
    <p>
      Drag a box to zoom both axes; <strong>Reset zoom</strong> in the header returns to the window
      the plot opens on, which is fitted to the data (a 26 dB amplifier sits 20 units out, so a
      fixed ±2 window would show an empty pair of axes). Hovering gives the frequency at the nearest
      point and its magnitude, phase, real and imaginary parts — the way to answer "what frequency
      is <em>that</em> part of the loop".
    </p>
  </div>
</template>
