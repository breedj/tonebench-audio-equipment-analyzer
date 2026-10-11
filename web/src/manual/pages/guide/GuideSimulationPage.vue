<template>
  <div>
    <h1>Comparing with a simulation</h1>

    <p>
      If you designed the circuit in LTspice or QSpice, you can put the simulated result on the
      same chart as the measured one. It is the quickest way to find out whether the thing on the bench is
      behaving like the thing you drew — and when it is not, the difference usually points straight
      at what to look at.
    </p>

    <h2>Before you simulate</h2>

    <p>
      Add this line to the schematic, as a SPICE directive. Without it LTspice throws most of the
      waveform away to save space, and the distortion will read far worse than it really is:
    </p>

    <p><code>.options plotwinsize=0</code></p>

    <p>
      Then set up the run itself. Something like:
    </p>

    <p><code>.tran 0 100m 5m 1u</code></p>

    <p>
      Read that as: simulate to 100&nbsp;ms, start saving at 5&nbsp;ms, and take a step no longer
      than 1&nbsp;µs. The 5&nbsp;ms gives the circuit a moment to settle before anything is
      recorded, which matters a lot with valves — their coupling capacitors take time to charge, and
      you do not want that in the measurement.
    </p>

    <h2>How long to simulate</h2>

    <p>
      The length of the saved run decides how fine the detail is. There is no setting in Tonebench
      that improves it; only a longer simulation does.
    </p>

    <table class="manual-wrap">
      <thead>
        <tr>
          <th>If you save</th>
          <th>You get detail of about</th>
        </tr>
      </thead>
      <tbody>
        <tr><td>95 ms</td><td>10 Hz — fine for distortion at a single tone</td></tr>
        <tr><td>a third of a second</td><td>3 Hz — the same as the analyzer itself</td></tr>
      </tbody>
    </table>

    <p>
      Short runs are quicker and perfectly good for looking at harmonics. Save longer when you want
      the simulated noise floor to line up with the measured one.
    </p>

    <h2>If you use QSpice</h2>

    <p>
      QSpice files work the same way — pick the <code>.qraw</code>. The one thing to change is the
      transient line, because <code>.tran 100m</code> on its own lets QSpice pick its own step sizes
      and they end up far too uneven to measure from. Write it out in full instead:
    </p>

    <p><code>.tran 0 100m 0 1u</code></p>

    <p>There is no compression setting to worry about in QSpice.</p>

    <h2>Import it before you close the simulator</h2>

    <p>
      Both simulators can throw the result file away on their own — it is
      <strong>Automatically delete .raw files</strong> in LTspice
      (Tools&nbsp;&rsaquo;&nbsp;Control&nbsp;Panel&nbsp;&rsaquo;&nbsp;Operation) and
      <strong>Auto delete simulation data files</strong> in QSpice. If either is ticked, the file
      only exists while the simulator still has the run open, so do the import first and close the
      simulator afterwards.
    </p>

    <p>
      If you would rather not think about it, untick the setting. The files are big — a tenth of a
      second of an amplifier runs to about a hundred megabytes — but once a simulation is imported
      Tonebench keeps its own copy of the traces you picked, so the original is not needed again.
    </p>

    <h2>Importing it</h2>

    <ol>
      <li>Open the FFT measurement you want to compare against.</li>
      <li>In the <strong>History</strong> panel, press <strong>Import simulation…</strong>.</li>
      <li>Find the <code>.raw</code> (or <code>.qraw</code>) file the simulator wrote next to your schematic.</li>
      <li>
        Choose which trace is the one you care about — usually the output node. A simulation carries
        every node in the circuit, so there will be a long list.
      </li>
      <li>Press <strong>Import</strong>.</li>
    </ol>

    <p>
      It then appears in the History list under <strong>Simulations</strong>, with a small
      <strong>sim</strong> label so you can never mistake it for something you measured. Tick its
      box and it is drawn on the chart in its own colour, over the live trace.
    </p>

    <h2>Changing your mind later</h2>

    <p>
      You do not have to get the settings right first time, and you do not need the file again.
      Tonebench keeps the simulated waveform itself, so in <strong>History details</strong> the
      row's <strong>Re-analyse</strong> button lets you change the window, the FFT size, the level
      offset, the band, or where in the run the measurement starts — and redraws it.
    </p>

    <p>
      Two things it cannot do. It cannot show you a <em>different</em> trace, because only the ones
      you picked were kept; import the file again for that. And it cannot start the measurement
      <em>earlier</em> than you originally chose, for the same reason — that part of the run was
      never kept.
    </p>

    <h2>The quick one: where does it roll off?</h2>

    <p>
      Everything above is about distortion, which needs a long simulation. There is a second, much
      faster thing worth doing: an <strong>AC sweep</strong>, which tells you where the amplifier
      rolls off at each end and what its phase is doing. It runs in a second or two.
    </p>

    <p>Put this in the schematic instead of the <code>.tran</code> line:</p>

    <p><code>.ac dec 100 1 1Meg</code></p>

    <p>
      Then open a <strong>Frequency response</strong> measurement rather than an FFT one and import
      it there. Pick the output node, and — if the schematic has one — the input node as the
      reference, which makes the answer the amplifier's own gain. You get the same −3 dB corners the
      bench measurement gives you, to put side by side.
    </p>

    <p>
      One thing it will not tell you: an AC sweep assumes the amplifier behaves itself. It says
      nothing about distortion or clipping, however hard you imagine driving it. That is what the
      transient run above is for.
    </p>

    <h2>Reading the comparison</h2>

    <p>
      Expect the simulation to look <em>better</em> than the bench, usually by a good margin. That
      is not a fault in either one. A simulation has no hum, no mains harmonics, no stray coupling,
      perfect components and a transformer model that is kinder than the real part. What is worth
      comparing is the shape:
    </p>

    <ul>
      <li>
        <strong>Which harmonics dominate.</strong> If the simulation says the third harmonic is the
        big one and the bench says the second, something is not symmetrical that should be — valve
        matching, or the bias.
      </li>
      <li>
        <strong>How distortion changes with level.</strong> A real amplifier that tracks the
        simulation at low level and departs at high level is telling you where it runs out of
        headroom.
      </li>
      <li>
        <strong>The noise floor.</strong> Only ever worse on the bench. How much worse is a measure
        of hum, layout and grounding, none of which a simulation knows about.
      </li>
    </ul>

    <p>
      Do not chase the absolute distortion figure. Chase the differences in shape — those are the
      ones that mean something is wrong, or that a change you made did what you hoped.
    </p>

    <h2>If it will not import</h2>

    <p>
      Tonebench will tell you why rather than quietly giving you a bad answer. The two common ones:
      you left out <code>.options plotwinsize=0</code> (or, in QSpice, a maximum timestep), or you
      picked the <code>.op.raw</code> / <code>.qopraw</code> file — that is a different file LTspice writes beside the real one, and
      it holds no waveform. Pick the plain <code>.raw</code>.
    </p>
  </div>
</template>
