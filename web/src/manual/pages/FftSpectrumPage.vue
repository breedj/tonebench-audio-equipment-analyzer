<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>FFT analysis</h1>

    <p>
      One capture, transformed into a spectrum. This is the screen that answers "what is actually
      in this signal": the fundamental and its harmonics, mains hum, intermodulation products, the
      broadband noise floor, and the summary numbers computed from all of them — THD, THD+N, SNR,
      RMS level, output power.
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
      ·
      <button type="button" class="manual-link" @click="showPage('wiring-loopback')">
        loopback
      </button>
    </p>
    <p>
      It is also the screen to reach for when something is wrong and you do not yet know what. The
      three sweep screens each reduce a whole capture to one number per point; this one shows the
      capture itself.
    </p>

    <h2>What it measures</h2>
    <p>
      The analyzer plays a stimulus of your choosing, captures both inputs continuously, and
      transforms one record at a time. Distortion figures are referred to the fundamental you asked
      the generator for. Noise-based figures — THD+N, SNR, the noise floor, the band RMS and the
      noise density — are integrated over the <strong>Bandwidth</strong> chosen in the Bandwidth
      &amp; weighting panel, which also keeps DC out of the sum, and optionally through a
      <strong>Weighting</strong> curve (A, C, ITU-R 468, CCIR/ARM, RIAA, or your own table). A weighting changes what "noise"
      means and nothing else: the fundamental, the harmonics, THD, gain and peak are always measured
      flat, and THD+N and SNR keep the unweighted fundamental as their reference.
    </p>
    <p>
      Levels are in <strong>dBV</strong> (0 dBV = 1 V rms) throughout. Watts appear wherever a load
      has been entered, computed from the measured voltage and that load — the app has no idea what
      your amplifier is really driving, so a wrong load figure makes every power reading wrong with
      it. With an <strong>Ext. gain</strong> declared on the status bar, every level is the DUT's
      output — what reached the analyzer through the divider, corrected by that gain — and that
      badge turns amber to say so.
    </p>

    <h2>Running it</h2>
    <table>
      <tbody>
        <tr>
          <td>RUN</td>
          <td>
            Starts the generator and measures continuously, updating about twenty times a second,
            until STOP.
          </td>
        </tr>
        <tr>
          <td>SINGLE</td>
          <td>
            Captures one settled spectrum — the configured number of averages, or one record if
            averaging is off or the count is 1 — and stops by itself. This is what you want before
            saving a result. It measures exactly what RUN measures, capture delay included.
          </td>
        </tr>
        <tr>
          <td>STOP</td>
          <td>Ends acquisition and silences both generator channels.</td>
        </tr>
      </tbody>
    </table>
    <p>
      Edits made while a measurement is running are applied live, so you can walk the generator
      level up and watch the harmonics grow without stopping.
    </p>

    <h2>Generator panel</h2>
    <p>
      Left and right play the <em>same</em> stimulus; the two checkboxes at the top only decide
      which outputs are actually driven. The <strong>Load</strong> in ohms is shared with every
      other screen's power figures. The DUT's own gain is not set here — it lives under the
      <strong>Units</strong> dropdown in the display panel, described under Level range below.
    </p>
    <p>
      At a sample rate this analyzer can capture at but not play — 384 kHz on a QA403, if you have
      enabled it in Configuration ▸ Acquisition — the whole panel steps aside and says so. Nothing is
      lost: the stimulus you had set up is still there and comes back the moment you choose a lower
      rate, and both outputs stay silent meanwhile, which is what that rate is for.
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
          <td>Stimulus</td>
          <td>
            <strong>Sine tones</strong> — one or more tones, each with its own frequency and level.
            <strong>Multitone</strong> — a comb of tones across 5 Hz–20 kHz.
            <strong>White noise</strong> — flat noise at a set level.
            <strong>Square</strong> and <strong>Triangle</strong> — one hard-switched waveform at a
            set frequency and level, for looking at edges rather than at a spectrum (see
            <em>Square and triangle</em> below).
          </td>
        </tr>
        <tr>
          <td>IMD preset</td>
          <td>
            Sine tones only. A standard two-tone intermodulation test — <strong>SMPTE 60 Hz + 7 kHz
            (4:1)</strong>, <strong>DIN 250 Hz + 8 kHz (4:1)</strong>, <strong>CCIF/ITU-R 19 kHz +
            20 kHz (1:1)</strong> or <strong>CCIF 18.5 + 19.5 kHz (1:1)</strong> — sets both tones up
            from one <strong>Level</strong>: the <em>sine-equivalent</em> level, i.e. the level
            of a single sine whose peak equals the pair's peak (a 4:1 pair sits 1.94 and 13.98 dB
            below it, a 1:1 pair 6.02 dB below). Picking a preset also turns the IMD reading on and
            adds the <strong>IMD %</strong> and <strong>IMD dB</strong> tiles. <strong>Custom</strong>
            frees the tone rows again; any two tones still get a reading.
          </td>
        </tr>
        <tr>
          <td>IMD method, Max order</td>
          <td>
            Shown whenever exactly two tones are staged. <strong>SMPTE / DIN</strong> counts the
            sidebands f2 ± n·f1 relative to the HF carrier; <strong>CCIF / ITU-R</strong> counts the
            difference products |m·f1 − n·f2| relative to the RMS sum of both tones. Max order is the
            highest product order counted (SMPTE 5, CCIF 3 by default). See
            <button type="button" class="manual-link" @click="showPage('imd-vs-amplitude')">IMD vs amplitude</button>
            for the conventions in full.
          </td>
        </tr>
        <tr>
          <td>Tone rows</td>
          <td>
            Frequency in Hz and level per tone. <strong>+ Tone</strong> adds another; the bin icon
            removes one. Read-only while a preset is active. Two tones is the usual intermodulation
            test.
          </td>
        </tr>
        <tr>
          <td>Level unit</td>
          <td>
            The dropdown beside each level box — <strong>dBV</strong>, <strong>V</strong> or
            <strong>mV</strong> — sets what every level on the panel is typed in: the tone rows, the
            preset's sine-equivalent level, the multitone comb's total RMS and the noise level. One
            choice for all of them, saved with the measurement. <strong>Rolloff per
            octave</strong> stays in dB — it is a ratio, not a level. It is a display choice only:
            the generator is always told dBV, so switching units never changes what is playing.
          </td>
        </tr>
        <tr>
          <td>Snap to bin</td>
          <td>
            Moves each tone to the nearest exact FFT bin so it lands on one bin with no leakage into
            its neighbours. <strong>On by default</strong> for a new measurement, and leave it on for
            distortion work. The frequency you typed stays in the field; the grey figure beneath it
            is what is really playing, which depends on the FFT size. Sine tones and the multitone
            comb (snapped tone by tone); noise and the chirp have nothing to snap, and a square or
            triangle has its own rounding instead (below). Switching to a stimulus that can snap
            turns it back on; selecting a saved measurement restores whatever that one was saved
            with.
          </td>
        </tr>
        <tr>
          <td>Frequency, Level (rms)</td>
          <td>
            Square and triangle only. The level is the waveform's <em>rms</em>, like every other
            stimulus here, so one level means three different peaks: a square's peak equals its rms,
            a triangle's is 1.73 times it and a sine's 1.41. The grey lines beneath report what is
            really playing — the frequency after rounding, the samples per cycle, how many harmonics
            fit below Nyquist, the peak volts, and the levels an ideal shape's first harmonics sit
            at, to compare the Harmonics table against.
          </td>
        </tr>
        <tr>
          <td>Tones per octave, Total RMS level, Rolloff per octave</td>
          <td>
            Multitone only: density of the comb, its total level, and a tilt in dB per octave for
            testing with a spectrum shaped more like programme material. Mind the crest factor: the
            comb's tones add in phase, so 60 tones at −20 dBV total peak near 1.5 V — keep the total
            low enough that the peaks stay clear of the analyzer's range and the DUT's rails, or the
            TD+N reading is your own clipping.
          </td>
        </tr>
      </tbody>
    </table>

    <h2>Square and triangle</h2>
    <p>
      These two are transient tests rather than spectral ones. A square's edge excites everything at
      once, so overshoot, ringing, tilt and asymmetry on the oscilloscope say in one glance what a
      frequency sweep says in a minute: a flat top with a clean corner is a wide, well damped
      amplifier; a rounded corner is a limited high end; a few cycles of ringing on the step is a
      resonance, and its period is where; a sagging top is a limited low end; different behaviour on
      the rising and falling edges is an asymmetry in the output stage. A triangle has the same odd
      harmonics falling away far faster (1/n² instead of 1/n), so it asks much less of the high end
      and is the gentler probe when the square's own edge is what you keep measuring.
    </p>
    <p>
      <strong>The frequency is rounded, on purpose.</strong> One cycle is built from a whole, even
      number of samples, which is what keeps the waveform exactly periodic: every harmonic above
      Nyquist then folds back onto an odd harmonic of the fundamental instead of landing at some
      unrelated frequency, so the spectrum holds the shape's own lines and nothing else. The cost is
      that only a sample period is available: 1 kHz is exact at every rate, but 10 kHz at 192 kHz
      becomes 9.6 kHz (20 samples a cycle) and at 48 kHz becomes 12 kHz (4 samples). The panel says
      what it settled on. The ceiling is a quarter of the sample rate, where a cycle is four samples
      and a square has become a sine.
    </p>
    <p>
      <strong>The harmonics are known, which is what makes the reading useful.</strong> An ideal
      square has only odd harmonics, each 1/n of the fundamental: −9.5 dBc at H3, −14.0 at H5, −16.9
      at H7. A triangle has 1/n²: −19.1, −28.0, −33.8. Whatever your DUT does to those levels is its
      own high-frequency response, read straight off the harmonics table. Two caveats. The folding
      above lifts the top of the series slightly — 0.17 dB by H7 at 20 samples a cycle, more as the
      harmonic approaches Nyquist — so compare the lower harmonics, not the last ones. And THD+N,
      SNR, N+D, N−D and the noise tiles read <code>n/a</code>: the stimulus <em>is</em> harmonics, so
      an ideal square reads THD+N around 48 % and SNR around 6 dB however clean the amplifier is
      (a little less in a narrower measurement bandwidth, which leaves the top of the series out).
      The <strong>THD</strong> tile is a different sum again — only the harmonics the analyzer
      tracks, a dozen by default — so it reads lower still, around 44 % on an ideal square. Compare
      the harmonics line by line rather than reading one number.
    </p>
    <p>
      <strong>What a square cannot measure here is slew rate.</strong> The stimulus edge is not a
      step: the DAC's own reconstruction filter rolls off above about 86 kHz, so the fastest edge the
      analyzer can produce rises in roughly 4 µs — and at 192 kHz the captured record carries one
      sample every 5.2 µs, so the edge is about one sample wide on screen. A rise time you read off
      it is the analyzer's, not the DUT's, unless the DUT is slower than about 1 V/µs at the
      amplitude you are driving; the 384 kHz input rate would halve the sampling interval but the
      generator cannot be used there at all. Any overshoot and ringing on the first cycle of the step
      is partly the reconstruction filter's too, which is why the honest way to run this test is to
      loop the generator straight into the <strong>right</strong> input while the DUT drives the
      left: the right trace is then the stimulus as it really leaves the analyzer, and the difference
      between the two traces is the amplifier.
    </p>
    <p>
      For slew rate, use
      <button type="button" class="manual-link" @click="showPage('thd-vs-frequency')">
        THD vs frequency
      </button>
      instead, at the full output level you care about. Slew limiting shows up as distortion climbing
      steeply with frequency at constant amplitude — roughly as f² once it starts — and the frequency
      where it takes off bounds the slew rate at 2·π·f·V<sub>peak</sub>. That is the classical
      power-bandwidth measurement, and it is a real number from this analyzer rather than a reading
      of its own DAC.
    </p>

    <h2>FFT settings panel</h2>
    <table>
      <thead>
        <tr>
          <th>Control</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Left / Right</td>
          <td>
            Which inputs are processed and drawn. The ADC always samples both; this decides what is
            analysed, and processing one channel instead of two costs half the work.
          </td>
        </tr>
        <tr>
          <td>Sample rate</td>
          <td>
            Sets the top of the analysed band to half of it — 48 kHz reaches 24 kHz, 192 kHz reaches
            96 kHz. Take the higher rates when you need to see high-order harmonics of a high
            fundamental; take a lower one when you do not, since the record covers more time for the
            same number of points. The experimental 384 kHz appears here only once it is enabled in
            Configuration ▸ Acquisition, and it is capture-only: picking it takes the Generator panel
            out of service for as long as it is selected.
          </td>
        </tr>
        <tr>
          <td>FFT size</td>
          <td>
            Points per record. Bin width is sample rate ÷ FFT size, so a bigger FFT resolves finer
            detail — separating hum sidebands from a tone, or reaching down to a few hertz — and
            takes proportionally longer per record. 65536 at 192 kHz is about 0.34 s and 2.9 Hz per
            bin.
          </td>
        </tr>
        <tr>
          <td>Window</td>
          <td>
            How each record is tapered before transforming. <strong>Hann</strong> is the sensible
            default. <strong>Blackman-Harris</strong>, <strong>Kaiser 5</strong> and
            <strong>Kaiser 7</strong> are three rungs of the same trade — each one widens the
            mainlobe a little further to push the skirts a long way down, which is what you want
            when a small harmonic sits close to a large fundamental. <strong>Flat Top</strong>
            gives the most accurate amplitude for a tone that is not on an exact bin.
            <strong>Rectangular</strong> is only correct when the tone is exactly coherent — which
            is what Snap to bin arranges.
          </td>
        </tr>
        <tr>
          <td>Averaging</td>
          <td>
            <strong>Off</strong> shows each record raw. <strong>Linear</strong> averages a fixed
            number of records and then holds. <strong>Exponential</strong> averages continuously
            with the given time constant, so the display keeps tracking a changing signal. Averaging
            lowers the visible noise floor; it does not change the level of a steady tone.
          </td>
        </tr>
        <tr>
          <td>Averages</td>
          <td>
            Beside Averaging, once it is on: how many records a Linear average takes before it holds,
            or the time constant of an Exponential one, in records.
          </td>
        </tr>
      </tbody>
    </table>

    <p>
      The windows in order of that trade. <em>Noise bandwidth</em> is how many bins wide a single
      bin really is, so it is both the resolution cost and the factor a noise reading is divided
      by; <em>skirt suppression</em> is how far the highest sidelobe sits below the tone. The
      Kaiser numbers are the <em>α</em> of the classic Kaiser–Bessel family (β = πα), the same
      naming ARTA uses — not the bare β that scipy and MATLAB take, which would mean something
      much weaker.
    </p>
    <table>
      <thead>
        <tr>
          <th>Window</th>
          <th>Noise bandwidth</th>
          <th>Skirt suppression</th>
          <th>Use it for</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Rectangular</td>
          <td>1.00 bins</td>
          <td>13 dB</td>
          <td>A record that already holds whole cycles — a snapped tone, a periodic chirp.</td>
        </tr>
        <tr>
          <td>Hamming</td>
          <td>1.36 bins</td>
          <td>43 dB</td>
          <td>Resolving two tones close together, when nothing far below them matters.</td>
        </tr>
        <tr>
          <td>Hann</td>
          <td>1.50 bins</td>
          <td>32 dB</td>
          <td>The default, and the right answer for noise.</td>
        </tr>
        <tr>
          <td>Blackman-Harris</td>
          <td>2.00 bins</td>
          <td>92 dB</td>
          <td>A harmonic close to a large fundamental.</td>
        </tr>
        <tr>
          <td>Kaiser 5</td>
          <td>2.28 bins</td>
          <td>120 dB</td>
          <td>Distortion measurement where the skirts of the fundamental would otherwise sit above the harmonics.</td>
        </tr>
        <tr>
          <td>Kaiser 7</td>
          <td>2.68 bins</td>
          <td>171 dB</td>
          <td>The same, down to the floor of a 24-bit converter. Nothing here resolves better than this, and nothing needs to.</td>
        </tr>
        <tr>
          <td>Flat Top</td>
          <td>3.77 bins</td>
          <td>93 dB</td>
          <td>Reading a tone's amplitude when it is not on an exact bin — calibration, not distortion.</td>
        </tr>
      </tbody>
    </table>
    <p>
      One caveat on the two Kaisers: a harmonic's amplitude is summed over a fixed ±3 bins, and a
      Kaiser's main lobe is wider than that (about ±5 bins at α = 5 and ±7 at α = 7). The energy
      left outside is small — 0.00004 dB and 0.002 dB — so it does not change a reading, but it is
      the reason to reach for Kaiser 7 for <em>separation</em> rather than for absolute level.
    </p>

    <h2>Bandwidth &amp; weighting panel</h2>
    <p>
      Below FFT settings, and separate from it on purpose: the FFT size, window and averaging decide
      how a record is <em>transformed</em>, while these two decide what the numbers read from it
      <em>mean</em>. Both apply live, like the FFT settings, and both are saved with the measurement.
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
          <td>Bandwidth</td>
          <td>
            The measurement band every noise figure is integrated over: <strong>20 Hz – 20 kHz</strong>,
            <strong>20 Hz – 80 kHz</strong> (only meaningful at 192 kHz, where Nyquist is 96 kHz), or
            <strong>Custom</strong>, which is whatever the Configuration dialog's Metrics bandwidth
            fields hold — choosing Custom changes nothing by itself. Note that the upper edge also
            bounds which harmonics count toward THD and which intermodulation products are "in
            band", so 80 kHz counts more of both.
          </td>
        </tr>
        <tr>
          <td>Weighting</td>
          <td>
            <strong>None</strong>, or a curve applied to the noise figures and the displayed
            spectrum: <strong>A</strong> and <strong>C</strong> (IEC 61672), <strong>ITU-R 468</strong>
            (the broadcast noise curve, +12.2 dB at 6.3 kHz), <strong>CCIR/ARM</strong> (468 re-zeroed
            to 0 dB at 2 kHz, the Dolby convention: 5.6 dB more than 468 everywhere),
            <strong>RIAA playback</strong> and <strong>RIAA record</strong> (its inverse — weighting
            a phono stage's output by the record curve reports its noise as a flat chain would).
            Every curve is 0 dB at 1 kHz except CCIR/ARM. Weighted tiles carry a small amber tag
            naming the curve, and the chart shows the same name beside its title.
          </td>
        </tr>
        <tr>
          <td>User 1, User 2</td>
          <td>
            Two slots for your own weighting tables, each loaded from a text file with one
            <code>frequency, dB</code> pair per line — the QA40x's format, where a <em>positive</em>
            value is attenuation and a negative one is gain (the tutorial's ITU-R 468 file says
            <code>6300, -12.2</code> for the curve's peak). Comma or whitespace separated; blank lines,
            a header line and lines starting with <code>#</code> are ignored. Values are interpolated
            linearly in dB over log frequency and held constant outside the table. The
            <strong>…</strong> button picks a <code>.txt</code> file with the in-app file dialog,
            <strong>✕</strong> clears the slot; the tables live in the configuration database, so
            they survive a restart and are shared by every project. Ticking a slot combines it with
            the standard curve (the two multiply), and a ticked slot with no table weighs nothing.
          </td>
        </tr>
      </tbody>
    </table>

    <h2>What the views show</h2>

    <h3>Metric tiles</h3>
    <p>
      Above the chart, one violet <strong>Gen</strong> tile summarising what the generator is
      playing, then the selected readings for each processed channel.
      <strong>Metrics</strong> chooses which appear; the ✕ on a tile removes it. The choices
      include level in several conventions (<code>dBV</code>, <code>dBu</code>, <code>Vrms</code>,
      watts), distortion as a percentage or in dB, THD+N, SNR, peak and RMS, the measured
      fundamental frequency and its nearest musical note, and gain relative to the generator.
      <strong>IMD %</strong> and <strong>IMD dB</strong> read only while two sine tones are staged —
      no reading is not a reading, so with any other stimulus the tiles are simply absent.
      <strong>TD+N %</strong> and <strong>TD+N dB</strong> likewise appear only while a Multitone
      comb plays: total distortion plus noise, everything found between the comb's tones over the
      comb's own RMS (see the Multitone table below).
    </p>
    <p>
      Three tiles are for noise work. <strong>RMS dBV(bw)</strong> is the RMS of everything inside
      the measurement band (the plain <strong>RMS dBV</strong> is the whole record, time domain, no
      band and no weighting). <strong>Noise dBV/√Hz</strong> is the noise density: the mean over the
      band with the fundamental's notch and the harmonics taken out, so it reads the same at every
      FFT size — a −150 dBV/√Hz floor over 20 Hz – 20 kHz is −107 dBV of noise.
      <strong>Noise in nV/√Hz</strong> is the same density referred to the DUT's input through the
      DUT gain in the display panel: −150 dBV/√Hz is 31.6 nV/√Hz at 0 dB and 3.16 nV/√Hz through
      20 dB. All three exist with the generator off, when there is no fundamental to report. When a
      weighting is selected, the tiles it applies to — THD+N, N+D, N-D, SNR and these three — carry
      an amber tag naming it (<code>A</code>, <code>468</code>, <code>A+U1</code>); the fundamental,
      THD, gain, peak and RMS tiles never do, because nothing weights them.
    </p>
    <p>
      Those same tiles read <code>n/a</code> whenever the stimulus is more than one tone — an IMD
      preset, any Sine tones stimulus with two or more tones, or a Multitone comb — and for a square
      or a triangle, whose own harmonic series fills the noise band. Every one of them
      is defined against a single fundamental: the loudest tone is treated as the signal and
      everything else in the band as noise and distortion. Play a second tone and the figure is
      still arithmetically correct and completely meaningless — SMPTE's 7 kHz tone sits 12.04 dB
      under its 60 Hz one, which reads as THD+N 399.8 % and SNR −12.04 dB on a perfectly healthy
      analyzer. Hovering an <code>n/a</code> tile says why. THD, the harmonics table, the IMD figure
      and its products table are unaffected and keep their numbers — those are what a two-tone test
      is for, and TD+N is the multitone equivalent. A report of such a measurement prints
      <code>n/a</code> for the same rows and gives the reason once per channel.
    </p>

    <h3>Spectrum</h3>
    <p>
      Level against frequency, log frequency axis, blue left and red right. Harmonic markers label
      the fundamental and its harmonics. With <strong>Min/max</strong> on, each screen column is
      drawn as a band between the loudest and quietest bin it covers — see
      <button type="button" class="manual-link" @click="showPage('charts')">Reading the charts</button>
      for why that band, and not the line above it, is the true noise floor, and why it opens out
      into a pale haze towards the right of the plot without the noise having changed at all.
      <strong>Peak-hold</strong> keeps the highest level seen per bin since it was last reset, which
      is how you catch an intermittent spur.
    </p>
    <p>
      <strong>FFT data</strong> is the live capture — the trace and the min/max band under it, which
      is the same data — and it is the one to uncheck once peak-hold has caught what you were waiting
      for. Unchecking it clears <strong>Min/max</strong> with it and leaves the peak-hold curve on
      its own: the worst case so far, with nothing redrawn over the top of it twenty times a second.
      Peak-hold keeps collecting while the trace is hidden, <strong>Reset peak-hold</strong> starts
      it again, and ticking FFT data back on brings the band back with it.
    </p>
    <p>
      <strong>Harmonic markers</strong> are the vertical lines at the fundamental and each harmonic,
      labelled with that order's level in dBc. Unticking it leaves the trace clean: at the low end of
      a log axis the first few orders land within a handful of pixels of one another, and over a
      dense trace or a two-channel capture the lines can hide more than they point at. The
      <strong>Harmonics</strong> panel under the chart reads the same figures either way, and so does
      a report. It does not affect the gap markers a multitone stimulus draws, which belong to the
      multitone table rather than to any one fundamental.
    </p>
    <p>
      With a weighting selected the trace itself is weighted — the A curve's roll-off below 1 kHz is
      visible on a flat noise floor — and an amber <strong>A-weighted</strong> (or
      <strong>ITU-R 468</strong>, <strong>User 1 (name)</strong> …) tag sits beside the chart's title
      so a weighted trace is never mistaken for a flat one. The harmonic markers are still placed at
      the harmonics' unweighted levels, so on a weighted trace they can sit off the line by the
      curve's value at that frequency. Peak-hold and the waterfall are cleared whenever the
      weighting or the unit changes, since their history would otherwise mix two scales.
      <strong>Weighting curve</strong> in the Display panel draws the curve in effect (the standard
      one combined with any ticked user table) as a dashed amber line — its shape, not a level: a
      weighting is a gain, so the line is hung with its 0 dB mark 20 dB below the top of the level
      range, labelled at the left edge. Raise or lower the range's Top to move it. Nothing is drawn
      while the weighting is None, and a saved result's report never draws it, since the curve is
      whatever is selected now rather than what the result was captured under.
    </p>

    <h3>Waterfall</h3>
    <p>
      The last several seconds of spectra stacked downward in time, with level as colour — one
      channel at a time, chosen with the L/R buttons, because the colour axis is already spent on
      level. <strong>Auto levels</strong> tracks the colour range to what is on screen; turning it
      off lets you pin the two dBV bounds so two recordings can be compared. It is the quickest way
      to see something that comes and goes: hum that drifts, a burst of noise, an oscillation that
      starts only under drive.
    </p>

    <h3>Oscilloscope</h3>
    <p>
      The captured waveform, triggered on a zero crossing of the left channel, showing the number of
      generator periods you ask for. Both channels are drawn from the same instant, so a phase
      difference between them is real. If the captured record is too short for the requested number
      of periods, it shows fewer and says so. <strong>Residual</strong> shows the same window with
      the fundamental removed: a sine is fitted to the whole captured record (frequency, amplitude,
      phase and offset — the frequency refined, so an unsnapped tone cancels too) and subtracted,
      leaving the DUT's distortion in the time domain. Crossover distortion shows as spikes at the
      zero crossings, a soft clip as bumps at the peaks, and a harmonic residue as a periodic ripple
      whose order you can count against the raw trace. The trigger still comes from the raw left
      channel, so the residual lines up with the waveform it came from; the axis autoscales to the
      residual, which is typically tens of dB below the tone. It needs a fundamental — with a
      multitone or noise stimulus the box says so and shows nothing. The setting is saved with the
      measurement type and follows the FFT screen's report.
    </p>

    <h3>Harmonics, Intermodulation and Other peaks</h3>
    <p>
      <strong>Harmonics</strong> lists the fundamental and each harmonic with its frequency, level
      in dBV and level relative to the fundamental in dBc. How each of those levels is arrived at —
      the peak search, the skirt sum, the noise-floor subtraction — and how they are combined into
      the THD tiles is set out under
      <button type="button" class="manual-link" @click="showPage('thd-vs-frequency')">
        THD vs frequency
      </button>.
      <strong>Intermodulation</strong> appears next to it while two sine tones are staged: both
      tones, then every counted product (labelled IM2, IM3 … with its combination, e.g. f2 − 2·f1)
      with frequency, level and dBc against the method's reference, and underneath the IMD total,
      each order's figure, the pair's sine-equivalent level and its true RMS. If the FFT is too
      small to separate the tones (they need to be at least 8 bins apart), the table says so
      instead of showing a number.
      <strong>Other peaks</strong> lists everything that is neither — mains hum and its harmonics,
      intermodulation products, spurs — largest first, as many as you ask for. Both tables need a
      single fundamental to refer to, so neither appears for Multitone or White noise. A square or
      triangle keeps them both: those shapes have one unmistakable fundamental, and the harmonics
      table against their known series is most of what the measurement is for. Other peaks
      starts collapsed; its ▸ opens it.
    </p>
    <p>
      <strong>Multitone</strong> takes the Harmonics table's place while a Multitone comb plays. With
      every tone's own bins set aside, whatever is left <em>between</em> adjacent tones is the DUT's
      distortion products plus the noise floor, and the table reads it per gap: one row per tone,
      with the tone's level, then the highest line between it and the next tone (frequency, level,
      and dBr — relative to the comb's total RMS, √ΣV², the same reference for every row and for the
      total) and the gap's D+N, the RMS of everything in it. Underneath, <strong>TD+N</strong> is the
      power sum of every resolved gap over the comb, in percent and dB, with how many gaps were
      resolved. The chart marks each resolved gap's highest line with a tick and its dBr. A gap whose
      two tones sit closer than 8 bins at the current FFT size reads <em>unresolved</em>: nothing
      between them can be separated from their own window lobes — at 5 tones per octave the
      bottom of the comb is under 1 Hz apart, which no practical FFT resolves, so the low rows merge
      and are reported as such; a larger FFT resolves lower. Nothing below the first tone or above
      the last is counted, and the analysis band's upper limit bounds the gaps like THD+N's. The
      tones are measured like harmonics (skirt-summed, floor-subtracted); the highest line in a gap
      the same way, with its skirt kept inside the gap; the gap's D+N is the plain integral — it is
      distortion <em>plus noise</em> by definition. Keep <strong>Snap to bin</strong> on: the comb
      is snapped tone by tone, and an unsnapped comb reads its own window sidelobes in every gap —
      about −60 dBr under Hann on a straight wire — as if they were distortion. Saved with the
      result and printed in its report.
    </p>

    <h2>Display panel</h2>
    <p>
      <strong>Level range</strong> and <strong>Frequency range</strong> set the spectrum's window.
      One <strong>Autofit</strong>, under the level range, fits both: the frequency axis to 10 Hz and
      just past Nyquist for the current sample rate, and the level axis to the highest and lowest
      point plotted with 10 dB of headroom. The frequency <strong>To</strong> list ends at 100 kHz,
      which covers a 192 kHz capture whole; enabling the experimental 384 kHz rate adds a 200 kHz
      entry to it, so the wider band can be chosen by hand and not only reached with Autofit. <strong>Units</strong> picks what the level axis reads:
      <strong>dBV</strong> per bin; <strong>dBV/√Hz</strong>, a noise density, which is the bin's
      power spread over the window's equivalent noise bandwidth (dBV − 10·log₁₀ ENBW, about
      −6.4 dB for Hann at 65536 points and 192 kHz) so a noise floor sits at the same height
      whatever the FFT size — and a tone, which has no density, reads lower on a coarser grid;
      or <strong>dBV/√Hz (input-referred)</strong>, the same less the DUT gain. Switching units
      refits the range. Picking the input-referred unit reveals a <strong>DUT gain</strong> field
      under the dropdown: the DUT's gain in dB from its own input to the analyzer's input, so a
      preamp's output floor reads as the noise at its input. It also divides the
      <strong>Noise in nV/√Hz</strong> tile, which is why the field stays visible once it is
      non-zero even if the axis is switched back to dBV — a hidden field always means 0 dB, and
      nothing on screen is changed by a value you cannot see. Leave it at 0 for a loopback. It is
      saved with the measurement, unlike the load. Note it is not the same as the
      <strong>Ext. gain</strong> on the status bar, which describes an attenuator or probe
      <em>between</em> the DUT's output and the analyzer and corrects every measured level. A history overlay converts with its own window, so two results taken at
      different FFT sizes compare correctly on the density axis. Below those: <strong>Thick lines</strong>,
      <strong>Show data points</strong>, <strong>Autofit when measurement done</strong> (on for a
      newly created measurement), the <strong>Channels</strong> row, the FFT data, Min/max,
      Peak-hold, Harmonic markers and Weighting curve toggles, and finally four checkboxes that show or hide the
      Waterfall, Oscilloscope, Harmonics and Other peaks panels — so a measurement that only needs
      the spectrum does not have to scroll past the rest. A new measurement starts with the
      Oscilloscope and Harmonics shown and the Waterfall and Other peaks hidden; tick them on where
      you want them, and the choice is saved with the measurement. The waterfall and oscilloscope
      are only hidden: what they showed is still there when they are ticked back on.
    </p>

    <h2>Zooming</h2>
    <p>
      Drag horizontally across the spectrum to zoom the frequency axis; double-click, or press
      <strong>Reset zoom</strong> in the chart header, to go back to the full span. A zoom asks the
      server for that window at full resolution, so zooming in genuinely reveals more detail rather
      than magnifying what was already drawn. The level axis is not draggable — set it from the
      display rail.
    </p>

    <h2>Saving a result</h2>
    <p>
      Stop acquisition first, then press <strong>Save measurement result</strong>: what is on screen
      is what gets stored, together with the metrics, the settings that produced it and a preview.
      <strong>Clear</strong> empties the chart, scope and meters without touching anything saved.
    </p>

    <div class="manual-note">
      <p>
        If the status bar shows <strong>dropped</strong> chunks or blocks, the noise floor and THD+N
        on screen are not trustworthy — a dropped chunk splices the sample stream, which smears
        tones and lifts the floor. Re-run the measurement rather than reading through it.
      </p>
    </div>
  </div>
</template>
