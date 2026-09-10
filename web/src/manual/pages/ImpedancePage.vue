<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Impedance</h1>

    <p>
      Measures how much a thing resists an alternating current, and how much it shifts the phase
      while doing it — across the whole audio band, in ohms. A loudspeaker's resonance, a
      crossover's behaviour, an output transformer's leakage inductance and an amplifier's input
      impedance are all this measurement.
    </p>

    <h2>The fixture</h2>
    <p>
      Impedance is not something the analyzer can read directly: it measures voltages. So a known
      <strong>reference resistor</strong> is put in series between the generator and the device
      under test, and the two together form a voltage divider. One input channel measures the
      generator side of the resistor, the other measures the junction where the resistor meets the
      DUT. From the ratio of those two voltages the impedance falls out as
      <code>Z = R · H / (1 − H)</code>.
    </p>

    <p>
      Wire it as: <strong>SOURCE</strong> and one input to the top of the resistor, the other input
      to the bottom of it, and the DUT from there to ground. Tell the app which channel is on the
      generator side with <strong>Reference channel</strong>, and enter the resistor's actual
      measured value — the answer is directly proportional to it, so a 5% resistor is a 5% error.
    </p>

    <h3>Choosing the resistor</h3>
    <p>
      Both input channels share one input range on this hardware, so whatever the divider throws
      away is signal-to-noise the DUT channel does not get. A 100 Ω reference measuring an 8 Ω
      loudspeaker puts the measured node 22.6 dB below the drive; that is perfectly workable, but
      27 Ω would be better. The rule of thumb is to pick a reference near the impedance you expect.
      The panel shows what an 8 Ω load would sit at for whatever value you enter.
    </p>
    <p>
      For a valve amplifier's grid input, which is hundreds of kilohms, use a reference in the same
      range — and see <em>Analyzer input impedance</em> below.
    </p>

    <h2>Stimulus</h2>
    <p>
      <strong>Chirp sweep</strong> plays one exponential sweep that excites the whole band at once,
      and a measurement takes seconds. It is the right choice for almost everything.
    </p>
    <p>
      <strong>Stepped sine</strong> plays one tone at a time and measures each on its own. It takes
      minutes instead of seconds, and it buys one thing: level. A chirp spreads its energy over
      thousands of frequency bins, so any single bin sits far below the sweep's own amplitude. A tone
      puts the whole drive on the one frequency being read. That matters wherever the two channels
      get close together and their difference gets small — a high-impedance DUT such as a valve grid
      input, the bottom of the band, and a shorted fixture. It is also a second opinion: chirp and
      stepped sine disagreeing on the same DUT is the tell that something in the calibration chain
      is wrong.
    </p>
    <p>
      A stepped sweep is measured <strong>once</strong>. The RUN button is replaced by a single
      <strong>RUN SWEEP</strong>, because repeating a run that is minutes long until you press Stop
      is not useful.
    </p>
    <p>
      Its settings are a start and end frequency, how many points per octave, and
      <em>adaptive resolution</em>, which sizes each point's record for its own frequency rather than
      for the bottom of the band — that is most of what keeps the top of a sweep quick. The panel
      shows the point count and an estimate of how long the run will take before you start it. Every
      point is a measurement rather than a bin, so there is no smoothing to apply and the chart draws
      the points as they are.
    </p>
    <p>
      Coherence means something here that it does not on a stepped Bode plot: each point takes
      <em>Averages</em> independent records and reports how much of the measured channel the stimulus
      actually explains. With one average there is nothing to compare and it reads 1. With two or
      more, a coherence that sags is the honest signal that a reading is noise — which is exactly
      what a shorted fixture's bottom end looks like when the drive is too low.
    </p>

    <h2>Calibration</h2>
    <p>
      Two standards, measured in this order, and both stored with the measurement so they survive a
      restart.
    </p>
    <p>
      The <strong>calibration stimulus</strong> is chosen separately from the measurement's, and the
      chirp is normally the right answer for it whichever way the DUT is measured. A standard is
      stored as a curve in <em>frequency</em>, and a fixture is a linear passive network, so what it
      presents does not depend on how it was excited — a chirp-captured open corrects a stepped-sine
      measurement unchanged, and does it in seconds rather than minutes. Capture the standards over
      at least the band you intend to measure: outside the band a standard was captured over, its
      curve is held flat rather than measured.
    </p>

    <h3>Open</h3>
    <p>
      With <strong>nothing</strong> on the DUT terminals, both channels see the same voltage, so the
      ratio should be exactly 1. Whatever it is instead is the gain and phase difference between the
      analyzer's two input channels, and it is divided out of every later measurement. This is what
      makes the phase trace believable: a mismatch of 0.3 dB and 4° — well inside what two ADC
      channels can differ by — turns an 8.000 Ω resistor into 8.303 Ω at 4.3°, a reactance that is
      not there at all. It takes one run of a few seconds. Do it.
    </p>

    <h3>Calibration drive level</h3>
    <p>
      Calibration runs drive harder than measurements, and have their own level for that reason. A
      shorted fixture is the hardest thing this measurement ever has to see: the measured channel
      sits about 60 dB below the reference one, and the QA403 has a single input range shared by both
      channels which is set from the loud one. The drive is the only lever left.
    </p>
    <p>
      Measured on a real 100 &Omega; fixture with croc-clip leads: at &minus;20 dBV the short read
      <strong>0.21 &Omega; at &minus;22&deg;</strong> with coherence 0.80, and the phase wandered
      &plusmn;20&deg; across the bottom of the band &mdash; noise sitting on top of a small signal. The
      same leads at <strong>0 dBV</strong> read a flat <strong>0.127 &Omega; at +0.8&deg;</strong> with
      coherence 0.998. Above 5 kHz the two runs agreed to within a percent, because the leads' own
      inductance had already lifted the signal clear of the noise there.
    </p>
    <p>
      Raising it costs nothing in accuracy. The open and short are properties of the <em>fixture</em>,
      which is linear and passive, so the standard it presents does not depend on the level you
      measure it at &mdash; while a driver may well want measuring gently. That is exactly why the two
      levels are separate. If a captured short looks noisy at the bottom of the band, raise this.
    </p>

    <h3>Short</h3>
    <p>
      With the DUT terminals <strong>shorted</strong>, what is left is the resistance and inductance
      of the leads and connectors, in series with everything you go on to measure. Capture it and set
      <strong>Subtract for the leads</strong> to <em>Measured short</em>, or type a known value in
      under <em>Fixed value</em> instead. On a 4 Ω driver a tenth of an ohm of lead is a 2.5% error,
      so it is worth one more run.
    </p>

    <h2>Reading the result</h2>
    <p>
      <strong>|Z| and phase</strong> is the usual view: impedance in ohms on a logarithmic axis
      against frequency, with phase in degrees on the right. A passive one-port stays between −90°
      and +90°; positive is inductive, negative capacitive.
    </p>
    <p>
      <strong>Complex plane</strong> plots the same data as resistance across against reactance up —
      the Nyquist locus. A driver's resonance is a circle there, and the axes are drawn to true equal
      aspect so a circle looks like one. This is the audio-standard view of a complex impedance;
      there is deliberately no Smith chart, which plots reflection coefficient against a
      transmission line that does not exist at these frequencies.
    </p>
    <p>
      The <strong>Equivalent circuit</strong> panel turns one frequency into components: the series
      resistance and reactance, and the inductance or capacitance that reactance corresponds to
      there. "8.5 Ω at 45°" is a measurement; "6 Ω in series with 1 mH" is something you can reason
      about.
    </p>
    <p>
      <strong>Short calibration</strong> in the Display panel draws the stored short standard as the
      impedance it represents, on the same ohms axis as the DUT. That is the quickest way to see
      whether a measurement is trustworthy at a given frequency: the short is the <em>floor</em>
      every reading sits on top of, and a DUT curve that comes down to meet it is a DUT this fixture
      cannot measure there &mdash; which is what choosing a smaller reference resistor fixes. It is
      off unless you turn it on. There is no equivalent for the open standard: an open circuit
      inverts to an enormous impedance, so it can only ever sit far above the top of a useful ohms
      window, and coherence is the better warning at the high-impedance end.
    </p>
    <p>
      With <strong>Phase</strong> ticked as well, the short standard's own phase is drawn on the
      degrees axis beside it, in the same orange and the phase curve's dash. It is worth a look: a
      test lead is a small resistance in series with a little inductance, so its angle is nothing at
      the bottom of the band and climbs steeply once the reactance overtakes the resistance &mdash;
      on the leads this fixture was characterised with (0.117 &Omega; + 0.58 &micro;H) it passes 1.8&deg;
      at 1 kHz, 17&deg; at 10 kHz and 32&deg; at 20 kHz. That is the error sitting underneath every
      reading at the top of the band, and a magnitude trace alone does not show it. Both Autofit
      buttons take these curves into account whenever they are on screen.
    </p>
    <p>
      <strong>Coherence</strong> is the trust metric, as on the
      <a href="#" @click.prevent="showPage('frequency-response')">frequency response</a>. It falls
      wherever the divider output has got too small to measure properly — usually because the
      reference resistor is far from the impedance at that frequency.
    </p>

    <p>
      The row above the chart is its <strong>legend</strong>: every curve currently drawn, with the
      colour, thickness and dash pattern it is drawn in. Hover the plot and the same row gains each
      curve's value at that frequency. The dash pattern is what matters when history overlays are
      showing &mdash; they share the live curve's colour on purpose, so that the measurement you are
      making stays the one you read first, and the pattern is what tells them apart.
    </p>

    <h2>Device under test</h2>
    <p>
      The readout's <strong>Device under test</strong> selector decides which component metrics it
      offers. It defaults to <strong>None</strong>, which shows only what is true of any impedance —
      peak, minimum, median. <strong>Minimum |Z|</strong> is worth a look on its own: it is the
      worst-case load, where the DUT asks the most current of whatever is driving it, and a nominal
      8 Ω driver routinely dips below 6.
    </p>
    <p>
      It asks rather than guesses because nothing in the curve can answer it. A choke's
      self-resonance and a driver's motional resonance are the same shape — a peak in |Z| with the
      phase swinging through zero — and only you know which is on the terminals.
    </p>

    <h3>Loudspeaker</h3>
    <p>
      Reports <strong>fs</strong>, <strong>Zmax</strong>, <strong>Re</strong> (the minimum |Z| above
      resonance, close to but not the same as a DC ohmmeter's answer), the skirt frequencies
      <strong>f₁/f₂</strong> where |Z| falls to √(Re·Zmax), and <strong>Qms</strong>,
      <strong>Qes</strong> and <strong>Qts</strong> from the width between them.
    </p>
    <p>
      Vas, Bl and Mms are deliberately absent: they need a second run with a known added mass or a
      sealed box, so no single sweep can give them. If the sweep does not reach far enough past
      resonance to find a skirt, the Qs are shown as dashes rather than computed from a clamped
      width — a Q from a truncated skirt is a lower bound dressed up as a measurement. And note
      <em>smoothing blunts Zmax and widens the skirts</em>, so measure with none if these numbers
      matter.
    </p>

    <h3>Capacitor</h3>
    <p>
      Reports <strong>C</strong>, fitted across every sample that is clearly capacitive and below
      self-resonance — with the band it used, because a fitted value quoted without one is not a
      measurement. Above self-resonance it also reports <strong>SRF</strong>, <strong>ESR</strong>
      and <strong>ESL</strong>.
    </p>
    <p>
      <strong>ESR is only honest at self-resonance</strong>, which is where it is taken from. Away
      from it, ESR is |Z|·cos φ with φ within a hair of −90°: for a 100 nF film capacitor at 1 kHz,
      |Z| is 1.6 kΩ and the ESR might be 20 mΩ, which is cos φ = 1.2 × 10⁻⁵. A tenth of a degree of
      residual channel phase — about the best a two-channel fixture holds after an open calibration —
      invents 2.8 Ω of ESR there, more than a hundred times the real value. The dissipation factor
      <strong>D</strong> carries a warning marker whenever it falls below what the fixture can
      resolve, and you should believe the warning.
    </p>
    <p>
      What this does measure well: <strong>capacitance itself</strong>, which is the dominant term;
      and ESR when it is <em>already large</em>, which is the useful case — deciding whether an old
      electrolytic has gone high-ESR. Note also that the leads are the floor for small impedances: a
      typical lead set is over 100 mΩ, so measuring a 50 mΩ ESR means the short calibration is doing
      all the work.
    </p>

    <h3>Inductor</h3>
    <p>
      Reports <strong>L</strong> and its band, <strong>DCR</strong> (the real part where |Z| is
      lowest), <strong>SRF</strong>, the impedance at self-resonance — a <em>peak</em> for an
      inductor, where the winding's own capacitance parallels it — and <strong>Q</strong>. DCR is
      marked with a warning when the sweep never gets down to where the resistance dominates, since
      the real part of a large reactance is where any residual phase lands.
    </p>
    <p>
      For an output transformer the two readings worth taking are the primary inductance with the
      secondary open, and the leakage inductance with the secondary shorted — both are just L at a
      chosen band, measured twice.
    </p>
    <p>
      Whatever the part, pick a reference resistor near the impedance you expect. A reactive part's
      |Z| sweeps over decades, so one resistor covers about one or two: measure a band around the
      frequency you care about rather than the whole span at once.
    </p>

    <h2>Fitting the axes</h2>
    <p>
      <strong>Autofit</strong> beside the impedance bounds sets the ohms window from everything the
      chart is currently drawing, rounded outward to a readable 1/2/5 step and taken only over the
      frequencies in view. That includes the <strong>short calibration</strong> trace whenever it is
      ticked, so switching it on and pressing Autofit brings both it and the DUT into one window —
      expect a much taller window than the DUT alone would give, since a short is a fraction of an
      ohm. Untick it and fit again for a window around the DUT by itself. Autofit beside the phase
      bounds does the same for the phase axis, from the measured curve.
    </p>
    <p>
      <strong>Autofit each measurement</strong> runs both automatically at the end of a
      run rather than during it, since refitting mid-average makes the trace crawl.
    </p>

    <h2>Zooming and the frequency axis</h2>
    <p>
      <strong>From</strong> and <strong>To</strong> under Frequency axis in the Display panel set the
      band the chart shows, and the chart follows them immediately — they are the window, not a
      starting suggestion. Drag a box across the plot to zoom both axes instead; while you are zoomed
      a <strong>Reset zoom</strong> button appears in the chart header, and that button, a
      double-click on the plot, or simply changing either dropdown all return you to the window the
      Display panel names.
    </p>

    <h2>Drive level</h2>
    <p>
      The generator level is given directly rather than solved for; there is no "output voltage"
      mode, because a one-port has no output to aim a level at. The panel reports the approximate
      drive current, which is the number that says whether a driver was measured gently enough to
      trust — a voice coil heats up, and a hot one has a higher resistance.
    </p>

    <h2>Against the simulator</h2>
    <p>
      <code>--simulate</code> offers three impedance fixtures, all through a <strong>100 Ω</strong>
      reference: a flat 8 Ω resistor, a 6 Ω + 1 mH series pair, and a loudspeaker with a 45 Hz
      resonance peaking at 60 Ω.
    </p>
    <p>
      <strong>Set the reference channel to Right for these.</strong> The simulator applies its DUT to
      the left channel only, so its divider output is on the left and the untouched generator is on
      the right — the mirror image of a bench fixture wired left-as-reference. That is a property of
      the simulator, not a bug in the measurement, and it applies to both stimuli.
    </p>
    <p>
      The 8 Ω resistor is the one number in this screen that can be checked against arithmetic rather
      than against itself: a flat load has the same impedance at every frequency and no reactance at
      all, so a correct run reads <strong>8.000 Ω at 0.00°</strong> from end to end — through the
      stimulus, the two-channel ratio, the open calibration and the divider inversion. Either
      stimulus should give it.
    </p>

    <h2>Advanced</h2>
    <h3>Analyzer input impedance</h3>
    <p>
      The analyzer's own input sits across the measurement node and takes a share of the current.
      For anything up to a few kilohms this is far below the noise and the field should be left
      blank. When the reference resistor approaches the analyzer's own input impedance — as it must
      when measuring a valve grid input — it stops being negligible: at 100 kΩ against 100 kΩ,
      leaving it uncorrected reads roughly <em>twice</em> the true impedance. Enter the figure from
      the QA403's datasheet and it is taken out of both the measurement and the open calibration.
      There is no default value, deliberately: a guessed one would quietly bias every
      high-impedance measurement instead of leaving the correction off.
    </p>

    <h3>Gate the sweep's harmonic packets</h3>
    <p>
      Deconvolving a chirp recovers the DUT's harmonic distortion alongside its linear response, and
      those packets add a fine ripple to the result. Gating removes them at the cost of halving the
      frequency resolution. A passive load distorts far too little for this to matter; it is here
      for the cases where it does.
    </p>

    <h2>What is not here yet</h2>
    <p>
      This screen measures impedance through a series reference resistor, which covers passive loads
      and an amplifier's <em>input</em> impedance. An amplifier's <em>output</em> impedance needs a
      different method — measuring the same amplifier into two different load resistances and
      solving for the source impedance behind them — because you cannot drive a generator through a
      resistor into a live amplifier output. That method, and the damping factor it yields, are not
      implemented yet. The stimulus selector offers the chirp and the stepped sine; pink noise is
      planned.
    </p>
  </div>
</template>
