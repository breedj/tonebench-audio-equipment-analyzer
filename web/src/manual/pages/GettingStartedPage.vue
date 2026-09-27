<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Getting started</h1>

    <p>
      This app drives a QuantAsylum QA403 over USB, does its own signal processing, and stores every
      result in a project file you name and place yourself. Nothing is uploaded anywhere and nothing
      installs itself; the analyzer window is an ordinary browser tab talking to a server running on
      this machine.
    </p>

    <h2>The device</h2>
    <p>
      The status bar along the bottom is the whole device story. A green dot and the product name
      mean a QA403 is open and ready. <strong>Disconnected</strong> with a <strong>Connect</strong>
      button means none was found — plug one in and press it; the app starts fine without a device,
      so this is a normal state to arrive in rather than a failure.
      <strong>Device removed</strong> means one was unplugged or powered off mid-session: plug it
      back in and press Connect again.
    </p>
    <p>
      <strong>Simulated</strong> means there is no real hardware in the loop at all — the app was
      started with <code>--simulate</code> and is synthesizing signals with a known amplitude,
      distortion and noise floor. Everything in this manual works the same way against the
      simulator, which is the easiest way to learn a screen without tying up a bench.
    </p>

    <h2>Projects and measurements</h2>
    <p>
      Work is organised two levels deep. A <strong>project</strong> is a body of work — one
      amplifier, one investigation — and it is one <code>.qa403</code> file on disk. Inside it live
      <strong>measurements</strong>: named,
      configured instruments, listed in the sidebar on the left. A measurement is not a
      reading — it is a saved <em>setup</em>, such as "1 kHz THD at 1 W" or "response into 8 Ω",
      which you select, run, and run again later under the same conditions.
    </p>
    <ol>
      <li>
        Create a project with <strong>New project…</strong> on the welcome screen, or open one from
        its recent list — later, from the <strong>Project</strong> menu at the top left.
      </li>
      <li>Press <strong>+ New measurement</strong> in the sidebar and choose a type.</li>
      <li>Set it up, run it, and save the readings you want to keep.</li>
    </ol>
    <p>
      New measurements are added to the bottom of the list. To reorder it, drag a measurement by
      the <strong>⠿</strong> grip on the left of its row and drop it where you want it — a blue
      line shows where it will land. The order is per project and is saved as soon as you drop.
    </p>
    <p>
      The sidebar can be <strong>unpinned</strong> with the pin button beside
      <strong>+ New measurement</strong>. Unpinned, it collapses to a column of icons — one per
      measurement, still selectable, with the name in a tooltip — and gives the chart the width it
      was using; moving the pointer onto the column opens the full menu over the chart, and moving
      off it closes it again. Pin it back with the same button. Whether it is pinned is remembered
      on this machine, for every browser tab. On a window narrower than 1080 px a pinned sidebar
      behaves as the column of icons anyway, and comes back as a column when the window is wider
      again.
    </p>
    <p>
      A red dot next to a measurement's name means its settings have been changed but not saved.
      <strong>Save</strong> stores the settings into that measurement, <strong>Save as…</strong>
      makes a new measurement from them, and <strong>Project ▸ Save</strong> saves every changed
      measurement in the project at once.
    </p>

    <h2>The five measurement types</h2>
    <table>
      <thead>
        <tr>
          <th>Type</th>
          <th>Answers</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>FFT analysis</td>
          <td>What is in the signal right now — spectrum, THD, harmonics, hum, noise floor.</td>
        </tr>
        <tr>
          <td>THD vs frequency</td>
          <td>How distortion changes across the band at one drive level.</td>
        </tr>
        <tr>
          <td>THD vs amplitude</td>
          <td>How distortion changes with level at one frequency — where clipping starts.</td>
        </tr>
        <tr>
          <td>IMD vs amplitude</td>
          <td>Two-tone intermodulation (SMPTE, DIN or CCIF) against level — the distortion test that still works past a class-D output filter.</td>
        </tr>
        <tr>
          <td>Frequency response</td>
          <td>Gain and phase across the band, measured fast with a chirp.</td>
        </tr>
        <tr>
          <td>Stepped sine response</td>
          <td>The same curves, one tone at a time — slower, but nothing else is playing.</td>
        </tr>
        <tr>
          <td>Crosstalk</td>
          <td>How much one channel leaks into the other, in dB against frequency, one direction or both.</td>
        </tr>
      </tbody>
    </table>
    <p>
      Each has its own chapter in this manual, reachable from the index or from the
      <strong>?</strong> button on the measurement screen itself.
    </p>

    <h2>Running a measurement</h2>
    <p>
      Every screen starts a measurement from the same place: a green button at the top of the
      settings rail on the left.
    </p>
    <ul>
      <li>
        <strong>RUN</strong> starts the generator and measures continuously until you press
        <strong>STOP</strong>. On the two sweep screens the same slot is <strong>RUN SWEEP</strong>,
        which walks its list of points once and stops on its own.
      </li>
      <li>
        <strong>SINGLE</strong> measures once and stops itself — one settled spectrum on the FFT
        screen, one complete pass on the chirp screen.
      </li>
      <li>
        <strong>STOP</strong> (or <strong>CANCEL</strong> on the sweeps) ends the measurement and
        silences the generator. Whatever was already measured stays on screen.
      </li>
    </ul>
    <p>
      Nothing you type reaches the hardware until you press one of those. Selecting a measurement in
      the sidebar loads its settings; it does not start playing anything.
    </p>

    <div class="manual-note">
      <p>
        The generator drives real equipment. Check the level before pressing RUN, especially in the
        output-referred level modes, which raise the generator until the <em>device under test</em>
        reaches the target you asked for.
      </p>
    </div>

    <h2>Ranges are automatic, until you say otherwise</h2>
    <p>
      The two figures at the right of the status bar — <strong>Attenuation</strong> (input range)
      and <strong>Output range</strong> — are readouts while <strong>Ranging</strong> is on
      <strong>Auto</strong>, which is how the app starts and what every measurement wants. The input
      range tracks the signal, and the output range is derived from the generator amplitude, because
      it is an analog attenuator whose setting sets the noise floor of the DAC. Leaving it wide for a
      quiet tone would put that noise into every measurement, so the app never does.
    </p>
    <p>
      Switch Ranging to <strong>Manual</strong> and both become dropdowns, pinned until you change
      them. That is worth doing for one job: comparing a reading against another analyzer, or
      against an earlier measurement of your own. The input range in particular changes what you
      measure — the analyzer's own distortion rises as its ADC runs closer to full scale — so two
      programs that choose different ranges for the same signal report different harmonics and are
      both right. A measurement run is refused while Manual is selected, because every sweep has to
      choose its own ranges as it goes; set Ranging back to Auto first.
    </p>

    <h2>Where to go next</h2>
    <ul>
      <li>
        <button type="button" class="manual-link" @click="showPage('guide-welcome')">
          The simple guide
        </button>
        — the other manual, in plain language and without the conventions and measured figures. If any
        of this chapter assumed something you have not met before, start there instead; every one of
        its pages links back here.
      </li>
      <li>
        <button type="button" class="manual-link" @click="showPage('wiring-basics')">
          Connections and cables
        </button>
        — the eight BNCs, the shorting plugs, and what happens if you leave one off. Read this
        before the first measurement.
      </li>
      <li>
        <button type="button" class="manual-link" @click="showPage('charts')">Reading the charts</button>
        — cursors, zooming and the display panels, which work the same way everywhere.
      </li>
      <li>
        <button type="button" class="manual-link" @click="showPage('fft-spectrum')">FFT analysis</button>
        — the screen to learn first; everything else is built on what it shows.
      </li>
      <li>
        <button type="button" class="manual-link" @click="showPage('projects')">Projects</button>
        — project files, the Project menu and the welcome screen.
      </li>
      <li>
        <button type="button" class="manual-link" @click="showPage('saving')">
          Projects, saving and history
        </button>
        — keeping results and comparing them against each other.
      </li>
      <li>
        <button type="button" class="manual-link" @click="showPage('glossary')">Glossary</button>
        — any term on a panel, an axis or a tile you do not recognise, with the convention this
        analyzer uses for it.
      </li>
    </ul>
  </div>
</template>
