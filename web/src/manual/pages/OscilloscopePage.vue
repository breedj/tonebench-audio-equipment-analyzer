<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Oscilloscope</h1>

    <p>
      The waveform itself, against time: a triggered capture of up to one second, drawn as it was
      sampled. Everything else in this app answers a question about a signal — how much distortion,
      how flat, how quiet. This one shows you the signal, which is what you want when the question
      is <em>what is it doing</em>: clipping on one half, ringing after an edge, a burst of hum once
      a second, a transient that no average survives.
    </p>

    <p class="manual-wiring-links">
      <strong>How to wire it:</strong>
      <button type="button" class="manual-link" @click="showPage('wiring-oscilloscope')">
        the wiring diagram
      </button>
    </p>

    <h2>There is no trigger socket, and that is an advantage</h2>
    <p>
      A bench scope waits for an event and then starts recording. This analyzer streams continuously
      and never stops, so a capture is a <em>window chosen out of the stream</em> — the samples
      before the trigger were already in hand when it fired. Pre-trigger therefore costs nothing:
      <strong>100 %</strong> is a legal setting, and a record that is entirely history is as cheap
      as one that is entirely future. It also means the trigger is software, so it can be moved,
      re-levelled and re-armed on a capture already running without restarting anything.
    </p>

    <h2>Acquisition</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Sample rate</td>
          <td>
            The capture rate, and with it the finest detail there is: one sample every 5.2 µs at
            192 kS/s, 20.8 µs at 48 kS/s. It cannot be changed on a capture already running — it
            re-clocks the analyzer — so changing it restarts the run for you.
          </td>
        </tr>
        <tr>
          <td>Record (ms)</td>
          <td>
            The whole record, pre- and post-trigger together, up to one second. Unlike a bench
            scope's timebase this is not what you see: the chart draws a window <em>into</em> the
            record, so a long record costs memory rather than detail. Longer than a second is the
            stream recorder's job (Configuration ▸ Diagnostics).
          </td>
        </tr>
        <tr>
          <td>Pre-trigger (%)</td>
          <td>How much of the record sits before the trigger, 0 to 100. The badges underneath quote the record in samples, the sample interval and what one capture costs end to end.</td>
        </tr>
        <tr>
          <td>Channels</td>
          <td>
            Which inputs are captured — a scope's CH1 and CH2 buttons. An input that is not captured
            is not drawn, is not measured and is not saved; whether a <em>captured</em> channel is
            drawn is the display rail's own pair.
          </td>
        </tr>
      </tbody>
    </table>

    <h2>Trigger</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Mode</td>
          <td>
            <strong>Auto</strong> draws a record anyway if nothing crosses the level within the
            timeout, and marks it <em>Untriggered</em> — what stops a mis-set level looking like a
            dead input. <strong>Normal</strong> waits as long as it takes and shows nothing until
            something qualifies. <strong>Single</strong> catches one record and holds it.
          </td>
        </tr>
        <tr>
          <td>Source</td>
          <td>Which channel the trigger watches. It must be one of the captured channels.</td>
        </tr>
        <tr>
          <td>Slope</td>
          <td>Rising, falling or either.</td>
        </tr>
        <tr>
          <td>Level (V)</td>
          <td>
            The threshold. Normally set by <strong>dragging the dashed line</strong> on the chart;
            the box is for a figure you already know. <strong>0 V</strong> puts it on ground, which
            needs nothing to have been captured, and <strong>50 %</strong> puts it half way between
            the extremes of the record on screen — the trigger source's own extremes, not both
            channels', since a level between two traces is a level on neither.
          </td>
        </tr>
        <tr>
          <td>Hysteresis (V)</td>
          <td>
            The noise-reject band, drawn as a faint band around the line. The trigger has to leave
            the band on the far side before it will fire again, so raise it when a noisy signal
            fires several times per edge and the trace will not stand still.
          </td>
        </tr>
        <tr>
          <td>Holdoff (ms)</td>
          <td>How long after a capture the trigger refuses to fire again — for a waveform with several qualifying edges per cycle.</td>
        </tr>
        <tr>
          <td>Auto timeout (ms)</td>
          <td>How long <strong>Auto</strong> waits before taking a record anyway. Shown only in Auto.</td>
        </tr>
      </tbody>
    </table>

    <p>
      <strong>Every control takes effect where it is turned.</strong> A scope is set up while it is
      running, so nothing here needs a stop and a start: the settings are handed to the capture in
      flight, within one record. The only exception is the sample rate, which re-clocks the
      analyzer and therefore restarts the run.
    </p>

    <h2>The generator</h2>
    <p>
      This screen has its own, at the top of the settings rail, and it is staged and saved with the
      measurement — independently of the FFT screen's. Press <strong>Run</strong> or
      <strong>Single</strong> and the stimulus goes out with the capture; change it while the scope
      runs and the change reaches the device without interrupting anything. <strong>Stop</strong>
      silences it. Every stimulus the app has is available here: tones, a multitone comb, noise, the
      chirp, and the exact square and triangle described on the
      <button type="button" class="manual-link" @click="showPage('fft-spectrum')">FFT analysis</button>
      page.
    </p>

    <h2>The chart</h2>
    <p>
      Time runs across, <strong>zero is the trigger</strong>, and anything before it is negative —
      the same convention the decay surfaces use. The dashed vertical line marks t = 0 and the
      dashed horizontal one is the trigger level, with its grab handle at the right-hand edge; when
      the level is off the top or bottom of the window the handle becomes an arrow pointing the way
      it went, rather than disappearing.
    </p>
    <p>
      <strong>The zoom bar</strong> across the top of the chart is the whole record, drawn small,
      with a box showing which part of it the chart below is looking at. Drag the box to move the
      window, drag either edge to resize it, or click anywhere on the bar to jump there keeping the
      same span. The dashed amber line on it is the trigger, so the bar says where you are in the
      record even when the chart is showing a tenth of a millisecond of it. The figure beside it is
      the <strong>record's</strong> own timebase, against the chart's own below — two timebases, the
      way a bench scope with a zoom window shows them.
    </p>
    <p>
      The box and the chart are <strong>one window</strong>: dragging the box, dragging the plot and
      pressing Back all move the same thing, so the bar always shows where the chart really is.
    </p>
    <p>
      <strong>Drag to zoom, double-click to reset.</strong> A zoom is not only a magnification: the
      window is sent to the analyzer, which cuts the record to it and — once the window holds no
      more samples than the chart has columns to draw them in — sends <strong>every sample</strong>
      rather than a min/max summary. The header says which you are looking at, and
      <em>every sample drawn</em> is what makes an edge or a rise time believable. Zoomed out, each
      column carries both extremes of what it spans, so nothing narrower than a column can hide.
      <strong>Back</strong> steps out one zoom at a time, <strong>Set axis</strong> writes the
      window into the display rail's boxes, and <strong>Reset zoom</strong> goes all the way out.
    </p>

    <h3>Display</h3>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>V/div and Centre</td>
          <td>
            A scope's vertical pair rather than a top and a bottom: eight divisions of a round
            figure, centred where you put it. A graticule is read by counting squares, which only
            works if a square is a round number. The arrows on <strong>Centre</strong> move it one
            division at a time. The ladder runs from 10 µV/div to 50 V/div — the bottom is under
            the analyzer's own noise floor, which is the point of having it.
          </td>
        </tr>
        <tr>
          <td>Time window (From / To)</td>
          <td>Both ends relative to the trigger, negative to its left, in the unit the record is read in.</td>
        </tr>
        <tr>
          <td>Channels</td>
          <td>Which captured inputs are drawn. A channel that is not being captured is listed but disabled, and says where to turn it on.</td>
        </tr>
        <tr>
          <td>Trigger level</td>
          <td>Whether the line and its hysteresis band are drawn over the trace.</td>
        </tr>
        <tr>
          <td>Signal path</td>
          <td>
            The coupling and an in-path filter — see <strong>The signal path</strong> below. Both
            run on the analyzer before anything is drawn or measured.
          </td>
        </tr>
        <tr>
          <td>View</td>
          <td>
            Which picture the chart draws — <strong>Y–T</strong>, the waveforms against time, or
            <strong>X–Y</strong>, one input against the other. See <strong>The X–Y view</strong>
            below.
          </td>
        </tr>
        <tr>
          <td>Math</td>
          <td>
            A waveform derived from the two captured ones — see <strong>The math channel</strong>
            below. Drawn in violet against its own axis on the right.
          </td>
        </tr>
        <tr>
          <td>Persistence</td>
          <td>
            How many records the chart keeps — see <strong>Persistence</strong> below. Off draws
            each record over the last; anything else keeps a history behind it.
          </td>
        </tr>
        <tr>
          <td>Residual</td>
          <td>
            Draws each channel again with its fitted fundamental taken out — see
            <strong>The residual</strong> below.
          </td>
        </tr>
        <tr>
          <td>Cursors</td>
          <td>
            Two pairs of measuring lines — see <strong>Cursors</strong> below. Switching a pair on
            places it a quarter and three quarters across the window, so there is always something
            to grab.
          </td>
        </tr>
        <tr>
          <td>Thick lines, Show data points</td>
          <td>Line weight, and a marker per sample — drawn only where the window is tight enough for the chart to hold real samples, because a marker on a min/max column would mark the column.</td>
        </tr>
        <tr>
          <td>Autofit / Autofit when measurement done</td>
          <td>Fits both axes to what is on screen: the time window to the whole record, the V/div and centre to the visible traces. The checkbox does it as a run starts, which also ends any zoom.</td>
        </tr>
      </tbody>
    </table>

    <h2>The signal path</h2>
    <p>
      Two controls that change the <em>trace</em> rather than adding to it: a coupling and one
      filter. Both run on the analyzer, over the whole record, before anything is drawn, measured,
      combined or plotted — so the picture and every figure beside it are of the same waveform.
    </p>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Coupling</td>
          <td>
            <strong>DC</strong> shows what arrived. <strong>AC</strong> subtracts the record’s own
            mean — see the note below, which is why it is a mean and not a filter.
          </td>
        </tr>
        <tr>
          <td>Filter</td>
          <td>
            Off, or a <strong>low-pass</strong> (a bandwidth limit — a bench scope’s “20 MHz”
            button, and real noise reduction on a quiet DUT), a <strong>high-pass</strong> (for a
            drift or a rumble inside the record), a <strong>band-pass</strong> (to look at one
            component of a busy trace) or a <strong>notch</strong> (what mains hum is taken out
            with).
          </td>
        </tr>
        <tr>
          <td>Corner / Centre</td>
          <td>Where it acts, in hertz.</td>
        </tr>
        <tr>
          <td>Order</td>
          <td>
            How steep a low- or high-pass is: a Butterworth of 2, 4, 6 or 8 poles — and then
            <em>doubled</em>, because the filter runs twice. Order 2 is 24 dB an octave on the
            trace, not 12.
          </td>
        </tr>
        <tr>
          <td>Q</td>
          <td>How narrow a band-pass or a notch is. A notch at Q 8 is about an eighth of its centre wide.</td>
        </tr>
      </tbody>
    </table>
    <p>
      The small chart under the controls is the filter’s own magnitude response, drawn from the
      analyzer’s own figures rather than from a second copy of the filter here — so it is the
      response the trace really gets, with the corner marked. It is the <em>squared</em> response,
      which is why the corner reads −6 dB and not −3.
    </p>
    <div class="manual-note">
      <p>
        <strong>The filter adds no delay, and that is why the corner is −6 dB.</strong> This
        screen’s x axis is time relative to the trigger, so an ordinary filter would slide the
        waveform off the very edge it fired on and a rise time measured from there would be of an
        edge that is no longer where it happened. Running the filter forward and then backward over
        the record cancels the delay exactly — and applies the magnitude twice, which doubles both
        the corner attenuation and the skirt.
      </p>
    </div>
    <div class="manual-note">
      <p>
        <strong>AC coupling here is the mean, not a high-pass, and that is the more correct
        operation on this hardware.</strong> Both the QA403 and a sound card already block DC in
        hardware, with a corner near 0.2 Hz — so what reaches a capture is the front end’s own
        standing offset, measured at about −4.5 mV on the left input. That is a constant, and
        subtracting the mean removes a constant <em>exactly</em>: no corner to choose, no settling,
        and nothing to ring at the start of the record where the pre-trigger view is read. For a
        genuine low-frequency roll-off — a drift or a rumble <em>inside</em> the record — use the
        high-pass.
      </p>
    </div>
    <div class="manual-note">
      <p>
        <strong>The first and last few cycles of the corner are the filter’s own assumption.</strong>
        A record is a window of a stream that has since moved on, so nothing in it says what came
        next, and a filter has to assume something about it. What is drawn at the very ends is that
        assumption rather than a measurement — bounded, and never more than about a fifth of full
        scale, but visible on a short record. The middle of the record is exact. If an edge matters,
        widen the record so the part you are reading is not at its end.
      </p>
    </div>
    <div class="manual-note">
      <p>
        <strong>Every reading is of the filtered trace, and what is stored never is.</strong> A
        low-pass lowers a peak to peak and lengthens a rise time; a notch removes a component the
        RMS was counting. None of that is wrong — the figures are of what is on screen, which is the
        only honest arrangement — and the Measurements panel says which trace that is. The samples
        that go into a saved result are always the unfiltered capture, so a result can be reopened
        and read through a different filter, or none.
      </p>
    </div>

    <h2>The X–Y view</h2>
    <p>
      <strong>View ▸ X–Y</strong> throws the time axis away and plots the left input against the
      right: a goniometer, a vectorscope, a Lissajous figure — the same picture under three names.
      Two channels become a <em>shape</em> rather than two traces, and the shape is read at a
      glance where the same information on a time axis is "two traces that look similar".
    </p>
    <table>
      <thead>
        <tr>
          <th>What you see</th>
          <th>What it means</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>A straight line up to the right, at 45°</td>
          <td>A matched pair, in phase. The dashed diagonals are drawn for exactly this comparison.</td>
        </tr>
        <tr>
          <td>A straight line at some other angle</td>
          <td>In phase, but one channel is louder — the <strong>Right / left</strong> row says by how much.</td>
        </tr>
        <tr>
          <td>A straight line up to the <em>left</em></td>
          <td>Out of phase: one lead is inverted, or the pair is wired the wrong way round. Correlation reads −1.</td>
        </tr>
        <tr>
          <td>A circle</td>
          <td>A quarter period apart — 90°, which is what a single reactance does.</td>
        </tr>
        <tr>
          <td>An ellipse</td>
          <td>Everything in between; the fatter it is, the nearer 90°.</td>
        </tr>
        <tr>
          <td>A loop with a kink, or a bowtie</td>
          <td>A nonlinearity between the two — a crossover region, a clipping stage, a magnetic hysteresis loop.</td>
        </tr>
        <tr>
          <td>A fuzzy cloud</td>
          <td>The two are unrelated, or one of them is noise.</td>
        </tr>
      </tbody>
    </table>
    <p>
      Three readings travel with the figure and appear in <strong>Measurements</strong>.
      <strong>Correlation</strong> is Pearson’s r over the pairs drawn — +1 when the two move
      together, 0 when they are unrelated or a quarter period apart, −1 when they are mirror
      images — and it is the one figure here that is always meaningful. <strong>Phase</strong> is
      how far the right input lags the left, <em>signed</em>, and is quoted only when both channels
      carry one common tone: on two different tones the figure is a knot and no single number is
      its phase. <strong>Right / left</strong> is the amplitude ratio, written both as a ratio and
      in decibels.
    </p>
    <div class="manual-note">
      <p>
        <strong>Both axes take the same V/div, and the plot is square.</strong> The first reading
        anyone takes off this picture is whether the line sits at 45°, and different scales on the
        two axes would tilt it by an amount no axis label reveals — a matched pair would read as a
        mismatched one. So the <strong>Vertical</strong> section’s V/div sets both, over eight
        divisions, and the centre offset does not apply here.
      </p>
    </div>
    <div class="manual-note">
      <p>
        <strong>Turn Persistence on.</strong> A single record’s figure is a thin loop; a goniometer
        is read as a <em>density</em>, and that is what makes a wandering phase, an intermittent
        channel or a once-in-a-while glitch visible at all. See <strong>Persistence</strong> below —
        it works the same way here, and this is where it earns the most.
      </p>
    </div>
    <p>
      The figure is drawn from <strong>pairs of samples</strong>, which is why it carries its own
      sample count: everywhere else this screen can summarise a wide window as both extremes of
      each column, and here it cannot — a column’s minimum and maximum say that the two channels
      each reached these levels somewhere in that stretch, and throw away which value of one went
      with which of the other, which is the whole of what this picture is. So a long window is
      drawn from its <em>first</em> 8192 pairs and the panel says so. Narrow the window to move the
      stretch. Taking every n-th pair instead would be the obvious way to cover the whole window
      and is the one that lies: a stride near a whole number of periods collapses an ellipse onto a
      line, and you would read a phase error as a matched pair.
    </p>
    <p>
      Which stretch of the record the figure is drawn from is the <strong>Time window</strong>
      section’s From and To, exactly as it is for the waveform. The zoom bar is hidden here — it
      navigates a time axis, which this picture does not have — and because there is no drag to
      zoom with, those two boxes are the only control: switching to X–Y therefore ends any zoom the
      waveform view was holding, so the boxes and the picture always agree. The trigger line, the
      cursors and the notes belong to the waveform view and come back with it.
    </p>

    <h2>Measurements</h2>
    <p>
      Nineteen readings per drawn channel, and <strong>every one of them is measured on the
      analyzer rather than read off the picture</strong>. Maximum, minimum and peak to peak would
      survive the drawing — both extremes of every column are kept — and nothing else would: an
      average of column maxima is an average of the envelope, and a rise time read off a column
      pair is a reading of the column width. So the whole set is taken on the samples themselves,
      at full resolution, over the window on screen.
    </p>
    <table>
      <thead>
        <tr>
          <th>Reading</th>
          <th>What it is</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Maximum, Minimum, Peak to peak</td>
          <td>The extremes of the window, and the distance between them.</td>
        </tr>
        <tr>
          <td>Mean, RMS, AC RMS</td>
          <td>
            The average of the samples, the true RMS with it included, and the RMS about it. On a
            signal riding on an offset, AC RMS is the figure to quote.
          </td>
        </tr>
        <tr>
          <td>Crest factor</td>
          <td>Peak over true RMS. 1.41 for a sine, 1.00 for a square, higher for anything peaky.</td>
        </tr>
        <tr>
          <td>Top, Base, Amplitude</td>
          <td>
            The two <em>settled</em> levels, taken from a histogram of the samples rather than from
            the maximum and the minimum — which is the whole difference between a rise time that is
            right and one that reads fast. A square that overshoots reaches well past the level it
            settles at, and the edge is timed between the settled pair.
          </td>
        </tr>
        <tr>
          <td>Period, Frequency, + Width, − Width, Duty</td>
          <td>Measured between crossings of the middle of the top and base, interpolated between samples.</td>
        </tr>
        <tr>
          <td>Rise, Fall</td>
          <td>10 % to 90 % of the first clean edge, against the settled pair. See the floor below.</td>
        </tr>
        <tr>
          <td>Overshoot, Preshoot</td>
          <td>How far past the top, and below the base, the record goes — as a percentage of the amplitude.</td>
        </tr>
      </tbody>
    </table>
    <p>
      <strong>A reading the waveform does not have is left out rather than dashed.</strong> A period
      needs two crossings, an edge needs two settled levels, and a record with one level — DC,
      silence, structureless noise — has neither, so the list is simply shorter. No attempt is made
      to decide whether a waveform &ldquo;has&rdquo; a top: a sine's samples really do pile up at the
      peaks, so no test separates it from a square, and every bench instrument quotes a top and a
      base for a sine anyway.
    </p>

    <div class="manual-note">
      <p>
        <strong>The inputs are AC-coupled</strong>, on the QA403 and on every sound card. A DC
        offset applied to an input moves the trace for about a second and then it returns, so
        <strong>Mean</strong> on a quiet input is the analyzer's own residual offset — a few
        millivolts, different on each channel, and corrected by nobody — rather than the DUT's DC.
        Measure DC with a multimeter.
      </p>
    </div>

    <h2>The math channel</h2>
    <p>
      <strong>Math</strong> draws one more trace, computed from the two captured waveforms. A is the
      left input and B the right. The derived trace is violet and has <strong>its own axis on the
      right</strong>, which is not a nicety: a differential reading is routinely a thousandth of
      what it came from — the millivolts of difference between two ten-volt signals — and on the
      waveforms’ own axis that is a flat line on zero. Several of the operations are not in volts at
      all, and the axis says which unit it is in.
    </p>
    <table>
      <thead>
        <tr>
          <th>Operation</th>
          <th>Unit</th>
          <th>What it is for</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>A − B, B − A</td>
          <td>V</td>
          <td>
            A <strong>software differential probe</strong>, and the one most of this exists for:
            what is across a transformer winding, a bridged output, a current shunt or a ground
            loop, with neither end of it at ground. Both polarities are offered because a
            differential reading has a sign and there is no second operation to chain.
          </td>
        </tr>
        <tr>
          <td>A + B</td>
          <td>V</td>
          <td>Twice the common-mode part of a differential pair, and the sum of a push-pull stage’s two halves.</td>
        </tr>
        <tr>
          <td>A × B</td>
          <td>V²</td>
          <td>
            Instantaneous power, where one channel measures a voltage and the other the current
            through a shunt — divide by the shunt’s ohms to read watts. The trace itself is in V²,
            because this screen does not know that one of your inputs is a current.
          </td>
        </tr>
        <tr>
          <td>A ÷ B</td>
          <td>—</td>
          <td>
            A ratio, <strong>railed at ±1000</strong>. Only meaningful where B is a settled level:
            against two AC waveforms the denominator passes through zero every cycle and the trace
            rails there, which the rail counts and reports. For a transfer function use the
            frequency-response measurement, which is the right tool for it.
          </td>
        </tr>
        <tr>
          <td>−A, −B</td>
          <td>V</td>
          <td>One input inverted, for comparing an inverting stage against its own input.</td>
        </tr>
        <tr>
          <td>dA/dt, dB/dt</td>
          <td>V/s</td>
          <td>
            The slope — a <strong>slew-rate trace</strong>, written in V/µs at anything a real
            amplifier does. Read the caveat below before quoting a figure off it.
          </td>
        </tr>
        <tr>
          <td>∫A dt, ∫B dt</td>
          <td>V·s</td>
          <td>
            The running integral, which is a flux or a charge. The window’s mean is removed first,
            so the trace starts at zero and is the AC part’s integral rather than an absolute one.
          </td>
        </tr>
      </tbody>
    </table>
    <p>
      The <strong>Measurements</strong> panel gives the derived trace the same reading set every
      waveform gets — peak to peak, RMS, period, rise time and the rest — written in its own unit.
      Peak to peak of A − B is exactly what a differential probe is for.
    </p>
    <div class="manual-note">
      <p>
        <strong>A derivative is honest about the analyzer, not about the DUT.</strong>
        Differentiation lifts the top of the band by 6 dB per octave, which at 192 kS/s is some
        73 dB over 20 Hz — so on a quiet signal the trace is a picture of the quantisation noise
        rather than of the waveform. And the fastest edge this analyzer can produce rises in about
        4 µs against a 5.2 µs sample interval, so a slope read off a fast edge is <em>its</em> slew
        rate unless the DUT is slower than roughly 1 V/µs. For a real figure, drive the DUT and the
        right input from the same generator and compare, or use the power-bandwidth method on
        <strong>THD vs frequency</strong>, where slew limiting reads as distortion climbing as
        roughly f².
      </p>
    </div>
    <div class="manual-note">
      <p>
        <strong>The derived waveform is computed on the samples, never on what is drawn.</strong>
        Where a window is wide the chart draws both extremes of each column rather than every
        sample, and the difference of two <em>envelopes</em> is not the envelope of a difference —
        the two extremes need not even fall at the same instant. Measured on a pair of 1 V sines a
        millivolt apart, subtracting the envelopes gives a flat −1 mV where the real difference is
        a 3 kHz sine of ±1 mV: the wrong sign, the wrong shape, and no minimum at all. So the whole
        operation is run on the analyzer over the window’s own samples, and the figures beside it
        are measurements of that.
      </p>
    </div>
    <p>
      An operation that needs an input the measurement is not capturing draws nothing and says so,
      rather than computing against silence and labelling the result A − B. Tick the input under
      <strong>Acquisition ▸ Channels</strong>.
    </p>

    <h2>Persistence</h2>
    <p>
      With <strong>Keep</strong> set to anything but Off, the chart stops drawing each record over
      the last and starts <strong>accumulating</strong> them: how often the trace passes through a
      pixel decides how that pixel is painted. A steady waveform burns in bright; jitter, noise and
      a once-in-a-while glitch show as a haze around it. The newest record is always drawn on top at
      full strength, so the live trace is still the clearest thing on the chart.
    </p>
    <p>
      <strong>This is the one display here that answers a question the ordinary trace cannot.</strong>
      A glitch one record in fifty is invisible on a chart that redraws each record over the last,
      and unmistakable on one that keeps them — which is what <strong>Infinite</strong> is for: set
      it, leave the scope running in Normal or Auto, and come back.
    </p>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Keep</td>
          <td>
            How many records are kept — 8, 32, 128, or all of them. A <em>record count</em> rather
            than a time, because records arrive at a rate set by the record length, the trigger and
            the DUT: two seconds is anywhere between one record and two hundred.
          </td>
        </tr>
        <tr>
          <td>Colour</td>
          <td>
            <strong>Channel's colour</strong> paints the haze in each trace's own hue, brighter the
            more often the trace goes there — the classic analog-scope picture.
            <strong>Colour map</strong> grades it instead, cold where the trace rarely goes and hot
            where it often does, which is how a digital-phosphor scope draws it.
          </td>
        </tr>
        <tr>
          <td>Map</td>
          <td>Which colour map, when it is one — the same three the decay surfaces use, so a colour means one thing throughout.</td>
        </tr>
      </tbody>
    </table>
    <div class="manual-note">
      <p>
        <strong>The picture is thrown away whenever a pixel would stop meaning the same thing.</strong>
        Zoom, change the V/div or the centre, resize the window or open another measurement, and the
        accumulated history goes: every count in it was a claim about a different part of the
        signal. It starts again from the next record, which is a second or two at most. It is not
        saved with the measurement and not in a report either — what is kept is a history of a
        <em>display</em>, not of a capture, and the setting is what is stored.
      </p>
    </div>

    <h2>The residual</h2>
    <p>
      Tick <strong>Residual</strong> in the display rail and each drawn channel gets a second,
      dashed trace: the window with <strong>one fitted sine subtracted</strong>. What is left is the
      distortion, the noise and anything else the fundamental does not explain — the same view the
      FFT screen's waveform panel offers, on a window you can set.
    </p>
    <p>
      The fit is least-squares and four-parameter, so the <strong>DC term goes with the tone</strong>
      and the <strong>frequency is refined rather than taken</strong>. That second part is what makes
      it work at all: a tone that is not a whole number of samples per period drifts in phase across
      the record, and subtracting a sine at the nearest round frequency leaves a residual that is the
      drift rather than the DUT — half a cycle's worth of it, at the fundamental's own size.
    </p>
    <p>
      The <strong>Residual</strong> rows under the measurements quote the fundamental that was taken
      out, the RMS of what is left, and that as a percentage of the fundamental. The percentage is a
      THD+N figure <em>over this window</em>, and it is not the one
      <button type="button" class="manual-link" @click="showPage('fft-spectrum')">FFT analysis</button>
      gives: that one integrates a measurement bandwidth, while this is simply everything the fit did
      not explain.
    </p>
    <p>
      <strong>Wind the V/div down to see it.</strong> The residual is as small as the distortion is
      — a clean amplifier's is a thousandth of its output, which at the trace's own V/div is a flat
      line along the centre. The ladder reaches 10 µV/div for exactly this; the waveform goes off
      the screen while you look, which is the normal way to read one.
    </p>

    <div class="manual-note">
      <p>
        <strong>A window with no fundamental has none to remove</strong>, and nothing is drawn — no
        period measured means no sine to fit, and a fit started from a guess would subtract an
        invented tone and call the difference distortion. Zoom in past about one cycle and the
        residual disappears for exactly that reason; zoom back out and it returns.
      </p>
    </div>

    <h2>Cursors</h2>
    <p>
      Two pairs of measuring lines, switched on in the display rail and <strong>dragged by the
      lettered grips on the chart</strong> — time along the bottom edge, level down the left. The
      number boxes beside each checkbox are the precise fallback. Switching a pair on places it a
      quarter and three quarters across the window, so it is never stacked somewhere you cannot
      find it.
    </p>
    <ul>
      <li>
        <strong>Time cursors</strong> read where each sits, the time between them, and
        <strong>1/Δt</strong> — the frequency of whatever you have put them a cycle apart on. That
        is a different claim from the measured <strong>Frequency</strong> above, which is taken
        between crossings of the settled middle: the two agreeing is a check, and the two
        disagreeing is itself a reading.
      </li>
      <li>
        <strong>Level cursors</strong> read each level and <strong>ΔV</strong>. They do not gate
        anything — a pair of levels names no stretch of the record.
      </li>
    </ul>
    <p>
      <strong>While the time pair is on, the Measurements panel reads the stretch between them</strong>
      instead of the whole window, and says so above the figures. The window travels to the analyzer
      and it measures those samples afresh: gating on the drawn picture would be a reading of the
      min/max columns rather than a measurement. A pair with no span, or one dragged entirely off
      the record, gates nothing and the panel goes back to the window's own figures rather than
      quoting them under a heading that says otherwise.
    </p>
    <p>
      Cursors belong to the session and not to the measurement: they are not saved with it, they do
      not come back when you reopen it, and they are not in a report. A note you want to keep is an
      <strong>annotation</strong> — see
      <button type="button" class="manual-link" @click="showPage('charts')">Charts</button>.
    </p>

    <h2>Two flags on the header</h2>
    <ul>
      <li>
        <strong>Untriggered</strong> — Auto's timeout took this record; nothing crossed the level,
        so the trace is aligned to nothing. Drag the line onto the trace, or lower the level.
      </li>
      <li>
        <strong>Spliced</strong> — a chunk or block was dropped while this record was filling, so
        two non-adjacent pieces of the stream sit side by side in it. An edge in the middle may be
        the splice rather than the signal. See
        <button type="button" class="manual-link" @click="showPage('reporting-issues')">
          Reporting a problem
        </button>.
      </li>
    </ul>

    <h2>What it cannot show you</h2>
    <p>
      <strong>Nothing faster than about 4 µs on an edge this analyzer produced.</strong> The DAC's
      reconstruction filter corners near 86 kHz, so the fastest edge it can make rises in about
      4 µs — and at 192 kS/s a sample arrives every 5.2 µs, so that edge is about one sample wide. A
      rise time read off it is the analyzer's own unless the DUT is slower than roughly 1 V/µs at
      the test amplitude. For a real slew-rate figure, drive a tone and watch distortion climb with
      frequency at constant amplitude on
      <button type="button" class="manual-link" @click="showPage('thd-vs-frequency')">
        THD vs frequency
      </button>; where it takes off bounds the slew rate at 2·π·f·V<sub>peak</sub>.
    </p>

    <h2>Against the simulator</h2>
    <p>
      Every DUT model is in the path, so the scope is the quickest way to see what each one actually
      does: <strong>SE triode</strong> clipping one half of a sine before the other,
      <strong>Class AB</strong> with its crossover notch at the zero crossings,
      <strong>Oscillating</strong> singing on its own with the generator off. Pick a model in the
      status bar, play a tone from this screen's own generator, and press
      <strong>Single</strong> — see
      <button type="button" class="manual-link" @click="showPage('simulator')">Simulator</button>.
    </p>
  </div>
</template>
