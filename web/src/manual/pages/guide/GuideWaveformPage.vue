<script setup lang="ts">
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Looking at the waveform</h1>

    <p>
      Most of this app answers a question with a number: how much distortion, how flat, how quiet.
      The oscilloscope does something simpler — it draws the signal, the way it actually looks. That
      is what you want when the question is "what on earth is it doing": the top of the sine wave
      flattened off, a kink where it crosses zero, a buzz once a second, a thump when something
      switches on.
    </p>

    <h2>Setting it up</h2>
    <ol>
      <li>
        <strong>Wire it like anything else.</strong> Generator out to the thing you are testing, its
        output back to the left input, shorting plugs on the unused − inputs. The
        <button type="button" class="manual-link" @click="showPage('guide-connecting')">
          plugging-in page
        </button>
        covers it.
      </li>
      <li>
        <strong>Make a new measurement</strong> and choose <strong>Oscilloscope</strong>.
      </li>
      <li>
        <strong>Set the generator</strong> at the top of the left-hand column — a 1 kHz tone at
        −20 dBV is a safe start.
      </li>
      <li><strong>Press Run.</strong></li>
    </ol>

    <h2>Making the picture stand still</h2>
    <p>
      A scope redraws constantly, and if each drawing started at a random moment the trace would
      slide about and you would see nothing. The <strong>trigger</strong> is what stops that: it is
      a voltage, and each picture starts at the moment the signal crosses it. Everything else on
      that panel exists to make the crossing unambiguous.
    </p>
    <ul>
      <li>
        <strong>The dashed horizontal line on the chart is the trigger level.</strong> Drag it with
        the handle on the right. Put it somewhere the trace definitely passes through — half way up
        a sine wave is ideal, the very top is not.
      </li>
      <li>
        <strong>0 V</strong> and <strong>50 %</strong> are shortcuts: ground, and half way between
        the highest and lowest points of what you just captured.
      </li>
      <li>
        <strong>If the trace still jitters</strong>, raise <strong>Hysteresis</strong> a little.
        That tells the trigger to ignore wobbles smaller than the figure you give it, which is what
        a noisy signal needs.
      </li>
    </ul>

    <h2>The three modes</h2>
    <table>
      <thead>
        <tr>
          <th>Mode</th>
          <th>Use it when</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td><strong>Auto</strong></td>
          <td>
            Starting out. If nothing crosses the level it draws something anyway, so you always see
            <em>something</em> — and it says <strong>Untriggered</strong> in the corner so you know
            the picture is not aligned to anything. That label almost always means the level is in
            the wrong place.
          </td>
        </tr>
        <tr>
          <td><strong>Normal</strong></td>
          <td>
            Once the level is right. It only draws when something genuinely crosses, which is what
            you want when you are waiting for an event that happens now and then.
          </td>
        </tr>
        <tr>
          <td><strong>Single</strong></td>
          <td>Catching one thing: a switch-on thump, a click, a one-off glitch. It captures once and stops, and the picture stays until you ask for another.</td>
        </tr>
      </tbody>
    </table>

    <h2>Reading it</h2>
    <p>
      Time runs left to right and <strong>zero is the trigger</strong>, so the part of the trace to
      the left of the middle happened <em>before</em> the thing you triggered on. That is free here
      and it is genuinely useful: you can see what led up to an event, not just what followed it.
    </p>
    <p>
      Up the side, the <strong>V/div</strong> box sets how many volts one square is worth and
      <strong>Centre</strong> moves the middle of the picture up and down. Press
      <strong>Autofit</strong> and the app sizes both for you. Drag a box on the chart to zoom in;
      when you are far enough in the app fetches the real samples rather than a summary, and the
      header says <em>every sample drawn</em>.
    </p>
    <p>
      Underneath, the <strong>Measurements</strong> panel gives the highest point, the lowest, the
      difference between them, and the average.
    </p>

    <h2>What to look for</h2>
    <ul>
      <li>
        <strong>Flat tops.</strong> The amplifier is clipping — it has run out of voltage. Turn the
        generator down until the peaks are round again.
      </li>
      <li>
        <strong>A kink at the middle of each slope</strong>, where the trace crosses zero, is
        crossover distortion: the classic fault of a push-pull amplifier that is biased too cold.
      </li>
      <li>
        <strong>Wobble on top of the wave</strong> is noise or hum riding along with the signal. The
        <button type="button" class="manual-link" @click="showPage('guide-noise')">
          noise and hum page
        </button>
        has the usual culprits.
      </li>
      <li>
        <strong>Ringing after a sharp edge</strong> — a few fast wiggles that die away — is normal
        in small amounts and worth investigating in large ones.
      </li>
    </ul>

    <h2>Two honest limits</h2>
    <ul>
      <li>
        <strong>It will not show you DC.</strong> A steady voltage fades out of the picture within a
        second or so, because the analyzer's inputs only pass changing signals. If you need to know
        a DC voltage, use a multimeter.
      </li>
      <li>
        <strong>It cannot make a truly sharp edge.</strong> The fastest step the generator can
        produce takes about 4 µs to rise, which at the highest sample rate is roughly one dot on the
        screen. So a very fast amplifier will look exactly as fast as the analyzer, and no faster.
      </li>
    </ul>

    <div class="manual-note">
      <p>
        Nothing you do on this screen can break anything, and the capture is only ever a picture of
        what was already there — so this is a good screen to experiment on. Try the
        <button type="button" class="manual-link" @click="showPage('guide-simulation')">
          simulator
        </button>
        with no hardware at all: pick a different DUT in the bar along the bottom and watch the
        shape of the wave change.
      </p>
    </div>
  </div>
</template>
