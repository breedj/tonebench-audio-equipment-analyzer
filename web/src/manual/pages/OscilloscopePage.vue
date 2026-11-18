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
            <strong>Roll</strong> has no trigger at all — see <strong>Roll</strong> below.
          </td>
        </tr>
        <tr>
          <td>Records</td>
          <td>
            What the analyzer does with each record after the first — <strong>Normal</strong>,
            <strong>Average</strong> or <strong>Envelope</strong>. See <strong>Averaging and
            envelope</strong> below. Disabled under Single and Roll, which produce nothing to
            combine.
          </td>
        </tr>
        <tr>
          <td>Average over</td>
          <td>
            How many records the mean is of, 2 to 256. Shown only under <strong>Average</strong>;
            the note under the control says how long that many will take to settle.
          </td>
        </tr>
        <tr>
          <td>Kind</td>
          <td>
            <strong>Edge</strong>, <strong>Pulse width</strong>, <strong>Window</strong>,
            <strong>Dropout</strong> or <strong>Runt</strong> — what the analyzer is looking for. See
            <strong>The advanced triggers</strong> below. Edge is what a scope does unless you tell
            it otherwise.
          </td>
        </tr>
        <tr>
          <td>Source</td>
          <td>
            Which channel the trigger watches — or <strong>Stimulus</strong>, which watches no
            channel at all and fires on the generator's own repeat. See <strong>The stimulus
            trigger</strong> below. A channel source must be one of the captured channels.
          </td>
        </tr>
        <tr>
          <td>Slope</td>
          <td>
            Rising, falling or either — and <em>what</em> that selects depends on the kind. On an
            edge trigger it is the direction through the level; on a pulse-width trigger it decides
            which stretch is the pulse (rising measures the stretch <em>above</em> the level); on a
            window trigger it chooses between entering and leaving the band.
          </td>
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
          <td>Second level / Second threshold (V)</td>
          <td>
            The other of the two voltages the window and runt triggers need. The order does not
            matter — the chart and the capture both sort the pair — and both kinds draw the band
            between them. Shown only on those kinds.
          </td>
        </tr>
        <tr>
          <td>No edge within (ms)</td>
          <td>
            The dropout trigger's timeout: how long an absence of qualifying edges has to last.
            Milliseconds, because this is for a signal that <em>stops</em> rather than for a glitch.
            Shown only on that kind.
          </td>
        </tr>
        <tr>
          <td>Condition, Width (µs)</td>
          <td>
            The pulse-width trigger's condition — <strong>shorter than</strong>,
            <strong>longer than</strong> or <strong>between</strong> — and the width or widths it is
            set by. Microseconds, because that is the scale this control is for. Shown only on that
            kind.
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

    <h2>The advanced triggers</h2>

    <p>
      An edge trigger fires on <em>every</em> crossing, so a fault that happens once in a thousand
      cycles is one trace in a thousand and is never on screen when you look at it. The other two
      kinds select the fault itself.
    </p>

    <h3>Pulse width — the glitch trigger</h3>

    <p>
      A pulse here is a stretch on one side of the level: with <strong>Slope</strong> rising, the
      stretch <em>above</em> it, measured from the crossing up to the crossing back down. Set a
      width condition and the analyzer fires on a pulse that meets it and on nothing else. A
      crossover notch is a pulse far narrower than the half-cycle around it; an output that has
      latched is one far wider; neither can be picked out by a level alone.
    </p>

    <p>
      <strong>Both crossings are interpolated</strong>, so a width is measured to a fraction of a
      sample rather than to the nearest one — at 192 kS/s a sample is 5.2 µs, which on a 50 µs
      glitch would be a tenth of the figure. What no interpolation can do is show a pulse narrower
      than one sample: there is no such pulse in any record, so a <strong>shorter than</strong>
      condition below the sample interval is refused rather than left to never fire. Raising the
      sample rate is the lever there.
    </p>

    <p>
      <strong>When it fires depends on which bound you set, and that is deliberate.</strong> With an
      upper bound — "shorter than 50 µs" — the width is not known until the pulse ends, so it fires
      on the pulse's <em>trailing</em> edge. With a lower bound alone — "longer than 50 µs" — it
      fires as soon as the pulse has lasted that long, without waiting for an end, because a level
      that has stuck never produces one and that is exactly the fault the condition is set for. With
      100&nbsp;% pre-trigger the record then shows what led up to it.
    </p>

    <h3>Window — in and out of a band</h3>

    <p>
      Two levels make a band, and the trigger fires on the signal <strong>entering</strong> it
      (Slope rising), <strong>leaving</strong> it (falling) or both (either). It is the trigger for
      something that is supposed to stay put: set the band around a bias point or a rail and trigger
      on leaving, and the capture is of the moment it misbehaved.
    </p>

    <p>
      The band is drawn on the chart between the two lines, in the trigger source's own colour — it
      is to this kind what the hysteresis band is to an edge trigger, the one thing that makes two
      numbers in the rail aimable at a signal. A level off the vertical window is clamped to the
      edge and drawn solid rather than dashed, the way a single level is.
    </p>

    <p>
      <strong>A signal that jumps clean through the band in one sample does not fire.</strong> Each
      sample is either inside or outside and only a transition counts, so a step from below the band
      to above it is outside-to-outside: nothing was ever observed inside. That is also the honest
      answer, since a band narrower than one sample's worth of slew cannot be seen to have been
      entered — and it is why a very tight band simply never fires rather than firing on every edge.
    </p>

    <h3>Dropout — the trigger that waits for nothing</h3>

    <p>
      Every other trigger waits for something to happen. This one fires when a qualifying edge has
      <em>not</em> happened for as long as you set, which is what an intermittent fault actually
      looks like: an amplifier that mutes for a tenth of a second, an oscillator that drops out, a
      connection that goes open. None of them produces an event to trigger on, and with 100 %
      pre-trigger the record is the signal right up to the moment it stopped.
    </p>

    <p>
      <strong>Slope</strong> decides what counts as a sign of life — <strong>Either</strong> means a
      crossing in any direction. Raise the <strong>hysteresis</strong> here before anything else: a
      signal sitting on the level crosses it constantly on its own noise, so without it a dead input
      looks perfectly alive and the trigger never fires at all. Two things worth knowing. The clock
      counts from the <em>first sample of the run</em> as well as from each edge, so an input that
      is already dead fires after one timeout rather than waiting for ever. And a signal that stays
      dead goes on firing once a timeout, which is what keeps something on screen.
    </p>

    <p>
      <strong>It will also fire on a hole in the stream</strong>, and that is worth recognising
      before it is read as a DUT fault. A dropped USB chunk or a starved DAC is a stretch of
      recording with no edges in it. The record says which it was: a capture that spans a drop is
      marked <strong>spliced</strong> on the chart and in the saved measurement, because a hole and
      a dead amplifier look identical in a waveform.
    </p>

    <h3>Runt — an edge that did not make it</h3>

    <p>
      A runt is a pulse that crosses one threshold and fails to reach the other: a stage that cannot
      quite swing to the rail on one cycle in a thousand, a supply that sags under a transient. Set
      the two thresholds either side of where a good pulse goes, and <strong>Rising</strong> catches
      a pulse up that never reaches the upper one. It fires on the way back, where "it never got
      there" becomes known.
    </p>

    <p>
      Neither a level nor a width can pick a runt out — it is exactly as wide as a good pulse and it
      crosses exactly the same lower threshold — which is the whole reason the kind exists. At audio
      bandwidth it is the most marginal of the five: the same fault usually reads better as
      distortion against level on a THD sweep. It earns its keep on a switching or logic waveform,
      and on anything where one cycle in a thousand is the question.
    </p>

    <h2>The stimulus trigger</h2>

    <p>
      This analyzer composes the stimulus, so the stimulus's phase is known exactly rather than
      inferred from the signal coming back — and <strong>Source ▸ Stimulus</strong> turns that into
      a trigger. It fires on the generator's own repeat: no level, no slope, no kind, and no
      jitter. No bench oscilloscope has it, because none of them generates what you are measuring.
    </p>

    <p>
      <strong>What it is for is a stimulus with no edge to find.</strong> A chirp, a periodic-noise
      sequence and a multitone comb have no feature an edge trigger can lock to, so they draw as a
      wash that redraws differently every capture. Triggered on the stimulus they stand perfectly
      still — which is what makes a transient visible in one, and what makes <strong>Average</strong>
      worth anything on them at all, since averaging rests on sample i of every record being the
      same point on the waveform. On a square or a triangle it is still worth having: the trigger
      point is exact rather than interpolated, so there is no sub-sample residue left over.
    </p>

    <p>
      It needs a stimulus whose repeat is a whole number of samples. A <strong>square</strong> and a
      <strong>triangle</strong> are built that way and are what this screen's own generator offers;
      a chirp qualifies too, and is what the frequency-response and impulse measurements play. A
      plain tone is refused, and deliberately — its period is the sample rate over its frequency, a
      whole number of samples only for frequencies nobody types, and rounding it would walk the
      trace sideways a fraction of a sample per cycle. A tone has a clean crossing to trigger on
      instead. Noise is refused for a different reason: the noise this panel stages runs
      continuously rather than repeating, so there is nothing to lock to.
    </p>

    <p>
      <strong>What it is not is absolute phase.</strong> Between the generator writing a sample and
      the ADC returning it there is the output queue, the DUT and the analog path, and this app does
      not measure that delay. So t&nbsp;=&nbsp;0 is a consistent point of the stimulus offset by an
      unknown constant, not the stimulus's own zero crossing. Consistent is the whole of what a
      stable trace needs; for an absolute figure, measure the delay — that is what the frequency
      response's group delay reading is.
    </p>

    <h2>Averaging and envelope</h2>

    <p>
      <strong>Records</strong> decides what happens to each capture after the first, and the two
      modes beyond Normal answer opposite questions.
    </p>

    <table>
      <thead>
        <tr><th>Mode</th><th>What it answers</th></tr>
      </thead>
      <tbody>
        <tr>
          <td>Normal</td>
          <td>Each record replaces the one before it. What a scope does.</td>
        </tr>
        <tr>
          <td>Average</td>
          <td>
            <em>What does this signal look like with the noise taken out?</em> The mean of the last
            N records, sample position by sample position: the uncorrelated part falls as
            <strong>√N</strong> while the signal stays — 12 dB of noise floor at 16 records, 24 dB
            at 256. The badge on the chart counts up while it fills.
          </td>
        </tr>
        <tr>
          <td>Envelope</td>
          <td>
            <em>How much does it move?</em> The band every sample position has swept since the run
            started. A signal that repeats exactly draws as a line; jitter, drift, modulation and a
            fault that happens once in a hundred cycles all draw as a band.
          </td>
        </tr>
      </tbody>
    </table>

    <h3>Averaging needs a trigger that is standing still</h3>

    <p>
      <strong>This is the one thing that can go wrong silently.</strong> Averaging works because
      sample <em>i</em> of every record is the same point on the same repeating signal — which is
      true only as far as the trigger puts it there. Average records that are <em>not</em> aligned
      and the signal itself is low-passed: the trace comes out smaller and rounder, which reads as
      quieter. Nothing in the analyzer can tell those two apart, so get the trigger steady first
      (raise <strong>Hysteresis</strong> until the trace stands still under Normal), and treat a
      trace whose <em>amplitude</em> fell when you turned averaging on as a trigger problem rather
      than a measurement.
    </p>

    <p>
      Two consequences worth knowing. A record that is marked <strong>Spliced</strong> is left out
      rather than combined — two non-adjacent pieces of the stream are not the same point on the
      waveform as everything already in the average. And anything that changes <em>what</em> is
      being combined starts it again: the mode, the count, the record length, the pre-trigger
      position, a Clear, or a new run.
    </p>

    <h3>Records are aligned between samples before they are combined</h3>

    <p>
      The trigger is interpolated — the analyzer reports where the crossing really was, to a
      fraction of a sample — but a record has to <em>start</em> on a whole sample, so every capture
      ends up with its trigger a different fraction of a sample from its own first frame. One record
      does not care. Combining them does, and nothing about the symptom points at the cause: a
      residual offset spread over half a sample attenuates a sinusoid by
      <strong>0.9 dB at a quarter of the sample rate and 3.6 dB at 0.95 of Nyquist</strong>, which
      reads as a <em>smaller</em> waveform rather than a misaligned one.
    </p>

    <p>
      So each record after the first is read across onto the first one's fractional grid, with the
      same windowed-sinc reconstruction the <strong>sin(x)/x</strong> drawing uses. Measured on a
      60 kHz tone at 192 kS/s — three and a bit samples a period — averaging sixteen records costs
      <strong>0.013 dB</strong> of amplitude, where without the alignment it would cost 1.44. Two consequences
      worth knowing: the <strong>first</strong> record of an accumulation is used exactly as
      captured, so a single capture is never interpolated and its figures are readings of the
      samples themselves; and the correction leaves a small error in the sixteen samples at each
      <em>end</em> of an averaged record, where there is no signal outside to read — the same fact
      the filter's padding is about. Keep an edge you are timing away from the very edges of the
      window.
    </p>

    <p>
      <strong>This is the useful half of equivalent-time sampling, and the other half would buy
      nothing here.</strong> A bench scope interleaves records taken at different fractional offsets
      to synthesise a sample rate above its own, which works because its sampler is slower than its
      front end. On this analyzer the band limit <em>is</em> the ADC's own decimation filter, applied
      before the samples exist — so there is nothing between the samples that the samples do not
      already determine, and nothing above Nyquist left to recover. Interleaving would add noise
      averaging that plain averaging already gives, and no information at all. Measured: reading one
      record between its samples agrees with a record genuinely taken at that offset to within 1 %
      of full scale at 3.2 samples a period, which is the reconstruction's own residue that close to
      Nyquist.
    </p>

    <h3>An envelope is not a waveform</h3>

    <p>
      It is two outlines, so only the questions that are about a band have answers:
      <strong>Vmax</strong>, <strong>Vmin</strong> and <strong>Vpp</strong>. Everything else in the
      Measurements panel is <em>absent</em> rather than computed — a mean or an RMS of an outline is
      arithmetic on something that was never a signal, a period or a duty cycle would be a reading
      of the accumulation rather than of the DUT, and a rise time between two outlines is the jitter
      and the edge added together. For the same reason the <strong>math channel</strong>, the
      <strong>X–Y figure</strong>, the <strong>eye</strong> and the <strong>residual</strong> are all
      absent on an envelope.
    </p>

    <p>
      The envelope keeps the worst case rather than the last N records, because what it is read for
      is “how far does this ever move” — a bound that quietly forgot a bad record three captures ago
      would answer a different question every time you looked. <strong>Clear</strong> starts the band
      again.
    </p>

    <p>
      Neither mode is offered under <strong>Single</strong>, which holds one record and so has
      nothing to combine it with, or under <strong>Roll</strong>, whose windows overlap by all but
      one roll step — averaging those would low-pass the signal and an envelope of them would be the
      same band drawn N times.
    </p>

    <h3>Two modes a bench scope has that this one does not need</h3>

    <p>
      <strong>Peak detect</strong> is already how every record works here: the capture keeps every
      sample, and the chart draws the minimum and the maximum of each display column, so nothing
      wider than one sample can hide between pixels. There is nothing to switch on. Zoom in until
      the header reads <em>every sample drawn</em> and you are looking at the samples themselves.
    </p>

    <p>
      <strong>High resolution</strong> — averaging adjacent samples to trade bandwidth for vertical
      detail — is the <strong>Signal path</strong>’s low-pass filter, which does the same thing where
      you can see its corner and its slope rather than behind a single button.
    </p>

    <h2>Roll</h2>

    <p>
      <strong>Roll draws the newest samples as they arrive, with no trigger.</strong> What is on
      screen is always the last <em>record length</em> of the stream, so <strong>t&nbsp;=&nbsp;0 is
      now</strong> and the whole record sits to its left: the axis runs from
      −<em>record</em>&nbsp;to&nbsp;0 rather than straddling a trigger. Entering or leaving Roll
      therefore moves the time window onto the new axis, because a window from the other side of
      that switch can hold no samples at all.
    </p>

    <p>
      It is for the signals a trigger cannot help with: a drifting bias, a thermal effect, a supply
      sagging over seconds, a slow sweep by hand — anything whose period is comparable with the
      record or which has no period at all. Set a long record (up to a second) and watch it scroll.
    </p>

    <p>
      Four things follow from there being no trigger, and all four are on screen rather than left to
      be worked out. The chart carries a <strong>Rolling</strong> badge. <strong>No trigger line and
      no trigger marker are drawn</strong> — the level, the slope and the hysteresis had no part in
      where this window starts, so a line across the trace would be read as something it is not, and
      those controls are disabled in the Trigger panel while Roll is selected. There is no
      <em>Untriggered</em> flag either, since nothing was meant to trigger. And a saved Roll capture
      reopens with t&nbsp;=&nbsp;0 at its right-hand edge, which is where it was.
    </p>

    <p>
      The window is built <strong>when it is asked for</strong> rather than committed on a clock, so
      a rolling display costs only what the screen is actually reading — and a drop in the stream
      shortens the history available for a moment rather than splicing two pieces of it together: a
      rolled record is never marked spliced, because a window that would cross a break is refused.
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
          <td>Drawing</td>
          <td>
            How one sample is joined to the next — <strong>Lines</strong>, <strong>sin(x)/x</strong>
            or <strong>Dots</strong>. See <strong>How the trace is drawn</strong> below. The last
            two need the samples themselves, so they apply only where the window is tight enough
            for the header to read <em>every sample drawn</em>.
          </td>
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
            Which picture the chart draws — <strong>Y–T</strong>, the waveforms against time,
            <strong>X–Y</strong>, one input against the other, or <strong>Eye</strong>, every cycle
            folded on the signal’s own period. See <strong>The X–Y view</strong> and
            <strong>The eye view</strong> below.
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
          <td>History</td>
          <td>
            Stored results ticked here are drawn behind the live trace as reference traces — see
            <strong>Reference traces</strong> below. Up to three.
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

    <h2>How the trace is drawn</h2>
    <p>
      <strong>Lines</strong> is what a scope has always drawn and is exact wherever there are
      plenty of samples a cycle. Near the top of the band it stops being true: a sampled waveform
      is band-limited by the analyzer’s own filter, so between any two samples there is exactly one
      signal it could have been — and that is not the straight line.
    </p>
    <p>
      At 48 kS/s a 1 kHz tone has 48 samples a period and the difference is invisible. A 20 kHz
      tone has 2.4, the samples land on a different part of every cycle, and lines between them
      draw a jagged shape whose peak wanders from cycle to cycle. That reads as amplitude
      modulation or as a dirty generator, and it is neither. <strong>sin(x)/x</strong> draws the
      band-limited waveform instead: measured on a tone at 2.37 samples a period, straight lines
      are out by about a third of full scale and the reconstruction by under one per cent.
    </p>
    <p>
      <strong>Dots</strong> draws the samples and nothing joining them — the honest picture when
      what you are reading is where the samples fell, and the one that can never show something the
      analyzer did not measure.
    </p>
    <div class="manual-note">
      <p>
        <strong>It is reconstruction, not resolution.</strong> sin(x)/x adds no information: it
        draws the one waveform consistent with the samples <em>and</em> with the band limit the
        hardware already imposes. It is right because that band limit is real — the ADC cannot pass
        anything above Nyquist — and it would be just as wrong as the straight lines, in a
        different direction, for a signal that broke it.
      </p>
    </div>
    <div class="manual-note">
      <p>
        <strong>Both need the samples, and a wide window has none.</strong> Past a couple of samples
        a column the analyzer sends both extremes of each column instead of the samples, and which
        values fell where inside one is exactly what that leaves out. So on a wide window the two
        fall back to lines and the rail says so. Zoom in until the chart header reads <em>every
        sample drawn</em>.
      </p>
    </div>

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

    <h2>The eye view</h2>

    <p>
      Pick <strong>Eye</strong> and the window is cut into single cycles of the signal’s own
      measured period and all of them are drawn on top of each other. A signal that repeats exactly
      folds into <em>one line</em>. Anything that moves it from one cycle to the next spreads that
      line into a band — and the shape of the band says what moved it.
    </p>

    <p>
      This is not the eye diagram of a data link. There is no symbol clock to recover here: a comms
      eye folds on the clock and reads the opening a receiver has to decide in, where this folds on
      the tone and reads <strong>repeatability</strong>. What it finds is the class of fault that
      looks like “a slightly fuzzy trace” on a time axis and like nothing at all in a spectrum
      averaged over seconds.
    </p>

    <table>
      <thead>
        <tr><th>What you see</th><th>What it usually is</th></tr>
      </thead>
      <tbody>
        <tr>
          <td>One clean line</td>
          <td>
            Every cycle in the window is the same. The starting point — a loopback at a round
            frequency folds to a single line, which is how you know the view is working.
          </td>
        </tr>
        <tr>
          <td>A band widest at the peaks</td>
          <td>
            The amplitude is varying: a rail sagging under drive, a thermal effect, a generator that
            is not settled, or hum at a frequency unrelated to the tone riding on it. The
            <strong>Where</strong> reading names the phase.
          </td>
        </tr>
        <tr>
          <td>A band widest at the crossings</td>
          <td>
            The timing is varying — jitter. The <strong>Jitter</strong> reading is the standard
            deviation of the folds’ own crossings, measured between samples, so it reads below the
            5.2 µs sample interval.
          </td>
        </tr>
        <tr>
          <td>A band at one phase only</td>
          <td>
            Something is switching at that point in the cycle: a crossover, a protection circuit, a
            class-B stage’s handover.
          </td>
        </tr>
        <tr>
          <td>Two or three distinct lines</td>
          <td>
            The signal alternates between states rather than wandering — every other cycle
            different, which a smooth band would not show.
          </td>
        </tr>
      </tbody>
    </table>

    <h3>Reading it</h3>

    <p>
      <strong>The traces are translucent, and that is the measurement rather than the styling.</strong>
      Where every cycle agrees the strokes land on each other and the line reads solid; where they
      disagree the band is as faint as the number of cycles that went through each part of it. One
      opaque stroke would draw a hundred agreeing cycles and a single outlier as the same flat line,
      which is the one thing this picture must not do. Turn <strong>Persistence</strong> on as well
      and the overlay accumulates across captures, which is what makes an intermittent fault visible.
    </p>

    <p>
      The dashed vertical marks where the band is widest — the phase the <strong>Where</strong>
      reading names. The readings under the chart are of the overlay rather than of the record:
      <strong>Spread</strong> is how far apart the cycles got at their worst phase, not the
      waveform’s own amplitude, and <strong>Mean spread</strong> is the band’s average width, which
      is the figure to watch where the worst case is set by one bad cycle.
    </p>

    <h3>When there is no eye</h3>

    <p>
      The view says which of three things it is rather than coming up blank. <strong>No frequency
      was measured</strong> in this window, so there is no period to fold on — widen it until the
      Measurements panel reads one. <strong>Fewer than two whole periods</strong> are in the window,
      so there is nothing to overlay. Or nothing has been captured yet. A picture folded on a
      guessed period would be a band of noise that looks exactly like a jittery signal, so it is
      never drawn.
    </p>

    <p>
      Two limits are worth knowing. The overlay draws at most <strong>256 cycles</strong>, and the
      first of them rather than every n-th — striding the cycles would hide exactly the one-in-three
      fault someone turns this on to find; the header says when the window held more, and narrowing
      the window moves the overlay along the record. And a period longer than about 10 ms at
      192 kS/s (94 Hz) is drawn at reduced resolution: every cycle is drawn at the same coarser
      step, so they stay aligned, but the fine detail between those points is not in the picture.
    </p>

    <p>
      One more, and it is a real floor rather than a setting. A period is almost never a whole
      number of samples, so each cycle is placed at its exact fractional position and read
      <em>between</em> samples — without that, a perfectly repeating signal would draw as a band
      that grows with the length of the overlay. What is left is the straight line between two
      samples, which at 48 samples a cycle is about a thousandth of the amplitude and near the top
      of the band is much more. So <strong>a reading of a few tenths of a per cent on a high
      frequency is the picture’s own floor, not the signal’s</strong>: compare a suspect
      measurement against the same signal at a lower frequency, or against a loopback, before
      believing a small spread.
    </p>

    <p>
      The eye is drawn for <strong>one input</strong> — the first one the measurement captures,
      named on the axis. Two bands at different levels over each other are a wash rather than two
      readings, and the figures are per channel.
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

    <h2>Reference traces</h2>

    <p>
      Tick a stored result in <strong>History</strong> and it is drawn on the chart behind the live
      trace, dashed and in its own shade — a reference. Up to three at once, which is the overlay
      limit everywhere in this app. The point of it is the comparison a scope is mostly used for:
      the amplifier before a change against the amplifier after it, or one channel's capture against
      the other's.
    </p>

    <p>
      <strong>A reference is cut to the same window as the live trace, by the analyzer, every time
      the window moves.</strong> That is what makes the two comparable: a stored capture shown at
      its own full length beside a 1 ms zoom would be two pictures of different stretches of time
      laid over each other. Zoom in and the references come back cut to the zoom; drag the zoom bar
      and they follow it. They are read through the <strong>signal path</strong> too — the same
      coupling and the same filter as the live trace — because a reference read through a different
      filter is a comparison of two different things rather than of two measurements.
    </p>

    <p>
      Both records are laid out <em>against the trigger</em>, so t = 0 is t = 0 on both however much
      pre-trigger each was taken with. Where a reference was taken at a different sample rate its
      samples are placed by time rather than one-for-one, so a 48 kS/s capture drawn beside a
      192 kS/s one keeps its real timebase. Where a reference does not reach as far as the window
      does, it stops: the trace simply has a gap there rather than a flat line along zero.
    </p>

    <table>
      <thead>
        <tr><th>What it shows</th><th>Reading</th></tr>
      </thead>
      <tbody>
        <tr>
          <td>Dashes</td>
          <td>
            Which reference it is, and that it is not the live trace. The dash is what tells them
            apart in a black-and-white report and in a screenshot; the shade is the quicker read on
            screen.
          </td>
        </tr>
        <tr>
          <td>Thin</td>
          <td>
            Deliberate: a reference is there to be compared against rather than read, and the live
            trace is drawn over it.
          </td>
        </tr>
        <tr>
          <td>Its own channel colour</td>
          <td>
            Blue for the left input, orange for the right, pulled toward that reference's own hue.
            A reference of a left-only measurement draws one trace.
          </td>
        </tr>
      </tbody>
    </table>

    <h3>What a reference does not do</h3>

    <p>
      <strong>Nothing is measured off it.</strong> The Measurements panel, the cursors' gated
      figures, the math channel and the residual are all of the <em>live</em> record — a reference is
      a picture. To read a stored capture's own figures, open it: selecting it in History loads it as
      the record, and then every reading on the panel is its.
    </p>

    <p>
      Checking a result does not change the picture's own history either: the persistence haze is a
      history of the live display, so a reference is drawn as a line and never accumulated into it,
      and neither the sample markers nor the min/max band of a second and third reference are drawn
      — one band is a reading, three over each other are a wash.
    </p>

    <p>
      References belong to the measurement that is open, and unticking is how they go away. Opening
      another measurement clears them, which is the rule on every screen here: a measurement's chart
      draws its own results.
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
