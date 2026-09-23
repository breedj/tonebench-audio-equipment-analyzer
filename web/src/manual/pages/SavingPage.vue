<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Projects, saving and history</h1>

    <p>
      Three different things can be saved, and they are easy to confuse because two of them use the
      word "save". Keeping them straight is most of what this chapter is for.
    </p>
    <table>
      <thead>
        <tr>
          <th>What</th>
          <th>Is</th>
          <th>Saved by</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Project</td>
          <td>A <code>.qa403</code> file holding a list of measurements and their results.</td>
          <td>Project ▸ Save, Save As…</td>
        </tr>
        <tr>
          <td>Measurement</td>
          <td>A named, configured instrument — settings, not readings.</td>
          <td>Save, Save as…</td>
        </tr>
        <tr>
          <td>Result</td>
          <td>One actual reading: the data on screen, with the settings that produced it.</td>
          <td>Save measurement / sweep / response result</td>
        </tr>
      </tbody>
    </table>

    <h2>Projects</h2>
    <p>
      A project is one file, opened, created and closed from the <strong>Project</strong> menu at
      the top left — its own chapter,
      <button type="button" class="manual-link" @click="showPage('projects')">Projects</button>,
      covers the menu, the welcome screen and where the files go. Two of its items belong here:
      <strong>Save</strong> (or <kbd>Ctrl</kbd>+<kbd>S</kbd>) saves every changed measurement in the
      project at once and is enabled only when something is actually unsaved, and
      <strong>Save As…</strong> writes the project to a new file — with or without its results — and
      continues there, leaving the original untouched, which is the safe way to start a variant of a
      setup you want to keep.
    </p>

    <h2>Measurements</h2>
    <p>
      The sidebar lists the measurements in the current project.
      <strong>+ New measurement</strong> picks a type and names it; the ✕ that appears on hover
      deletes one. A red dot means unsaved settings changes — on the measurement's own name in the
      toolbar, and beside it in the sidebar.
    </p>
    <p>
      <strong>Save</strong> writes the current settings into the selected measurement.
      <strong>Save as…</strong> creates a new measurement from them and lets the current one revert
      to its own last-saved settings, so experimenting with a setup never costs you the original.
      With <strong>Autosave measurement settings</strong> ticked in Configuration ▸ General — it
      is by default — Save happens by itself a moment after every change, and the red dot only ever
      flickers.
    </p>
    <p>
      Above every screen's History details sits a <strong>Notes</strong> box: free text about the
      measurement itself — the DUT, the wiring, what to look for. It is saved with the measurement
      by the same Save and Save as…, and typing in it marks the measurement unsaved like any
      setting. When a result is saved, the Save dialog starts with a copy of these notes, to keep or
      edit for that result; a report's Description prints both.
    </p>

    <h2>Results</h2>
    <p>
      The button in the toolbar is named for what the screen produces —
      <strong>Save measurement result</strong> on the FFT screen,
      <strong>Save sweep result</strong> on the two distortion sweeps,
      <strong>Save response result</strong> on the two response screens — and each asks for a name
      and notes, the notes starting as a copy of the measurement's own Notes box.
    </p>
    <p>
      Stop the measurement first: all three are disabled while one is running, because what gets
      stored is what is on screen. A cancelled sweep can be saved, and so can a response run that
      was stopped after its first pass.
    </p>
    <p>
      <strong>Clear</strong> next to it empties the display — the chart, the previews, the meters —
      without touching anything already saved and without altering the settings.
    </p>

    <h2>History</h2>
    <p>
      Every result belongs to the measurement it was taken under, and appears in two places on that
      screen.
    </p>
    <ul>
      <li>
        <strong>The History checklist</strong> in the display rail, right of the chart or under it on
        a narrow window. Tick a result to draw it on
        the chart alongside the live one, in the same units and the same window. A counter in its
        header shows how many of the plot budget are in use; ticking beyond it is disabled rather
        than silently dropping a trace. On the sweep screens each checked run also shows the dash
        pattern it is drawn with, so a legend entry can be traced back to a name.
      </li>
      <li>
        <strong>History details</strong> under the chart. One row per result with its name, notes,
        timestamp and headline figures, filterable by date, with three actions:
        <strong>Edit</strong> to rename or re-note it, <strong>CSV</strong> to download the
        measurement as text, <strong>Report</strong> for a PDF of it (below), and
        <strong>Delete</strong>, which asks for confirmation first.
      </li>
    </ul>
    <p>
      Only results of the same kind as the selected measurement can be overlaid — a spectrum and a
      sweep have no common axes to share.
    </p>

    <h2>Screenshots and reports</h2>
    <p>
      Two more exports sit in the toolbar above every chart, next to the save buttons, and a third in
      the Project menu at the top of the window.
    </p>
    <ul>
      <li>
        <strong>Screenshot</strong> makes a PNG of the chart on screen, after a short dialog. It is
        always in the light colour scheme, whatever the window and the theme are doing, and shows the
        window on screen, zoom included, with the measurement's name and a legend above the plot and
        units on the axes. Every view and projection exports — the spectrum, the distortion sweep,
        the Bode, Nyquist and Nichols responses, the impulse screen's five views, the impedance
        curve and its locus — and the history items ticked on in the checklist come along. The
        dialog's answers are remembered for the next screenshot:
        <ul>
          <li>
            <strong>Title</strong> — printed centred above the chart; empty for none.
            <strong>Save text</strong> keeps it for the next screenshot, so a caption need not be
            retyped for every chart of a session; unticked, the box starts empty next time.
          </li>
          <li>
            <strong>Add date</strong> prints the date and time at the bottom right, in the format
            chosen under Configuration ▸ General ▸ <strong>Date and time format</strong>.
          </li>
          <li>
            <strong>Image size</strong>, from Smallest (500 pixels wide) to XXX Large (2000), in steps
            of 250, and <strong>Aspect ratio</strong> 3 : 2 or 2 : 1 — the picture's size,
            title and date included, so a series of screenshots lines up in a document. The chart
            is drawn at that size rather than scaled, so a small picture has large labels relative
            to its plot and a large one fine ones.
          </li>
          <li>
            <strong>Destination</strong> — copy to the clipboard, save to a file (a download,
            named after the measurement), or both. The clipboard copy needs the browser window to
            be the active one.
          </li>
          <li>
            <strong>Colour</strong> or <strong>Black and white</strong>. Black and white is for a
            monochrome printer — every hue becomes its grey, while the line styles that tell a
            history overlay from a live curve are kept.
          </li>
        </ul>
      </li>
      <li>
        <strong>Report…</strong> makes a PDF of the measurement that is open. The dialog lists the
        measurement's stored results, and the <strong>current measurement</strong> — the one on screen,
        not saved — when there is one; tick the ones to include. Opened from the toolbar the current
        measurement and the history items ticked in the checklist start ticked, so the report shows
        the chart on screen; opened from a row's <strong>Report</strong> button in History details,
        that result does. With several measurements ticked, choose <strong>one chart with every
        measurement</strong> — the first as the main curve, the rest as history overlays, and each
        measurement's notes, settings and readings beneath it in the same order — or <strong>a chart
        per measurement</strong>, each with its own notes. A kind with several views or projections
        (the response's Bode, Nyquist and Nichols; the impulse screen's impulse, response, harmonics
        and two decays; the impedance Bode and Nyquist) has a checkbox per view under
        <strong>Charts</strong>, and every ticked view goes in; a view that draws one measurement at
        a time (the impulse, the harmonics, the decays, the impedance locus) is drawn once per
        measurement even in a combined report. Then the elements: the chart, the description (the
        notes on the result and on the measurement), the settings it ran with, and its readings —
        the Measurements panel for a response, a crosstalk or a damping-factor run; for the three
        sweeps (THD vs frequency, THD vs amplitude, IMD vs amplitude) the worst and best figure per
        channel and where they sit, then every point in the chart's own metrics table, in the unit
        the display panel is set to; for an impedance run the peak, minimum and median |Z|, the drive
        current and the reference resistor, then the component fit for whatever the
        <strong>Device under test</strong> dropdown says is on the fixture — the capacitor's C, ESR
        and ESL, the inductor's L and DCR, or the loudspeaker's Thiele-Small numbers — with the same
        caveats the readout panel shows; for an impulse response its <strong>Measurement</strong>
        block (peak, gate, what the gate resolves, the record, the reference, the records taken, the
        generator level and the removed DUT delay) and, when harmonics were extracted, each order's
        level at 100 Hz, 1 kHz and 10 kHz with the frequency it stays valid to. For an FFT the <strong>Metrics</strong> tiles chosen
        with the Metrics button, the <strong>Harmonics table</strong>, and the
        <strong>Oscilloscope</strong> and <strong>Waterfall</strong> under the spectrum. Those last
        two exist only for the measurement on screen (a stored result keeps no waveform and no
        history), and the waterfall only while the display panel shows it. Until you choose, a report
        has the current measurement ticked, every element but the settings, and a chart per
        measurement. <strong>Charts in black and white</strong> renders every chart in greys, for a
        monochrome printer; it is one setting for every kind. Views, layout and elements are
        remembered per kind, so the next report of the same kind comes out the same way; which
        results are ticked is not.
      </li>
      <li>
        <strong>Project ▸ Report…</strong> makes one PDF of the project: a title page with the
        project's notes, a contents list, then the measurements in the sidebar's order — one chapter
        per stored result, the current measurement of the screen that is open when something has
        been measured, and for a measurement that has no result at all a chapter of its
        <strong>notes and settings</strong>, so the report describes the whole project. Everything is
        ticked to begin with; untick what should stay out. Each chapter includes whatever its kind's
        Report dialog last chose — the views and the elements — or the defaults where that dialog
        has never been used. The black-and-white setting is the same one the Report dialog shows.
      </li>
    </ul>
    <p>
      The charts in a report are drawn by the same code as the charts on screen, at the report's fixed
      size and with the display panel's view and axis ranges — for a stored result, the view and
      ranges last saved with its measurement. A stored impulse response's decay surfaces are analysed
      again from its stored impulse with those saved analysis settings. The numbers in a report are
      the ones the panels show, formatted the same way.
    </p>

    <h2>A workflow that works</h2>
    <ol>
      <li>Make a project for the amplifier.</li>
      <li>
        Make the measurements you intend to repeat — "1 kHz THD at 1 W", "response into 8 Ω" — and
        save each once its settings are right.
      </li>
      <li>Run each and save a result named for the state of the DUT: "stock", "after recap".</li>
      <li>
        After a change, run the same measurements again and save results with new names. Tick both
        in the History checklist to see the difference on one chart.
      </li>
    </ol>
    <p>
      Because a result stores the settings that produced it, a comparison months later is still
      answerable: the stored parameters say exactly what was asked of the DUT.
    </p>
  </div>
</template>
