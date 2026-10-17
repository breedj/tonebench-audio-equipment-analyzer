<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>THD vs frequency</h1>

    <p>
      Steps a single tone across the band and measures the harmonics at every step, plotting
      distortion against frequency. It is the standard picture of where an amplifier misbehaves:
      output-transformer trouble at the bottom, the flat middle where the feedback loop still has
      gain to spare, and the rise at the top where it runs out.
    </p>

    <p class="manual-wiring-links">
      <strong>How to wire it:</strong>
      <button type="button" class="manual-link" @click="showPage('wiring-line-level')">
        a line-level DUT
      </button>
      ·
      <button type="button" class="manual-link" @click="showPage('wiring-power-amp')">
        a power amplifier
      </button>
    </p>

    <h2>What it measures</h2>
    <p>
      At each frequency the generator plays one tone, the analyzer waits for it to settle (the
      audio still in flight to the DAC, plus the <strong>Capture delay</strong> in
      <button type="button" class="manual-link" @click="showPage('settings')">Configuration</button>),
      and one capture is analysed for the fundamental and its harmonics. Each point stores the total
      harmonic distortion, the individual harmonics D2 through D5, everything from the sixth upward
      as D6+, the measured fundamental level, and — if you asked for it — the residual measured with
      the generator off.
    </p>
    <p>
      What each harmonic is <em>divided by</em> is a choice (<strong>THD method</strong>, below).
      The conventional figure divides every harmonic by the fundamental at the stimulus frequency.
      The frequency-normalized figure divides harmonic <em>n</em> by the fundamental the same sweep
      measured at <em>n</em> times the stimulus frequency — where that harmonic actually sits — so
      the DUT's own frequency response drops out of the reading and what is left is the
      nonlinearity. A normalized run stores both sets of figures per point.
    </p>
    <p>
      All of it is stored per point, so the chart can be re-read in percent or dB, and the FFT and
      waveform behind any single point can be inspected afterwards without re-running the sweep.
    </p>

    <h2>How the figures are calculated</h2>
    <p>
      The same arithmetic produces the THD tiles on the
      <button type="button" class="manual-link" @click="showPage('fft-spectrum')">FFT analysis</button>
      screen and every point of the
      <button type="button" class="manual-link" @click="showPage('thd-vs-amplitude')">amplitude sweep</button>;
      only the axis differs.
    </p>

    <h3>Measuring one tone</h3>
    <p>
      Each point is one averaged record, Hann-windowed. The analyzer locates the fundamental as the
      largest bin above the <strong>Fundamental min search</strong> frequency — 200 Hz on the FFT
      screen, so mains hum is never mistaken for the tone, which a sweep lowers to half its own
      start frequency, or a run from 20 Hz would fail to find its own tone for the whole bottom
      decade — then looks for each harmonic at an integer multiple of that bin. The fundamental and
      every harmonic are measured by exactly the same three steps:
    </p>
    <ol>
      <li>locate the peak within <strong>±2 bins</strong> of the expected bin, since clock drift moves a tone by a bin or two;</li>
      <li>sum the power spectral density over <strong>±3 bins</strong> around it — the tone's skirt;</li>
      <li>subtract the local noise floor from that sum.</li>
    </ol>
    <p>
      Four details there decide whether the number means anything, and none of them is arbitrary.
    </p>
    <ul>
      <li>
        The skirt is summed in the <strong>power (PSD) domain</strong>, not the amplitude domain. A
        coherent tone under a Hann window spreads real energy into its neighbouring bins, and only
        the PSD sum — which is the <em>S2</em> normalisation, the Parseval-safe one — adds that back
        to the true amplitude. An amplitude-domain (<em>S1</em>) sum double-counts the spread and
        reads high.
      </li>
      <li>
        The floor subtracted is the <strong>median</strong> of 24 bins either side of the tone,
        skipping a 4-bin guard band so the window's own mainlobe and the tone's phase-noise skirt
        are not counted as noise. Median rather than mean, so an adjacent spur or the next harmonic
        cannot inflate it — then scaled by <strong>1 ÷ ln 2</strong>, because PSD noise bins are
        exponentially distributed, for which median = ln 2 × mean, and it is the mean that has to
        come off a sum of bins. Omitting that scaling under-subtracts by 1.6 dB on every harmonic.
        Omitting the subtraction entirely is far worse: ±3 bins across eight harmonics integrates 56
        bins of noise floor, and on the bench a signal with no harmonic content at all reported
        THD −106 dB.
      </li>
      <li>
        The located peak is only believed over the expected bin if it stands <strong>6 dB</strong>
        above that floor. Taking the largest of five bins of pure noise is a biased estimator — for
        exponentially distributed noise power the maximum of five bins averages 2.28× the mean, i.e.
        +3.6 dB of selection bias — so unchecked peak-picking invents a harmonic wherever there is
        none.
      </li>
      <li>
        A harmonic whose power comes out at zero or below after the subtraction is reported at
        <strong>a tenth of the floor amplitude</strong>, 20 dB under it. Something finite is needed
        for dBV and dBc to stay renderable, and a tenth contributes 1 % of a single noise bin's
        power to the THD sum — nothing — while putting the reading unmistakably in “below the floor”
        territory.
      </li>
    </ul>
    <p>
      How many harmonics are counted is the <strong>Harmonic count</strong> in the FFT screen's
      analysis settings, twelve by default. A sweep counts them up to <strong>Nyquist</strong>
      rather than to the 20 kHz measurement band the FFT screen uses: bounded at 20 kHz, every
      harmonic of a point above about 7 kHz would be out of band and THD would read 0 across the
      whole top of the sweep.
    </p>

    <h3>From harmonics to THD</h3>
    <p>
      Every figure on the chart is a voltage ratio against the fundamental of the same capture,
      where <em>V1</em> is the fundamental and <em>Vn</em> the <em>n</em>th harmonic:
    </p>
    <table>
      <tbody>
        <tr>
          <td>THD</td>
          <td><code>√(V2² + V3² + … + VN²) ÷ V1</code> — every counted harmonic, power-summed.</td>
        </tr>
        <tr>
          <td>D2 … D5</td>
          <td><code>Vn ÷ V1</code> — one harmonic each, so they can be read individually.</td>
        </tr>
        <tr>
          <td>D6+</td>
          <td>
            <code>√(V6² + V7² + …) ÷ V1</code> — everything from the sixth up, rolled into one trace
            so the chart does not carry eleven near-identical lines down in the grass.
          </td>
        </tr>
        <tr>
          <td>THD+N</td>
          <td>
            Distortion <em>and</em> noise together, over the measurement <strong>Bandwidth</strong>
            set in the Sweep panel: everything in that band except the fundamental itself, over the
            fundamental. It sits above THD by however much noise the DUT has, and the two converge
            as the distortion rises &mdash; which is the whole reading. It is the figure a
            specification usually means by "THD".
            <strong>It steps down near the top of the sweep, and that is the band rather than the
            DUT</strong> &mdash; see <strong>Bandwidth</strong> below.
          </td>
        </tr>
        <tr>
          <td>SNR</td>
          <td>
            The fundamental over the noise in that same band, with the distortion taken out.
            <strong>It is drawn as the noise it measures, not as the ratio</strong>: this axis
            counts downward from the fundamental, so a 95 dB SNR is plotted at &minus;95 dB
            (0.0018 %), where it can be compared with THD, THD+N and the noise floor. Drawing it
            upward would need a second axis for one trace. The metrics table in a report and the
            <code>snr_db</code> column of an exported CSV print it the usual way up, counting
            upward in dB.
          </td>
        </tr>
      </tbody>
    </table>
    <p>
      Power-summed rather than “the largest of them”, because that is what makes the buckets add
      back up to the total: <code>THD² = D2² + D3² + D4² + D5² + D6+²</code>.
    </p>

    <h3>Percent and dB</h3>
    <p>
      <strong>Percent</strong> is that ratio × 100 — the distortion as a fraction of what the DUT
      put out. <strong>dB</strong> is the same ratio in decibels, <code>20·log₁₀(ratio)</code>, i.e.
      <code>20·log₁₀(percent ÷ 100)</code>, <em>plus the DUT's gain at that point</em> — see
      <strong>Units</strong> under Display panel below for why the gain is added and what it makes
      the axis mean. The ratio alone runs:
    </p>
    <table>
      <tbody>
        <tr><td>1 %</td><td>−40 dB</td></tr>
        <tr><td>0.1 %</td><td>−60 dB</td></tr>
        <tr><td>0.01 %</td><td>−80 dB</td></tr>
        <tr><td>0.001 %</td><td>−100 dB</td></tr>
      </tbody>
    </table>
    <p>
      Through an amplifier with 26 dB of gain those same four readings are drawn at −14, −34, −54
      and −74 dB. So a figure read off the dB axis is converted back with
      <code>percent = 100 × 10^((dB − Magnitude) ÷ 20)</code>, Magnitude being that point's own
      value on the Magn trace.
    </p>

    <h3>Frequency-normalized</h3>
    <p>
      Under <strong>Frequency normalized</strong> each harmonic is divided by the fundamental the
      same sweep measured <em>at that harmonic's own frequency</em> instead of at the stimulus
      frequency:
    </p>
    <p><code>nTHD(f) = √( Σ A<sub>n</sub>(f)² ÷ A₁(n·f)² )</code></p>
    <p>
      so the DUT's linear response — a roll-off, a resonance, a reflection — drops out and what is
      left is the nonlinearity. A conventional THD divides everything by A₁(f), which on a device
      with a 6 dB/octave slope reads the second harmonic 6 dB wrong and the third 9.5 dB wrong for
      reasons that have nothing to do with distortion. The reference is read off the sweep's own
      fundamental trace, interpolated linearly in dB over log frequency — the straight line a
      first-order roll-off draws on a Bode plot, so a 6 dB/octave slope reads exactly right between
      grid points and a resonance is at worst cut across by a chord. That is why the sweep carries
      on past the frequency you asked for, to five times it or 98 % of Nyquist, whichever comes
      first: without those extra points the top of the band has nothing to divide by. An order whose
      <em>n</em>·<em>f</em> still falls outside the measured span is left out of the sum and drawn as
      a gap rather than guessed at, and the total is withheld entirely when even the second harmonic
      has no reference — a “THD” made of whichever harmonics happened to be available is not a
      figure. Both sets are stored, so a normalized run can still be read conventionally.
    </p>

    <h3>The noise floor trace</h3>
    <p>
      With <strong>Measure noise floor first</strong> ticked, one capture is taken with the
      generator off before the sweep begins, and the trace is the <em>mean per-bin amplitude</em>
      across the measurement band, expressed as a percentage of each point's own fundamental. It is
      a per-bin figure, which is why that capture always uses the largest FFT the sweep's sample
      rate allows whatever size the points themselves use: a per-bin level falls as the bin narrows,
      so under adaptive resolution a floor taken at each point's own size would climb 16.8 dB from
      20 Hz to 20 kHz in 3.4 dB steps with the real noise perfectly constant — and on a DUT whose
      distortion sits near the residual, that false slope crosses the distortion traces part-way up
      the sweep.
    </p>

    <h3>Also measured, not plotted</h3>
    <p>
      Every point additionally stores <strong>THD+N</strong> — everything in the measurement band
      except the fundamental's notch, over V1, where the notch is half an octave either side
      (0.71·f to 1.41·f, always clear of the second harmonic at every FFT size) — and
      <strong>SNR</strong>, the same integral with each harmonic's own ±3 bins taken out as well, so
      distortion is not counted as noise. Neither is drawn on this chart or written to the CSV; they
      are in the stored result.
    </p>

    <h2>Running it</h2>
    <p>
      <strong>Run sweep</strong> walks the points once and stops on its own.
      <strong>Cancel</strong> stops it early and keeps whatever was measured — a cancelled sweep can
      still be saved. <strong>Clear</strong> discards the points on screen, and is deliberately
      disabled while a sweep runs so a two-minute measurement cannot be thrown away by a stray
      click.
    </p>

    <h2>Sweep settings</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Start / End (Hz)</td>
          <td>
            The frequency range, stepped upward from Start. Anything above 98% of Nyquist is
            dropped, and the panel warns when that will truncate the sweep. Start will not go below
            <strong>7.3 Hz</strong>: the harmonic analysis sums ±3 bins around each harmonic, so a
            fundamental fewer than five bins up overlaps its own second harmonic and THD reads near
            100%. Five bins of the longest record this app takes — 131 072 points at 192 kHz, or
            32 768 at 48 kHz, both 0.68 s and both 1.465 Hz per bin — is 7.32 Hz, and no sample rate
            makes it lower.
          </td>
        </tr>
        <tr>
          <td>Steps per octave</td>
          <td>
            Point density, logarithmically spaced. Five is a good survey; more resolves a narrow
            feature and costs proportionally more time. The estimate underneath updates as you type.
          </td>
        </tr>
        <tr>
          <td>Level mode</td>
          <td>
            <strong>Generator voltage</strong> holds the drive constant — simplest, and what you
            want when characterising the analyzer itself or a DUT of known gain.
            <strong>DUT output voltage</strong> and <strong>DUT output power</strong> are
            output-referred: the analyzer probes at 1 kHz first, works out the gain, and sets the
            generator so the DUT delivers what you asked for. That is what makes "THD at 1 W into
            8 Ω" expressible at all. The probe starts at &minus;40 dBV and corrects upward from
            there, so a DUT of unknown gain is never hit with the full output while its gain is
            being found.
          </td>
        </tr>
        <tr>
          <td>Level / Power and Unit</td>
          <td>
            The target itself, in dBV, V or mV — or in watts or milliwatts in output-power mode.
            Each level mode keeps its own figure <em>and</em> its own unit, so switching mode
            brings back what that mode was last set to rather than reading the previous
            mode's number in the new unit.
          </td>
        </tr>
        <tr>
          <td>Load (Ω)</td>
          <td>
            The amplifier's load. Turns watts into a target voltage and reports power per point.
            Shared with every other screen's power figures.
          </td>
        </tr>
        <tr>
          <td>Left / Right</td>
          <td>Which inputs are analysed at each point.</td>
        </tr>
        <tr>
          <td>Averages per point</td>
          <td>
            How many records are averaged before a point is recorded. More averages lower each
            reading's own noise floor, at a proportional cost in time.
          </td>
        </tr>
        <tr>
          <td>THD method</td>
          <td>
            <strong>Conventional</strong> is the textbook figure: harmonic over fundamental, both at
            the stimulus frequency. It folds the DUT's frequency response into the distortion — a
            6 dB/octave roll-off reads the second harmonic 6 dB wrong and the third 9.5 dB wrong for
            reasons that have nothing to do with distortion, and a resonance shows up as a bump in
            every distortion trace. <strong>Frequency normalized</strong> (Temme, Listen Inc.)
            divides harmonic <em>n</em> by the fundamental this sweep measured at <em>n</em>·f
            instead, which cancels that response and leaves the nonlinearity. To have those
            fundamentals, the sweep keeps stepping past End — to five times it, or to just under
            Nyquist, whichever comes first; the line under the settings says how far. <strong>Those
            are real tones at the sweep level</strong>, above the band you asked about: a 20 kHz
            sweep at 192 kHz drives the DUT to 94 kHz, which matters in the output-power modes into
            a tweeter or a transformer-coupled stage. The extra points are drawn like any other,
            with the Magnitude trace showing the response up there; their own distortion figures
            stay blank, since nothing above them was measured to reference against.
          </td>
        </tr>
        <tr>
          <td>Adaptive resolution</td>
          <td>
            Sizes the FFT per point instead of holding one size for the whole sweep, and picks the
            sample rate from the top of the range. A fixed size has to satisfy the lowest point and
            is then paid at every point above it, where the record is far longer than the
            measurement needs — so this mostly speeds up the top of a sweep. Turn it off for a fixed
            192 kHz / 131072-point reference: slower, but identical resolution and noise floor at
            every frequency. The line underneath always states what will actually be used.
          </td>
        </tr>
        <tr>
          <td>Measure noise floor first</td>
          <td>
            Captures one generator-off record before the sweep, so the chart can draw the residual
            the distortion traces sit above. Without it, a trace that dives into the floor looks
            like a genuinely low-distortion region.
          </td>
        </tr>
        <tr>
          <td>Bandwidth</td>
          <td>
            The band this sweep's noise figures are read over: the <strong>noise floor</strong>,
            <strong>THD+N</strong> and <strong>SNR</strong> traces, and nothing else.
            <strong>20 Hz &ndash; 20 kHz</strong>, <strong>20 Hz &ndash; 80 kHz</strong>, or
            <strong>Custom</strong>, which shows two boxes for the edges. It does <em>not</em> bound
            the harmonics, unlike the same control on the FFT screen: a sweep measures those up to
            Nyquist, because bounded at 20 kHz every harmonic of a point above about 7 kHz would be
            out of band and THD would read exactly zero from there up. So THD, D2&ndash;D5 and D6+
            do not move when you widen it, and the three band figures do.
            <strong>Which is why THD+N falls off a cliff near the top of a sweep on the 20 kHz
            band</strong>: a harmonic stops counting toward it the moment it leaves the band, so
            the second harmonic is gone above 10 kHz and the third above 6.7 kHz, and from there
            THD+N is running on noise alone &mdash; below THD, which is impossible for a figure
            that contains it, and the clearest sign that the two are being read over different
            spans. Measured on a QA403 into a push-pull amplifier at 1 W, a sweep whose distortion
            is mostly third harmonic: THD 0.046&nbsp;% and THD+N 0.047&nbsp;% together up to
            5.95&nbsp;kHz, then THD+N straight down to 0.020&nbsp;% at 6.70&nbsp;kHz and to
            0.015&nbsp;% at 10.9&nbsp;kHz, which is exactly that point's noise floor. The same
            sweep on <strong>20 Hz &ndash; 80 kHz</strong> has no step at all and THD+N stays above
            THD the whole way (0.057&nbsp;% to 0.081&nbsp;%), at the price of 7&nbsp;dB of SNR,
            because the band now integrates four times the noise. Widen it when you care about
            THD+N above a few kHz; leave it at 20&nbsp;kHz when you want the figure a
            specification quotes. The lower edge is also held clear of
            DC: a window spreads the input's DC offset over its own main lobe, so a band cannot
            start closer than a few bins from 0 Hz. That costs nothing at a long record &mdash; 20 Hz
            is bin 14 of a 131072-point FFT &mdash; but an adaptive sweep's short records at the top
            have 23 or 47 Hz bins, where the band really does start at about 120 or 235 Hz whatever
            you ask for. It is this sweep's own setting, saved and restored with the measurement; before
            2026-10-14 a sweep used whatever band the FFT screen had been left on, which no stored
            result could say afterwards.
          </td>
        </tr>
      </tbody>
    </table>

    <h2>What the views show</h2>

    <h3>The chart</h3>
    <p>
      Distortion against frequency, both axes logarithmic in percent mode. Each checked trace is
      drawn per channel — blue left, red right — and each overlaid history run gets its own dash
      pattern. The legend under the chart draws the actual line style of every entry, so a curve can
      always be traced back to a run. A frequency-normalized run's traces are labelled
      <strong>nTHD</strong>, <strong>nD2</strong> … so that, overlaid on a conventional run, the two
      read as the different measurements they are; Magnitude and Noise are the same number under
      either method and keep their names.
    </p>
    <p>
      During a normalized sweep the normalized traces fill in about an octave <em>behind</em> the
      point being measured: a point's nD2 needs the fundamental at twice its frequency, which the
      sweep has not reached yet. That is not a stall — the conventional figures for the same point
      are already there, and the point is updated in place as its references arrive.
    </p>

    <h3>The readings table</h3>
    <p>
      Under the chart, the full set of readings at the frequency nearest the cursor: one block per
      run, one row per channel, columns aligned across every block so a live sweep and a stored one
      can be read against each other.
    </p>
    <p>
      <strong>Generator</strong> is the level the point was actually played at, in volts and in dBV.
      Under <strong>Generator voltage</strong> level mode that is simply the Level field; under the
      two output-referred modes the analyzer solved for it from the 1 kHz probe, and this column and
      the <code>gen</code> figure on the status line above the chart are the only places it is
      stated. It is on every point, so a stored result read back months later still says how hard the
      DUT was driven. The column is omitted on the amplitude screen's <strong>Input voltage</strong>
      axis, where the sweep axis already is the generator level.
    </p>

    <h3>The point previews</h3>
    <p>
      In the left rail, the spectrum and the waveform of the point currently selected — the point
      being measured while the sweep runs, or the one nearest the cursor once it has finished. This
      is what makes a suspicious point answerable: a spike in the trace is either real distortion, a
      clipped waveform, or hum that wandered into the harmonic being counted, and the preview says
      which. Each checked history run gets its own preview pair below.
    </p>
    <p>
      <strong>Click the chart to pin a point.</strong> A dashed amber line marks it, a
      <strong>Pinned</strong> badge appears beside the chart's title, and from then on the readings
      table and both previews stay on that point however the pointer moves — which is what lets you
      take the pointer off the chart to expand a preview and study it, instead of watching it jump
      back to the last point measured. Click another point to move the pin, and click the pinned
      line again, or the badge, to release it and go back to following the cursor. The pin is
      dropped when the sweep is cleared, when you leave the measurement, and on the amplitude
      screen when the x axis is changed; it is deliberately kept across a re-run, so the same point
      can be watched before and after a change to the DUT.
    </p>
    <p>
      <strong>Fit all</strong>, in the spectrum preview's header, fixes its level axis instead of
      scaling it to the point on screen: the window then covers every point of every sweep the
      chart is drawing — the live run's and each checked history run's — with a grid line every
      20 dB and both ends on a line. That is what makes two points comparable, since an axis that
      re-scales as the cursor moves draws a harmonic that grew by 10 dB exactly like one that did
      not. The button is shared by all the spectrum previews on screen, so a run and its
      comparisons are read against one scale. Off, each preview scales to its own point.
    </p>
    <p>
      The oscilloscope preview has its own <strong>Fit all</strong>, doing the same to its volts
      axis: one window, symmetric about zero, covering every waveform the chart's points hold, with
      a grid at a round number of volts. Fixed, a point measured at a tenth of the drive is drawn a
      tenth the height instead of filling the chart like every other point. The two toggles are
      separate — a spectrum and a waveform are read for different things — and both are saved with
      the measurement, so a screen set up for comparing comes back that way. Both start
      <em>on</em> for a newly created measurement: reading one point against the next, or a run
      against a history overlay, is what the previews are for, and an axis that rescales under each
      of them makes every one of those comparisons wrong-footed.
    </p>

    <h2>Display panel</h2>
    <table>
      <tbody>
        <tr>
          <td>Units</td>
          <td>
            <strong>%</strong> or <strong>dB</strong>, and the two answer different questions.
            Percent is the distortion as a fraction of the DUT's <em>output</em>, on a log axis
            spanning several decades. dB is linear and <em>gain-compensated</em>: each figure is
            converted to dB below the fundamental and then the DUT's gain at that point is added
            back, so every trace — including Magnitude — is referred to one volt of generator
            drive. A harmonic at −51.08 dBV under a fundamental of +9 dBV, from a DUT with 14.53 dB
            of gain there, is 60.08 dB down and is drawn at <strong>−45.55 dB</strong>, which is
            that same harmonic read against the −5.53 dBV going in. The axis is labelled
            <strong>dB V/V</strong> for that reason, and it is what makes the traces readable
            against each other: the harmonics of a flat DUT sit a fixed distance under its
            Magnitude curve, and a bump in the response lifts the whole family together instead of
            dividing itself out. ARTA's distortion-vs-frequency plot and the previous QA40x
            application both do this; their percent plots, like this one, do not.
          </td>
        </tr>
        <tr>
          <td>Graph data</td>
          <td>
            Which traces are drawn: THD, D2, D3, D4, D5, D6+, the noise floor,
            <strong>THD+N</strong> and <strong>SNR</strong>, and — on the dB axis only —
            <strong>Magnitude</strong>, the measured fundamental relative to the generator setpoint.
            That last one is the DUT's frequency response riding along with the sweep, free. All
            start off except THD, D2 and D3: a chart that came up with nine lines would be
            unreadable.
          </td>
        </tr>
        <tr>
          <td>Style</td>
          <td>Thick lines and data points.</td>
        </tr>
      </tbody>
    </table>
    <p>
      Above Display sit two sections of their own, the way the FFT screen has them:
      <strong>Distortion range (%)</strong> — <strong>dB range</strong> when the units are dB — is the
      vertical window and <strong>Frequency range (Hz)</strong> the horizontal one. One
      <strong>Autofit</strong>, under the distortion range, fits both to every checked trace: the
      sweep axis to the measured span, the distortion axis to the values inside it. <strong>Autofit when measurement done</strong> is off unless you tick it, so a window you set
      stays where you put it.
    </p>

    <h2>Zooming</h2>
    <p>
      Drag a box anywhere on the plot to zoom both axes; double-click, or press
      <strong>Reset zoom</strong>, to return to the window the display panel names.
    </p>

    <h2>Reading it</h2>
    <ul>
      <li>
        <strong>Which harmonic dominates says what kind of distortion it is.</strong> D2 rising with
        level is ordinary asymmetry; D3 dominating suggests symmetric compression or the onset of
        clipping; a lot of D6+ is crossover distortion or something genuinely unpleasant.
      </li>
      <li>
        <strong>Watch the noise floor trace.</strong> Where a distortion trace meets it, you are
        measuring the analyzer and not the DUT. More averages per point, or more drive, buys
        headroom there.
      </li>
      <li>
        <strong>The top of the sweep runs out of harmonics.</strong> Above about a fifth of the
        Nyquist frequency, the higher harmonics have nowhere to land, so D5 and D6+ fall away for
        arithmetic reasons rather than because the DUT improved. Raise the sample rate — or turn off
        adaptive resolution — if the top octave matters.
      </li>
      <li>
        <strong>Conventional and normalized disagree exactly where the response is not flat.</strong>
        Through a flat DUT the two methods read the same number, which is the check that nothing
        else moved. Where they differ, the difference is the DUT's gain between f and n·f — a
        roll-off, a resonance, a room reflection — and only the normalized figure is distortion. The
        simulator's <button type="button" class="manual-link" @click="showPage('sim-phono-riaa')">phono stage</button>
        is the clearest demonstration: its 40 dB tilt pulls conventional D2 2.6 dB low at 1 kHz,
        and the normalized reading is the generator stack's true −46 dBc, flat across the band.
      </li>
      <li>
        <strong>A normalized figure is blank where its reference is missing.</strong> That is every
        harmonic whose n·f lies above the last frequency the sweep measured — so the reference
        points past End are the ones without figures of their own, and a sweep that stopped at
        Nyquist loses the upper orders first, as above.
      </li>
    </ul>

    <div class="manual-note">
      <p>
        In the two output-referred level modes the generator is raised until the DUT reaches your
        target. Set the target for the DUT you actually have connected, and check the
        <em>gen</em> figure in the progress line once the probe has settled.
      </p>
      <p>
        The status line passes through <strong>Determining input range</strong> after it: the range
        the sweep runs on is <em>measured</em>, from the least sensitive setting, where the probe
        cannot clip and so cannot read a peak that is really the range's own clipping level. If that
        measurement asks for a wider range than your target implied, the level search is repeated on
        it, since its first captures were clipped — so a run takes one or two captures longer than
        the point count suggests. Both probes need to program a range of their own, which is why a
        sweep will not start at all while <strong>Ranging</strong> is set to Manual in the status
        bar &mdash; set it back to Auto and the sweep does the ranging for you.
      </p>
    </div>

    <p>
      For the level axis instead of the frequency axis, see
      <button type="button" class="manual-link" @click="showPage('thd-vs-amplitude')">
        THD vs amplitude
      </button>.
    </p>
  </div>
</template>
