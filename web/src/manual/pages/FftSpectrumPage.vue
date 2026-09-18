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
    <p>
      It is also the screen to reach for when something is wrong and you do not yet know what. The
      three sweep screens each reduce a whole capture to one number per point; this one shows the
      capture itself.
    </p>

    <h2>What it measures</h2>
    <p>
      The analyzer plays a stimulus of your choosing, captures both inputs continuously, and
      transforms one record at a time. Distortion figures are referred to the fundamental you asked
      the generator for. Noise-based figures — THD+N, SNR, the noise floor — are integrated over the
      metrics bandwidth set in the Configuration dialog, which also keeps DC out of the sum.
    </p>
    <p>
      Levels are in <strong>dBV</strong> (0 dBV = 1 V rms) throughout. Watts appear wherever a load
      has been entered, computed from the measured voltage and that load — the app has no idea what
      your amplifier is really driving, so a wrong load figure makes every power reading wrong with
      it.
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
      other screen's power figures.
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
          </td>
        </tr>
        <tr>
          <td>Tone rows</td>
          <td>
            Frequency in Hz and amplitude in dBV per tone. <strong>+ Tone</strong> adds another; the
            bin icon removes one. Two tones is the usual intermodulation test.
          </td>
        </tr>
        <tr>
          <td>Snap to bin</td>
          <td>
            Moves each tone to the nearest exact FFT bin so it lands on one bin with no leakage into
            its neighbours. Leave it on for distortion work. The frequency you typed stays in the
            field; the grey figure beneath it is what is really playing, which depends on the FFT
            size. Sine tones only.
          </td>
        </tr>
        <tr>
          <td>Tones per octave, Total RMS level, Rolloff per octave</td>
          <td>
            Multitone only: density of the comb, its total level, and a tilt in dB per octave for
            testing with a spectrum shaped more like programme material.
          </td>
        </tr>
      </tbody>
    </table>

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
            same number of points.
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
            default. <strong>Blackman-Harris</strong> trades resolution for far lower skirts, useful
            when a small harmonic sits close to a large fundamental.
            <strong>Flat Top</strong> gives the most accurate amplitude for a tone that is not on an
            exact bin. <strong>Rectangular</strong> is only correct when the tone is exactly
            coherent — which is what Snap to bin arranges.
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
          <td>Spectra averaged / Time constant</td>
          <td>How many records the chosen averaging mode uses.</td>
        </tr>
      </tbody>
    </table>

    <h2>What the views show</h2>

    <h3>Metric tiles</h3>
    <p>
      Above the chart, one violet <strong>Gen</strong> tile summarising what the generator is
      playing, then the selected readings for each processed channel.
      <strong>+ Metrics</strong> chooses which appear; the ✕ on a tile removes it. The choices
      include level in several conventions (<code>dBV</code>, <code>dBu</code>, <code>Vrms</code>,
      watts), distortion as a percentage or in dB, THD+N, SNR, peak and RMS, the measured
      fundamental frequency and its nearest musical note, and gain relative to the generator.
    </p>

    <h3>Spectrum</h3>
    <p>
      Level against frequency, log frequency axis, blue left and red right. Harmonic markers label
      the fundamental and its harmonics. With <strong>Min/max</strong> on, each screen column is
      drawn as a band between the loudest and quietest bin it covers — see
      <button type="button" class="manual-link" @click="showPage('charts')">Reading the charts</button>
      for why that band, and not the line above it, is the true noise floor.
      <strong>Peak-hold</strong> keeps the highest level seen per bin since it was last reset, which
      is how you catch an intermittent spur.
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
      of periods, it shows fewer and says so.
    </p>

    <h3>Harmonics and Other peaks</h3>
    <p>
      <strong>Harmonics</strong> lists the fundamental and each harmonic with its frequency, level
      in dBV and level relative to the fundamental in dBc.
      <strong>Other peaks</strong> lists everything that is neither — mains hum and its harmonics,
      intermodulation products, spurs — largest first, as many as you ask for. Both tables need a
      single fundamental to refer to, so neither appears for Multitone or White noise.
    </p>

    <h2>Display panel</h2>
    <p>
      <strong>dBV range</strong> and <strong>Frequency range</strong> set the spectrum's window,
      each with its own Autofit. The frequency Autofit goes to 10 Hz and just past Nyquist for the
      current sample rate. Below those: <strong>Thick lines</strong>,
      <strong>Show data points</strong>, <strong>Autofit when measurement done</strong>, the channel
      and Min/max and Peak-hold toggles, and finally four checkboxes that show or hide the
      Waterfall, Oscilloscope, Harmonics and Other peaks panels entirely — so a measurement that
      only needs the spectrum does not have to scroll past the rest.
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
