<template>
  <div>
    <h1>Importing a simulation</h1>

    <p>
      A simulated spectrum can be drawn on the FFT chart beside a measured one, so a change in the
      circuit can be checked against the bench without leaving the app. The import reads a
      <strong>transient</strong> run — LTspice's <code>.raw</code> or QSpice's <code>.qraw</code>,
      which are the same container — takes the traces you choose, and analyses them with the same
      code the live FFT screen uses. Its THD, THD+N, SNR and harmonic figures therefore mean
      exactly what a measured capture's do.
    </p>

    <p>
      Imported runs appear in the <strong>History</strong> list under their own
      <strong>Simulations</strong> heading, and are checked on and off the chart like any saved
      result. They are stored inside the project file, so they travel with it and can be renamed,
      annotated, exported to CSV, put in a report and deleted the same way.
    </p>

    <h2>The file is a scratch file, and both simulators can delete it</h2>

    <p>
      A <code>.raw</code> or <code>.qraw</code> is working data, not a document. The next run of the
      same schematic overwrites it, and <strong>both simulators have a setting that deletes it by
      itself</strong>:
    </p>

    <table class="manual-wrap">
      <thead>
        <tr>
          <th>Simulator</th>
          <th>Setting</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>LTspice</td>
          <td>
            <strong>Automatically delete .raw files</strong>, in
            Tools&nbsp;&rsaquo;&nbsp;Control&nbsp;Panel&nbsp;&rsaquo;&nbsp;Operation.
          </td>
        </tr>
        <tr>
          <td>QSpice</td>
          <td><strong>Auto delete simulation data files</strong>, in its settings.</td>
        </tr>
      </tbody>
    </table>

    <p>
      With either of those ticked the file exists only <em>while the simulator still has the run
      open</em>. Close the simulator after simulating and the file goes with it — so import it
      first, with the simulator left open, and only then close it. If the file browser shows an
      empty folder where you expected a <code>.raw</code>, that setting is the usual reason: run the
      simulation again and leave the window open.
    </p>

    <p>
      You can also simply untick it, which is worth doing on a schematic you are iterating on. The
      cost is disk: these files are large, because they hold every node of every timestep. The run
      used as the example throughout this page is <strong>107&nbsp;MB</strong> for 95&nbsp;ms of a
      push-pull amplifier.
    </p>

    <p>
      None of this affects a simulation you have already imported. Tonebench stores the samples of
      the traces you chose <em>inside the project file</em>, precisely because the source cannot be
      relied on to still be there — see
      <strong>Changing the settings afterwards</strong> below.
    </p>

    <h2>What the simulator's directives do to the spectrum</h2>

    <p>
      Three things about a transient run reach the measurement, and it is worth knowing which is
      which, because only one of them can be fixed afterwards.
    </p>

    <table class="manual-wrap">
      <thead>
        <tr>
          <th>What you set</th>
          <th>What it decides</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>The <strong>maximum timestep</strong> (<code>.tran</code>'s fourth number)</td>
          <td>
            The simulator's effective sample rate, and therefore the highest frequency the run can
            represent at all. 1&nbsp;µs is a 1&nbsp;MHz grid — far more than audio needs, and a
            good default. The import refuses a run whose <em>coarsest</em> step cannot carry the
            top of your measurement band, because interpolating such a run is inventing samples.
          </td>
        </tr>
        <tr>
          <td>The <strong>saved length</strong> (<code>stop − start</code>)</td>
          <td>
            The resolution: bin width ≈ 1 ÷ saved seconds. This is the one that cannot be fixed
            later by any setting in this app — see below.
          </td>
        </tr>
        <tr>
          <td>The <strong>start-save time</strong> (<code>.tran</code>'s third number)</td>
          <td>
            How much of the startup settling is thrown away before anything is recorded. You can
            also skip further in with this dialog's <strong>Analyse&nbsp;from</strong>, so this one
            is recoverable — but only downwards, and only within what was saved.
          </td>
        </tr>
      </tbody>
    </table>

    <h3>LTspice</h3>

    <p>
      Two directives and one <code>.tran</code> line decide whether a run is usable. The first is not
      optional.
    </p>

    <table>
      <thead>
        <tr>
          <th>Directive</th>
          <th>Why</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td><code>.options plotwinsize=0</code></td>
          <td>
            <strong>Required.</strong> Switches off LTspice's waveform compression, which otherwise
            discards most of the samples. Measured on a real push-pull EL84 amplifier: with
            compression on, 5.7&nbsp;% of the points survive and the same circuit reads
            <strong>THD 0.63&nbsp;%</strong> against a true <strong>0.38&nbsp;%</strong>, with the
            third harmonic 4.2&nbsp;dB wrong. It does not look broken — it looks like a worse
            amplifier. The import refuses such a file and says so rather than reporting the figure.
          </td>
        </tr>
        <tr>
          <td><code>.tran 0 &lt;stop&gt; &lt;start&gt; &lt;maxstep&gt;</code></td>
          <td>
            For example <code>.tran 0 100m 5m 1u</code>. <em>maxstep</em> sets the simulator's
            effective sample rate (1&nbsp;µs is a 1&nbsp;MHz grid, far more than enough for audio);
            <em>start</em> is when LTspice begins saving, which should be past the circuit's startup
            settling; and <em>stop&nbsp;−&nbsp;start</em> is the saved length, which is what fixes
            your resolution. See below.
          </td>
        </tr>
        <tr>
          <td><code>.options numdgt=7</code></td>
          <td>
            <strong>Not needed.</strong> It writes double-precision data, doubling the file, and
            measured identically: the samples are stored at single precision either way, and the
            arithmetic floor of the analysis sits around −182&nbsp;dBc, far below it. LTspice's own
            single-precision values are bit-identical to what is stored.
          </td>
        </tr>
      </tbody>
    </table>

    <h3>QSpice</h3>

    <p>
      QSpice writes the same container, so a <code>.qraw</code> imports exactly as a
      <code>.raw</code> does — both the native file and one exported in LTspice format. There is no
      compression option to worry about; the one thing to get right is the timestep, because
      <code>.tran&nbsp;100m</code> on its own lets QSpice choose, and it chooses very freely. A real
      QSpice run of its own AudioAmp example steps between <strong>6&nbsp;ps and 42&nbsp;µs</strong>
      — seven orders of magnitude — and that 42&nbsp;µs worst case carries only 11.8&nbsp;kHz, so
      the import refuses it. Give the transient a maximum step and it behaves like any other:
    </p>

    <p><code>.tran 0 100m 0 1u</code></p>

    <p>
      Read as: run to 100&nbsp;ms, start saving at 0, step no coarser than 1&nbsp;µs. A run whose
      steps still vary widely is imported anyway, resampled at a bounded rate, with a note saying so.
    </p>

    <h3>Is <code>.four</code> worth adding?</h3>

    <p>
      <strong>Not for this import — it reads nothing from it — but it is worth adding as a
      cross-check.</strong> <code>.four</code> makes the simulator write its own Fourier table to
      the log file, which is an entirely independent calculation of the same thing. Comparing it
      against what Tonebench reports is the best confidence check there is, and it is how this
      feature was verified: on a push-pull EL84 run, LTspice's <code>.four</code> gave
      <strong>THD 0.3796&nbsp;%</strong> with H2 at −59.98 and H3 at −48.73&nbsp;dBc, and Tonebench
      read <strong>0.3792&nbsp;%</strong>, −60.16 and −48.72 — agreement to a hundredth of a decibel
      on the dominant harmonic.
    </p>

    <p>
      It is not imported as data because it would be a <em>worse</em> number sitting next to a
      better one. LTspice's <code>.four</code> reports <code>N-Period=1</code>: it analyses a single
      cycle, the last one in the run. Tonebench analyses the whole saved length — ninety-five cycles
      in that example — so its figure is the better estimate of a steady state, and the small
      disagreement above is mostly that one-cycle window. Two differently-defined THD figures on one
      screen invites the question "which is right?", and the honest answer would be a paragraph.
    </p>

    <h2>Resolution comes from the length of the run</h2>

    <p>
      This is the one thing about the measurement worth understanding before using it. The analysis
      rate is derived so that the record spans the whole saved run, which means the bin width is
      always
    </p>

    <p><strong>bin width ≈ 1 ÷ saved seconds</strong></p>

    <p>
      and <em>the FFT size cannot change it</em>. A larger FFT raises the analysis rate and computes
      more empty spectrum above the audio band; it buys no resolution at all. The dialog suggests
      the smallest size whose range clears the measurement band, and that is the one to use.
    </p>

    <table>
      <thead>
        <tr>
          <th>Saved length</th>
          <th>Bin width</th>
        </tr>
      </thead>
      <tbody>
        <tr><td>95 ms (<code>.tran 0 100m 5m</code>)</td><td>10.5 Hz</td></tr>
        <tr><td>200 ms</td><td>5.0 Hz</td></tr>
        <tr><td>341 ms</td><td>2.93 Hz — the same as the analyzer at 192 kHz / 65536 points</td></tr>
      </tbody>
    </table>

    <p>
      So to lay a simulation over a measurement at matching resolution, simulate for about a third
      of a second. Nothing else gets you there.
    </p>

    <h2>The controls</h2>

    <table class="manual-wrap">
      <thead>
        <tr>
          <th>Control</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Left channel / Right channel</td>
          <td>
            Which trace becomes which channel. A tube-amp run carries dozens — the EL84 example has
            41 node voltages and 99 branch currents — so they are chosen here rather than in
            LTspice. A second trace is useful for a stage's input beside its output.
          </td>
        </tr>
        <tr>
          <td>Analyse from</td>
          <td>
            Where the analysed record starts, in absolute simulation time, i.e. the number LTspice
            shows. A valve amplifier's coupling capacitors and bias take a long time to settle, and
            a record straddling that measures the settling rather than the circuit.
          </td>
        </tr>
        <tr>
          <td>Level offset</td>
          <td>
            A dB offset applied to the samples, for when the bench sees the node through a divider
            or a pad. Applied at analysis time, so it can be corrected afterwards without
            re-importing.
          </td>
        </tr>
        <tr>
          <td>FFT size</td>
          <td>
            How far above the band the spectrum is computed — <em>not</em> the resolution. Sizes
            larger than the run can support are disabled.
          </td>
        </tr>
        <tr>
          <td>Window</td>
          <td>The analysis window, as on the FFT screen.</td>
        </tr>
        <tr>
          <td>Measurement band</td>
          <td>The band the THD and noise figures integrate over, as on the FFT screen.</td>
        </tr>
        <tr>
          <td>Lock the record to whole cycles</td>
          <td>
            Shortens the record by up to one cycle of the detected tone so it holds a whole number
            of them. The record is then exactly periodic in the stimulus and every harmonic lands
            on a bin centre instead of leaking across its neighbours — the same trick the chirp
            frequency-response measurement rests on. Leave it on unless the stimulus is noise or a
            multi-tone, where there is no single tone to lock to.
          </td>
        </tr>
      </tbody>
    </table>

    <h2>Changing the settings afterwards</h2>

    <p>
      The imported samples themselves are stored, not just the spectrum, so almost everything on
      that form can be changed later and the spectrum rebuilt — with no need for the original file.
      That matters more than it sounds: an LTspice <code>.raw</code> is a scratch file that the next
      run overwrites and that LTspice deletes without warning, so a stored path could not be relied
      on.
    </p>

    <p>
      In <strong>History details</strong>, an imported simulation's row carries a
      <strong>Re-analyse</strong> button beside Edit and CSV. It opens the same settings, prefilled
      with what that result was analysed at, and recomputes its spectrum, its distortion figures and
      its stored parameters in place — same result, same name, same notes, same position in the
      Simulations group. If it is ticked on the chart, the trace is redrawn as soon as it is done.
    </p>

    <table class="manual-wrap">
      <thead>
        <tr>
          <th>Afterwards you can change</th>
          <th>And you cannot change</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>The window, the FFT size, the level offset, the measurement band, whole-cycle locking</td>
          <td>
            <strong>Which traces were imported.</strong> The other columns of the file were
            discarded, so a different node means importing the file again.
          </td>
        </tr>
        <tr>
          <td>
            <strong>Analyse from</strong>, forwards — moving it later skips further into the stored
            samples, which is how to cut out settling you did not cut out at import.
          </td>
          <td>
            <strong>Analyse from, backwards</strong> past the figure the import used. The samples
            begin there; anything earlier in the run was never stored. Asking for it says so rather
            than quietly analysing from where they do begin.
          </td>
        </tr>
        <tr>
          <td>Nothing about the resolution</td>
          <td>
            <strong>The bin width.</strong> It is 1 ÷ the analysed length, so only a longer
            simulation improves it — and that is a fresh run and a fresh import. Note the corollary:
            moving <em>Analyse from</em> later shortens the record, so it makes the bins
            <em>coarser</em>.
          </td>
        </tr>
      </tbody>
    </table>

    <p>
      A re-analysis rewrites the result rather than adding one, which is deliberate: the settings
      are an opinion about one simulation run, not a second measurement of it. To keep both, import
      the file twice and name them apart.
    </p>

    <h2>Two things the import does for you</h2>

    <p>
      <strong>It removes the node's DC operating point.</strong> A plate node sits at a few hundred
      volts with a signal of a few volts on it. Left in, that DC leaks out of any analysis window
      far enough to swamp the bottom of the measurement band — the same capture reads THD+N
      8373&nbsp;% and SNR −38.5&nbsp;dB, which is arithmetically correct and describes nothing worth
      knowing. The removed voltage is recorded with the result, so the operating point is reported
      rather than lost.
    </p>

    <p>
      <strong>It filters before it resamples.</strong> A simulator runs at a far higher rate than
      the analysis needs, and simply thinning the samples would fold everything above the new
      Nyquist back into the audio band, where it would read as distortion the circuit does not have.
      Measured on a test signal, a component at 200&nbsp;kHz reappears at 27.5&nbsp;kHz at nearly
      full amplitude without the filter, and is 129&nbsp;dB down with it.
    </p>

    <h2>What is refused, and why</h2>

    <table class="manual-wrap">
      <thead>
        <tr>
          <th>File</th>
          <th>What happens</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Too coarsely stepped for the band</td>
          <td>
            Refused, naming <code>.options plotwinsize=0</code> (LTspice) or a maximum timestep
            (QSpice). See above for what it costs.
          </td>
        </tr>
        <tr>
          <td>An <code>.op.raw</code></td>
          <td>
            Refused: that is the operating-point file LTspice writes beside every run, and it holds
            one solution rather than a waveform.
          </td>
        </tr>
        <tr>
          <td>An <code>.ac</code> run</td>
          <td>
            Refused — it is a frequency response rather than a waveform. Importing one as a Bode
            plot would be a useful thing to add, and is not built yet.
          </td>
        </tr>
        <tr>
          <td>LTspice's own FFT export</td>
          <td>
            Refused: it is already a spectrum, windowed by LTspice. Import the transient run itself
            so the window and the resolution are this app's to choose.
          </td>
        </tr>
        <tr>
          <td>A <code>.step</code>ped run</td>
          <td>
            Refused: several sweeps sit in one file with the time axis restarting, and the import
            cannot separate them. Run a single simulation instead.
          </td>
        </tr>
      </tbody>
    </table>

    <h2>A current trace</h2>

    <p>
      Branch currents can be imported and are measured correctly, but they are in amps: their levels
      are dB relative to 1&nbsp;A, while the chart's axis and the noise-density figures are written
      for volts. Read the shape and the relative figures, not the absolute levels. The dialog says
      so when a current trace is chosen.
    </p>
  </div>
</template>
