<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Impulse response</h1>

    <p>
      Plays the same exponential chirp the
      <a href="#" @click.prevent="showPage('frequency-response')">frequency response</a> uses, and
      deconvolves it into the DUT's impulse response — its output for an infinitely short input.
      From that one capture come five things: the impulse itself, a <em>gated</em> magnitude and
      phase read off it, the DUT's harmonic distortion against frequency, and two views of how it
      stops — a cumulative spectral decay and a burst decay.
    </p>

    <p class="manual-wiring-links">
      <strong>How to wire it:</strong>
      <button type="button" class="manual-link" @click="showPage('wiring-line-level')">
        a line-level DUT
      </button>
      ·
      <button type="button" class="manual-link" @click="showPage('wiring-loopback')">
        loopback
      </button>
    </p>

    <h2>Why gate an impulse response</h2>
    <p>
      Everything a DUT does arrives at a different time. Direct sound comes first; a reflection off a
      wall, a cabinet edge or a cable mismatch arrives later, as a second smaller impulse. Cutting
      the impulse response to a window around the first arrival throws the later ones away, and the
      magnitude computed from what remains is the DUT without them — an anechoic measurement made in
      an ordinary room.
    </p>

    <h3>What it costs, and the one number to read</h3>
    <p>
      A gate of <em>T</em> seconds cannot resolve anything narrower than 1/<em>T</em> hertz. A 5 ms
      gate says <strong>nothing below about 200 Hz</strong>, however smooth and convincing the curve
      looks down there. The <strong>Gate</strong> panel prints that limit as you change the window,
      and it is repeated under the gated response chart, because it is the difference between a
      measurement and a picture.
    </p>
    <p>
      So the gate is a trade, and both ends of it are real: long enough to reach the frequencies you
      care about, short enough to exclude the first reflection. For a loudspeaker, the reflection
      usually decides. The panel prints how far a reflection can travel and still fall inside the
      gate — sound covers about 34 cm per millisecond.
    </p>

    <h3>The other gate, which is not this one</h3>
    <p>
      There is a second gate you never set. Deconvolving a sweep recovers the DUT's harmonic
      distortion as separate packets sitting <em>ahead</em> of the impulse, and one fixed by the
      sweep's own geometry deletes them. Your gate is always a subset of it — the widest values
      allowed are printed beside the fields, and a run asking for more is refused rather than
      quietly clamped, because a clamped run would report settings you did not choose.
    </p>

    <h2>Reference</h2>
    <p><strong>None</strong> measures against the generated sweep. The result includes the analyzer's own converters and filters.</p>
    <p>
      <strong>Right input channel</strong> feeds the same signal back to the right input and measures
      left relative to it. The generator and the cabling divide out, and the reported delay becomes
      the DUT's own — but nothing on the analyzer's input side is cancelled.
    </p>
    <p>
      <strong>Stored loopback calibration</strong> divides out a previously captured impulse response
      of the analyzer itself. Capture one with <em>Capture new loopback…</em>: patch the generator
      output straight to the analyzer input, with the DUT out of the path. This matters most at the
      band edges, where the analyzer's anti-alias filter would otherwise read as DUT roll-off. A
      calibration is tied to the sample rate it was taken at and cannot be used at another one, and
      the app refuses to store one that does not look like a straight wire — a calibration captured
      through the DUT would divide the DUT out of every later measurement.
    </p>

    <h2>Harmonic distortion</h2>
    <p>
      <strong>Only a sweep can measure this</strong>, and the Harmonics section is not offered for
      any of the PN signals: noise excites every bin at once, so its distortion comes back spread
      evenly over the record as a raised floor rather than as packets at known places.
    </p>
    <p>
      The distortion packets the gate discards are a measurement in their own right, and reading them
      costs nothing extra — the sweep is already made. Each curve shows one harmonic order against
      the <strong>fundamental</strong> that produced it, in dB relative to the response at that same
      fundamental.
    </p>
    <p>
      Each order stops at its own frequency, which is not an omission: the stimulus has no energy
      above the sweep's top, so the k-th harmonic can only be observed for fundamentals up to that
      top divided by k. The fifth-harmonic curve therefore ends at a fifth of the span. The legend
      prints where each one stops.
    </p>

    <h2>Views</h2>
    <p>
      <strong>Impulse response</strong> draws the gated impulse on a linear time axis with the
      impulse at t = 0 — the only chart in the app whose x axis is not frequency. A reflection reads
      as a second spike; its distance from the origin is the extra path it travelled. In volts you
      can see polarity; <em>Normalised</em> switches to dB below the peak, which is easier for
      reading a decay.
    </p>
    <p>
      <strong>Gated response</strong> is the magnitude and phase computed from the window, drawn by
      the same chart the frequency response uses. Its display block offers the same
      <strong>Delay</strong> selector (None, Group delay, Phase delay) on a right-hand axis, with a
      <strong>Delay top</strong> / <strong>bottom</strong> window in µs or ms that Autofit sizes
      along with the level and phase windows. The curves are the physical delay only with
      <strong>Right channel</strong> as the reference; against the generated sweep the bulk delay
      went out with the record's arbitrary start point and what remains is the excess delay. See
      the frequency-response page for what the two delays mean.
    </p>
    <p><strong>Harmonics</strong> is the distortion curves described above.</p>
    <p>
      The display panel's <strong>Measurement</strong> block describes the data on screen rather
      than the settings in the panel, which after an edit are not the same thing: the
      <strong>Reference</strong> the run divided by, the <strong>Gate</strong> and what it
      <strong>resolves above</strong>, how many <strong>records</strong> of the requested average
      have arrived, the <strong>generator</strong> level, and — only when the reference was measured
      — the <strong>DUT delay</strong> the deconvolution removed, in samples. A report of the
      measurement prints that block, with the impulse's peak and record length beside it, and the
      harmonic orders' levels at 100 Hz, 1 kHz and 10 kHz. It deliberately quotes no ±1 dB or −3 dB
      corners: this response is gated, so below the resolution limit the curve is the window's and a
      corner read from there would not be the DUT's.
    </p>
    <p>
      Checking a saved run in <strong>History</strong> overlays it on all three curve views: the
      waveform on <strong>Impulse response</strong>, the stored gated magnitude and phase on
      <strong>Gated response</strong>, and the stored distortion curves on
      <strong>Harmonics</strong>. Overlays are drawn with a dash pattern per run — on the waveform
      and the gated response in the trace colour, and on Harmonics keeping each order's own colour,
      so H3 is the same colour in every run and the dash says which run. The waveform's overlays
      carry no min/max band; that stays with the measurement you are reading.
    </p>
    <p>
      The two <strong>decay surfaces</strong> are heatmaps and cannot show two runs at once, so they
      have one subject: the live measurement when there is one, and otherwise the first checked
      history item — which is what lets you open a saved result and look at its decay without
      re-measuring. <strong>Autofit</strong> is the one button for the chart in every other view: it
      sizes the level axis to everything drawn, overlays included — which matters on the gated
      response, where a stored amplifier run sits tens of dB above a window left at 0 dB and would
      otherwise be off the top of the plot — and it sets the horizontal window with it, to the
      measured gate on the impulse and to the band the curves cover on Harmonics.
    </p>
    <p>
      On the waveform the overlay is put on the measurement's own time columns, so checking one on
      never changes the live curve; a stored run gated <em>wider</em> than the current one keeps its
      extra span, and the live trace is simply absent there. In <em>Normalised</em> each curve is dB
      below <em>its own</em> peak, which is what lets two decays be compared without the louder run
      merely sitting above the quieter one.
    </p>
    <p>
      <strong>Cumulative spectral decay</strong> and <strong>Burst decay</strong> are the two decay
      surfaces, described below. Both are computed from the impulse response already on screen, so
      every one of their settings redraws rather than re-measures — which is why they sit on the
      Display panel and not with the sweep settings.
    </p>

    <h2>How a DUT stops</h2>
    <p>
      A frequency response says how loud each frequency comes out. It says nothing about how long it
      keeps coming out for — and a cabinet panel, a driver breakup mode or a badly damped transformer
      is a thing that rings after the signal has gone. The two decay views are two ways of showing
      that, from the same impulse response, and they answer different questions.
    </p>

    <h3>Cumulative spectral decay</h3>
    <p>
      A window is slid along the impulse response and its spectrum taken at each step. Slice 0 is the
      impulse itself; every slice behind it is what is still there that many milliseconds later. A
      resonance shows up as a ridge that outlives the band around it.
    </p>
    <p>
      <strong>0 dB is the loudest point of the whole surface</strong>, once, so the front slice still
      carries the DUT's frequency response: a resonance reads as a ridge that is both tall and slow.
    </p>
    <p>
      <strong>FFT length</strong> is the whole frequency resolution — the panel prints what it means
      in milliseconds and hertz at the current sample rate, because a length in samples means
      different things at 48 and 192 kHz. It is the one real trade here: a longer transform reaches
      lower and blurs the decay it is being used to look at over its own length. <strong>Decay
      span</strong> is how far past the impulse the surface reaches and <strong>Slices</strong> is
      how many spectra it is cut into. The span is in milliseconds rather than samples on purpose,
      so the same setting is the same measurement at every sample rate, and it is honoured to the
      sample whatever the slice count — the slices are spread across it rather than stepped by a
      fixed amount, so changing how many there are never changes how long the axis is. The panel
      prints the span you get and the average gap between slices. Ask for more decay than the record holds and the run is refused rather than
      answered — past the record there is nothing but the gate, and the gate decays perfectly.
    </p>
    <p>
      <strong>Start before peak</strong> begins the surface ahead of the impulse instead of at it, so
      the first slices show the run-up rather than the decay. Zero — the default — starts at the
      impulse. It exists for comparing with ARTA, whose time zero is its cursor; see below. The time
      axis goes <em>negative</em> rather than re-zeroing, so the impulse stays at 0 ms wherever the
      surface begins, and asking to start further back than the gate kept is refused with the figure
      it has.
    </p>
    <p>
      <strong>Analysis</strong> picks the window. The CSD window is a rectangle with a short taper
      at each end: everything from the slice's own time to the end of the block counts at full
      weight, which is what a cumulative spectrum is the transform of, and the tapers are only there
      to stop either edge being a step, whose leakage would bury the decay being looked for.
      <strong>Rise time</strong> sets how long each taper lasts. A plain Hann window is an ordinary
      short-time transform, centred on its own slice time — it reads a resonance building up as well
      as dying away, at the cost of smearing the impulse across half a window.
    </p>
    <p>
      <strong>Signal</strong> picks what is played: the sweep, or one of five periodic-noise
      sequences — ARTA's own list, and the same one the frequency-response screen offers. See that
      page's settings table for what each of them is for. The one thing to know here is that a noise
      measurement has no harmonic curves and no sweep gate: there are no distortion packets to read
      or to delete, so the whole record is kept, which costs none of the resolution gating a sweep
      costs. <strong>Chirp start</strong> is replaced by <strong>Noise cut-off</strong> for the pink
      and speech signals, and neither is shown for White PN.
    </p>
    <p>
      <strong>Comparing with ARTA.</strong> The window is ARTA's, to the letter, and so is what a
      new measurement opens on — a 5.33 ms window resolving 188 Hz over 2.46 ms of decay, which is
      what ARTA's own 256 / 2 / 60 come to off a 48 kHz sound card. The numbers in the boxes differ
      because two of ARTA's are in <em>samples</em> and a sample is four times shorter at 192 kHz:
      its shift of 2 is this screen's 2.5 ms span, and its 256-point transform is 1024 here. Type
      ARTA's own figures in at 192 kHz and you get a 1.3 ms window resolving 750 Hz — a quarter of
      the picture at the wrong end of the band.
    </p>
    <p>
      The other difference is where time zero is. Ours is the impulse peak unless you say otherwise;
      ARTA's is the cursor, which its manual has you set some way before the peak — so the flat
      stretch at the front of an ARTA surface is largely the run-up to the impulse rather than any
      decay. That is not a quirk of its drawing: the window is a flat-topped rectangle starting at
      each slice's own time, so a slice placed early still holds the whole impulse at full weight and
      reads 0 dB. Set <strong>Start before peak</strong> to the same offset and ours does the same —
      measured on the simulator's loudspeaker at ARTA's own analysis, a 2.5 ms head start holds 1 kHz
      within a decibel across all sixty slices where starting at the peak falls 12 dB over the same
      span.
    </p>

    <h3>Burst decay</h3>
    <p>
      The same decay, measured in <strong>periods of each frequency</strong> rather than in
      milliseconds. That is the entire difference, and it is not a presentation choice: a 40 Hz
      resonance and a 4 kHz one that both ring for ten cycles are the same defect, and on a
      millisecond axis one of them is 250 ms long and the other 2.5 ms. Read off a common time axis
      every low-frequency ridge looks alarming and every high-frequency one looks clean, whatever
      the DUT is actually doing.
    </p>
    <p>
      <strong>Every frequency is normalised to its own maximum</strong>, which deletes the frequency
      response from the picture on purpose. This view answers "how long", and leaving "how loud" in
      would let a bass hump bury the ringing three octaves up. It is the opposite of what the
      cumulative spectral decay does, and neither picture can be converted into the other.
    </p>
    <p>
      <strong>Time-frequency resolution</strong> is how wide the analysis filter is, and there is no
      setting that avoids the trade: a filter narrow enough to separate two close resonances rings
      for longer than either of them. Preferring frequency gives about a third of an octave —
      filter-bank resolution — and takes twice as many periods to open up, so the first cycles of
      every decay are the analysis rather than the DUT. Preferring time follows a fast decay honestly
      and merges resonances that sit close together. Either way the <em>rate</em> of decay is
      reported correctly; what changes is how much of the start is the measurement.
    </p>
    <p>
      The deep slices stop short at the low-frequency end, and that is deliberate: thirty periods of
      40 Hz is three quarters of a second, which a shorter record simply does not contain. What sits
      there instead is the zeros the gate left, and drawing them would report a perfect decay at
      exactly the frequencies least likely to have one. Measure at a finer resolution — which
      lengthens the record — to reach further down.
    </p>

    <h3>Reading either one</h3>
    <p>
      <strong>Waterfall (f)</strong> stacks the slices in a receding projection, time zero at the
      back. Each one is a solid surface, not a line: everything under a curve is filled, so a nearer
      slice hides what is behind it, which is what makes the shape readable. It shows where in the
      band something rings, and how far it falls.
    </p>
    <p>
      <strong>Waterfall (p)</strong> is the same surface read the other way round — time runs across
      the curves and the stack recedes in frequency, so every curve is one frequency's decay
      envelope. It answers the other question: not where the ringing is, but how long it lasts and
      whether it dies smoothly. A curve that stops partway has run out of record at that frequency,
      the low ones first.
    </p>
    <p>
      <strong>Sonogram</strong> draws the same surface flat, frequency across and time down, and
      shows exactly where in frequency something is.
    </p>
    <p>
      Both report the point under the cursor — frequency, time and level — with a ring marking it. In
      the waterfall the whole solid reports, not just the curves: point anywhere inside the body and
      you get the level of the slice that owns that pixel, which is the nearest one drawn over it.
    </p>
    <p>
      The colour key runs up the left-hand edge of the waterfall's box, and it is the box's own dB
      axis — a level is drawn at the height its number is printed at, so a colour on the surface can
      be carried across to a figure by eye. The back wall is ruled at the same steps, which is what
      the empty part of it above the surface is for. Time runs down the right-hand edge, in
      milliseconds for a cumulative spectral decay and in periods for a burst decay. In
      <strong>Waterfall (p)</strong> the two swap over: time is along the curves and the receding
      axis is frequency. All three axes are titled with their unit, whichever way round they are.
      While a surface is being computed an <em>Analysing…</em> box sits in the middle of the chart,
      over the previous surface if there is one.
    </p>
    <p>
      The band running down the right-hand edge is the cut face of the solid, not decoration. Each
      slice is drawn a little further right than the one behind it, so a strip of every one of them
      is never covered; coloured by level, those strips read as a ramp down through the palette and
      are the quickest way to see how far the whole surface has fallen by the time it reaches the
      front. <strong>Coloured curves</strong> turns it off along with the curve colouring, and a
      <strong>Filled surface</strong> always draws it.
    </p>
    <p>
      <strong>Filled surface</strong> paints the strip between each curve and the one behind it from
      the palette, so the stack reads as a lit solid rather than as a set of lines with the page
      showing between them; the curves themselves are then drawn in white, the level being carried
      by the fill. It is the clearer picture of a ridge and its surroundings, where the unfilled
      stack is the clearer picture of one curve's shape — which is why both are offered rather than
      one replacing the other.
    </p>
    <p>
      <strong>Rotation</strong> and <strong>Tilt</strong> are the projection. Rotation is which way
      the stack recedes, in degrees from straight down: positive leans it to the left, as ARTA draws
      it, zero stacks the curves straight down the chart, and a negative angle sends them to the
      right. Tilt is how far the view leans back. On this projection that is how much of the chart's
      height the depth axis takes, so a deep-looking surface is one with a squashed dB scale — the
      two share the box, and neither can be had for nothing.
    </p>
    <p>
      On the flat projection both can also be <strong>dragged on the chart itself</strong>: hold
      the left button on the surface and move the pointer, sideways to lean the stack and up or
      down to set its depth. The
      fields follow the drag, so what you set by hand and what you set by dragging are the same two
      numbers &mdash; which is also why a screenshot or a report drawn afterwards shows exactly the
      view you left on screen. The stack is drawn at a coarser slice spacing while the button is
      down and fills back in when it is released, and the readout is suspended for the length of the
      drag, since what is under the pointer is moving with it.
    </p>
    <h3>The 3D view</h3>
    <p>
      Where the browser can drive a GPU, the two waterfalls are drawn as a <strong>lit 3D
      surface</strong> that can be rotated and zoomed freely: drag to turn it, and use the wheel to
      move the camera in and out. It is the same measurement, the same palette and the same dB
      range as the flat projection &mdash; only the drawing differs.
    </p>
    <p>
      This needs <strong>WebGPU</strong>, which Chrome and Edge 113 and later have on Windows and
      macOS, and Firefox 141 and later has on Windows. On Linux it depends on the browser, the GPU
      and the driver. Where it is not available the decay charts simply use the flat projection
      described above, with no loss of measurement and no message &mdash; nothing needs setting
      either way.
    </p>
    <p>
      It can also be turned off by hand: <strong>Configuration &rarr; General &rarr; Decay
      surfaces</strong> chooses between the 3D surface where the browser allows it and the flat
      projection always. The choice is kept for this browser rather than with the bench's settings.
    </p>
    <p>
      <strong>Rotation</strong> and <strong>Tilt</strong> set the 3D view, and from the first drag
      the chart's own camera takes over: it turns through a whole circle, which those two fields
      cannot describe, so they stop following it rather than springing the surface back part way
      through a turn. Changing either field puts the view where it says again. A screenshot taken
      after a drag is the picture on screen, not the one the fields describe.
    </p>
    <p>
      Two more differences. <strong>Sonogram</strong> is always drawn flat, because it is a colour
      map and there is no surface to turn. And <strong>Filled</strong>, <strong>Coloured
      curves</strong> and <strong>Thick lines</strong> describe how a stack of curves is drawn, so
      they have no effect on the 3D surface, which is always a lit solid.
    </p>
    <p>
      <strong>Points per period</strong> is how finely the burst decay is sampled in time, and so how
      many curves the waterfall has to draw. Eight is the default, and at any ordinary chart size every
      one of them is drawn — what separates adjacent curves is the level between them, not the depth
      offset, so a dense stack stays legible.
    </p>
    <p>
      Where a slice's curve begins part-way across, the analysis has no answer for the frequencies to
      its left: a cumulative spectral decay cannot resolve below what its transform length allows,
      and a burst decay runs out of record at the low end first, sooner the deeper the slice. The
      staircase that leaves along the left of the surface is that limit receding, not a measurement.
    </p>
    <p>
      The two views do not resolve frequency the same way, which is worth knowing before comparing
      them. A burst decay runs <em>its own</em> wavelet at every frequency the chart draws, so it has
      a measurement per column however wide the band is. A cumulative spectral decay has one
      transform per slice, and its bins are the sample rate over the
      <strong>FFT length</strong> — 188 Hz at 1024 samples and 192 kHz, which the caption under that
      control states. Over a 20 Hz – 1 kHz band that is five bins, so below a few hundred hertz the
      curve is a straight line drawn between measured points rather than detail. Lengthening the
      transform is the only thing that buys resolution there, and it costs time: the window has to
      fit inside the gated impulse response.
    </p>
    <p>
      <strong>Range</strong> is how far below 0 dB the chart reaches — 30 dB is a good place to
      start. <strong>Coloured curves</strong> colours each curve by level; off draws them in one
      colour, which reads the shape rather than the level. The <strong>From</strong> and
      <strong>To</strong> frequencies are shared with the Harmonics view.
    </p>
    <p>
      Neither view uses the display gate. A gate tapers the impulse response to nothing over a few
      milliseconds, and a decay analysis run on that would measure the window — decaying on schedule,
      at every frequency at once, however good or bad the DUT is.
    </p>

    <h2>Settings that behave as they do elsewhere</h2>
    <p>
      The drive level, the frequency span, the chirp start and the averaging work exactly as on the
      <a href="#" @click.prevent="showPage('frequency-response')">frequency response</a> screen, and
      that page describes them. One thing behaves differently here: the resolution setting also sets
      the sweep's length, and a longer sweep pushes the distortion packets further apart — which is
      what allows a wider gate.
    </p>
  </div>
</template>
