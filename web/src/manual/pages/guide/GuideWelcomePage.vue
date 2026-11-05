<script setup lang="ts">
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>What this app does</h1>

    <p>
      An audio analyzer does two things at once. It <strong>plays a test signal</strong> — usually a
      pure tone — into whatever you want to test, and at the same time it <strong>listens</strong> to
      what comes back out. Then it draws the difference. That is all a measurement is here: send
      something known, look at what returns.
    </p>
    <p>
      The box on your bench is a QuantAsylum QA403. It has the generator and the two very quiet
      inputs. This app drives it over USB, does all the maths on your computer, and keeps the results
      in a file you choose. Nothing is uploaded anywhere.
    </p>

    <h2>What you can find out</h2>
    <table class="manual-wrap">
      <thead>
        <tr>
          <th>Question</th>
          <th>The measurement that answers it</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Is my amplifier clean?</td>
          <td>FFT analysis — one tone in, and you see every extra tone the amplifier added.</td>
        </tr>
        <tr>
          <td>Bass and treble alike?</td>
          <td>Frequency response — a sweep from the bottom of the band to the top.</td>
        </tr>
        <tr>
          <td>How loud before it breaks up?</td>
          <td>THD vs amplitude — distortion against level, so you can watch clipping start.</td>
        </tr>
        <tr>
          <td>Is it humming?</td>
          <td>FFT analysis with the generator off — hum appears as spikes at 50 Hz and its multiples.</td>
        </tr>
        <tr>
          <td>What is this loudspeaker doing?</td>
          <td>Impedance — one resistor and two clips, and you get the whole curve.</td>
        </tr>
      </tbody>
    </table>
    <p>
      There are a dozen more, and they all work the same way: press a green button, watch a chart,
      keep the ones worth keeping.
    </p>

    <h2>What you need on the bench</h2>
    <ul>
      <li>The QA403 and its USB cable.</li>
      <li>
        <strong>Two or three BNC cables</strong> — the twist-lock coaxial sort. Most measurements
        need two.
      </li>
      <li>
        <strong>Four BNC shorting plugs.</strong> These are not optional and they are not spares.
        Without them every measurement is far worse than it should be, and nothing on screen says so.
        The
        <button type="button" class="manual-link" @click="showPage('guide-connecting')">
          next page
        </button>
        explains why.
      </li>
      <li>For an amplifier: a <strong>load resistor</strong> of the right value and wattage.</li>
      <li>The thing you want to measure.</li>
    </ul>

    <h2>You can learn all of this with no hardware at all</h2>
    <p>
      In the status bar along the bottom of the window there is a box marked
      <strong>Analyzer</strong>. Switch it from <strong>QA40x</strong> to <strong>Simulator</strong>
      and the app invents the signals instead of measuring them. Every screen works, every button
      does what it normally does, and the numbers are realistic.
    </p>
    <p>
      Beside it is a <strong>DUT</strong> box — "DUT" is what everyone calls the
      <em>device under test</em>. It lets you pretend to have a valve amplifier, a loudspeaker, a
      phono stage, an amplifier that clips, one that hums, even one that has started oscillating. It
      is the cheapest way to learn what a fault looks like before you meet it for real, and it cannot
      damage anything. If you are reading this before your analyzer has arrived, start there.
    </p>

    <div class="manual-note">
      <p>
        One safety rule before anything else: the inputs take up to 40 V, and a power amplifier can
        easily produce more. Never connect an amplifier's output to the analyzer before reading
        <button type="button" class="manual-link" @click="showPage('guide-connecting')">
          Plugging everything in
        </button>.
      </p>
    </div>

    <h2>Where to go next</h2>
    <ul>
      <li>
        <button type="button" class="manual-link" @click="showPage('guide-connecting')">
          Plugging everything in
        </button>
        — the sockets, the plugs, and the two ways to break something.
      </li>
      <li>
        <button type="button" class="manual-link" @click="showPage('guide-first-measurement')">
          Your first measurement
        </button>
        — ten minutes, step by step, with the analyzer measuring itself.
      </li>
      <li>
        <button type="button" class="manual-link" @click="showPage('guide-decibels')">
          Decibels and volts
        </button>
        — if "−20 dBV" does not mean anything to you yet, read this early. It takes five minutes and
        everything afterwards is easier.
      </li>
    </ul>
  </div>
</template>
