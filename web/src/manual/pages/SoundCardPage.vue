<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Using a sound card</h1>

    <p>
      A sound card can be the analyzer instead of a QA403. It is a <em>calibrated</em> instrument,
      not a fallback: once both calibration steps below have run, every level it reports is a
      voltage, and every measurement in the app works against it. The audience is anyone who has
      built an analog front-end — a buffer, a divider, an attenuator board — around a card.
    </p>

    <p class="manual-callout">
      <strong>Before it can measure anything, it needs calibrating.</strong> Jump straight to
      <a href="#calibration">Calibrating the card</a> if the card is already chosen.
    </p>

    <h2>Choosing it</h2>
    <p>
      The <strong>Analyzer</strong> dropdown in the status bar offers <strong>Sound card</strong>
      beside the QA403 and the simulator, live, with no restart. Selecting it puts a
      <strong>cogwheel</strong> next to the dropdown, which opens the sound-card settings — that
      button is where everything on this page happens, and it is there before a card has been
      chosen, because choosing one is the first thing to do.
    </p>

    <h3>What replaces the attenuator controls</h3>
    <p>
      With a sound card selected the <strong>Ranging</strong>, <strong>Attenuation</strong> and
      <strong>Output range</strong> controls disappear. They have nothing to offer: a card has one
      fixed full scale on each side, where a QA403 has an eight-step input ladder and a four-step
      output one. What remains is the <strong>Ext. gain</strong> badge, which every analyzer has —
      see <button type="button" class="manual-link" @click="showPage('getting-started')">Getting
      started</button> for the control itself.
    </p>
    <p>
      On this bench that badge is the only thing describing the signal path, and it matters more
      here than on a QA403: a card has no input attenuator to save a measurement, so the front-end
      you built is the whole of your headroom. A 10:1 divider is <strong>−20 dB</strong>, or
      <strong>×0.1</strong>.
    </p>
    <p class="manual-callout">
      <strong>The card's figures are its own.</strong> They are remembered separately from the
      QA403's, because they are different benches — your card's front-end is not in front of the
      QA403's BNCs — and switching analyzers swaps which pair applies. The panel names the bench it
      is writing to, so there is no doubt which one a number landed on.
    </p>

    <h2>Picking the card</h2>
    <p>
      The <strong>Sound card</strong> dropdown comes first, and it lists <em>cards</em> rather than
      devices: one entry per physical interface per <em>host API</em> — the driver family the card is
      reached through. The order is deliberate: the ones that reach the hardware come first, the ones
      that resample last.
    </p>
    <p>
      Choosing a card is what fills the <strong>Input</strong> and <strong>Output</strong> lists, and
      they only ever offer that card's own devices — which is how both directions are guaranteed to
      be one piece of hardware, and therefore <a href="#duplex">one clock</a>. Where the card has
      only one device on a side, as an ASIO card does, that list is not shown at all: there is
      nothing to choose.
    </p>
    <table>
      <thead>
        <tr>
          <th>Host API</th>
          <th>Use it?</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td><strong>ASIO</strong></td>
          <td>
            Yes, and on Windows effectively always. It is the only one that presents a card as a
            single full-duplex device, which is what a measurement needs — see
            <a href="#duplex">why that matters</a>. It also gives the card's true sample rates and
            the lowest latency. Needs the manufacturer's ASIO driver installed.
          </td>
        </tr>
        <tr>
          <td><strong>WDM-KS</strong></td>
          <td>
            Kernel streaming: unmixed and honest, but it does not always allow a duplex stream, and
            its device names carry no interface name at all, so the app cannot tell which input
            belongs with which output.
          </td>
        </tr>
        <tr>
          <td><strong>WASAPI</strong></td>
          <td>
            Only in exclusive mode, which the app always requests. In shared mode Windows pins the
            card to the mixer's rate and resamples silently — measured on one interface, shared mode
            offered only 48 kHz while exclusive gave 44.1, 48, 96 and 192 kHz.
          </td>
        </tr>
        <tr>
          <td><strong>DirectSound</strong>, <strong>MME</strong></td>
          <td>
            No. Both accept any rate by resampling in software, so the rate they report is not the
            rate the converter ran at. The dialog warns if you pick one.
          </td>
        </tr>
      </tbody>
    </table>

    <h3 id="duplex">Why input and output must be one interface</h3>
    <p>
      The analyzer plays and records at the same time, and the two must run on <em>one clock</em>.
      Two separate cards run on two, and the drift between them is not reported as a fault — it is
      reported as DUT response. The frequency-response measurement is especially exposed, because it
      rests on the played sweep being exactly one period of the analysis record; a drifting clock
      breaks that silently.
    </p>
    <p>
      Under ASIO the whole card is one device and the question does not arise. Under the other host
      APIs Windows splits a card into a separate input device and output device, so you pick both —
      and the dialog warns when the two do not look like the same interface. That warning is
      advisory rather than a refusal, because WDM-KS names genuinely cannot be matched up.
    </p>

    <h3>Channel pairs</h3>
    <p>
      A card with more than two channels on a side offers a <strong>channel pair</strong> dropdown:
      1/2, 3/4, and so on. Under ASIO this selects within the one device. Under WASAPI or WDM-KS the
      pairs are usually separate <em>devices</em> instead, so you pick them in the device lists.
    </p>

    <h3>Sample rates</h3>
    <p>
      There is nothing to set. Every time the choice of card, device or channel pair changes, the
      app probes the hardware — in the mode it actually opens it in — for the three rates it
      measures at, <strong>48, 96 and 192 kHz</strong>, and keeps whichever of them the card
      accepts.
    </p>
    <p>
      <strong>The rate a measurement runs at is the measurement's own setting</strong>, on its
      screen, exactly as it is for the QA403. What the probe does is limit what those screens
      offer: a card that cannot do 192 kHz does not offer it, and the frequency-span dropdowns drop
      the spans that would need it — a 96 kHz span needs a 192 kHz rate, because the sweep is
      analysed to Nyquist. <strong>So the rate dropdown on a measurement screen is where you see
      what the card accepted</strong>; the dialog has no list of its own.
    </p>
    <p>
      A card that accepts <em>none</em> of the three is not half-usable, so the dialog says so in
      red and the selection cannot be applied. That is usually a mismatched input and output rather
      than a card that genuinely cannot do any of them.
    </p>

    <h3>Applying the choice</h3>
    <p>
      The device controls take effect as they are changed — there is no Apply button, because every
      one of them is a statement about which hardware is in front of you rather than something to
      confirm. A line underneath says when the card is open and ready. Changing the card reopens the
      device, which is why the ranges and the calibration readings refresh with it.
    </p>

    <h3>Control panel</h3>
    <p>
      Beside the card dropdown, for an <strong>ASIO</strong> card only, is a
      <strong>Control panel</strong> button. It raises the driver's own settings — buffer size,
      clock source and often the sample rate — none of which this app can set and some of which it
      has to live with.
    </p>
    <p>
      It is <strong>refused while a measurement is running</strong>, and that is not politeness:
      the panel is a modal dialog owned by the driver, and it holds the one thread every audio call
      here is made on, which is the thread the transfer loop runs on. Raising it mid-stream would
      stop the transfers for as long as it stayed open.
    </p>
    <p class="manual-callout">
      <strong>Not every ASIO driver has one.</strong> Measured on a MOTU M4 (2026-09-30): the call
      returns success immediately and nothing appears, because that driver keeps its settings in a
      separate application instead. There is no way to tell that apart from a panel that opened, so
      a button that seems to do nothing means your driver is one of those — look for the
      manufacturer's own control application.
    </p>

    <h3>Settle after opening</h3>
    <p>
      A card <strong>re-clocks its hardware</strong> when a stream asks for a rate it is not already
      running at — which is why anything else playing through the same card goes silent at that
      moment — and it is unsettled for a short while afterwards. <strong>Settle after opening</strong>
      is how long the app throws away before it analyses anything, and it is paid only when a stream
      is genuinely opened. Changing the sample rate opens one; an ordinary RUN reuses the stream it
      already has and costs nothing.
    </p>
    <p>
      The default is <strong>3 seconds</strong>, measured rather than guessed. On a MOTU M4 the
      transient ran for about 1.5 s whatever the rate — which was 5 records at 48 kHz and 23 at
      192 kHz, hence a setting in milliseconds rather than in records — and three seconds covers it
      with margin at every rate. Lower it if your card settles faster; raise it if the first trace
      after a rate change still looks wrong.
    </p>
    <p class="manual-callout">
      This is <strong>not</strong> the same thing as <strong>Capture delay</strong>, and neither
      replaces the other. Settle after opening is the <em>card's</em> clock coming good, and applies
      only on an open. Capture delay is the <em>DUT's</em> settling after a level or frequency step,
      and applies after every generator change — on a valve amplifier that is much the larger of the
      two. An earlier version of this page claimed Capture delay covered both; it does not.
    </p>

    <h2 id="calibration">Calibrating the card</h2>
    <p>
      Calibration answers one question per side: <strong>how many volts is digital full scale
      worth?</strong> Until it is answered the app reports levels in <strong>dBFS</strong> and says
      so — the status bar shows an <strong>Uncalibrated</strong> badge.
    </p>
    <p class="manual-callout">
      An uncalibrated card still measures every <em>ratio</em> exactly: THD, IMD, crosstalk, the
      shape of a frequency response, group delay, the bandwidth corners. A consistent wrong
      full-scale constant cancels out of a ratio. What it cannot do is name a voltage — so absolute
      levels, watts, noise density, and the output-referred drive modes all need the calibration.
    </p>

    <p>
      Each of the two steps carries a <strong>?</strong> at the right of its heading, which folds
      out the same instructions in a sentence or two. They are folded away by default: they are
      read once, when the bench is first set up, and the dialog is shorter without them. Everything
      they say is below in full.
    </p>

    <h3>Step 1 — Full scale output</h3>
    <p>This one needs a voltmeter, and it must be done first: step 2 can depend on it.</p>
    <ol>
      <li>
        Connect a voltmeter (or a scope) across the <strong>output</strong> channel. An AC
        millivolt range is what this measures.
      </li>
      <li>
        Choose an <strong>Output level</strong>: how far below full scale to play. −6 dB is a good
        default. Quieter is safer for whatever is connected; louder reads more accurately on a
        meter.
      </li>
      <li>
        Press <strong>Generate sine (400 Hz)</strong>. The tone starts and keeps playing — the
        button changes to <strong>Stop tone</strong> — so you can read the meter while it does.
      </li>
      <li>
        Type what the meter reads into <strong>Voltmeter reads</strong>, and set the unit to
        <strong>mV rms</strong> or <strong>mV peak</strong> to match your meter. Almost every
        multimeter reads rms; a scope cursor usually reads peak. Getting this wrong puts the whole
        calibration out by 3.01 dB.
      </li>
      <li>
        Press <strong>Estimate peak output mV</strong>. The tone stops, the figure is stored, and
        the card is reopened with it.
      </li>
    </ol>
    <p>
      The <strong>current</strong> figure beside the button is what is stored now. The arithmetic is
      just the reading scaled back up by the level played: 1000 mV rms at −6 dB is 2821.7 mV peak at
      full scale.
    </p>

    <h3>Step 2 — Full scale input</h3>
    <p>
      There are two ways to give the inputs a known voltage, chosen with the
      <strong>Known source</strong> dropdown. The patch cable is the default and does both channels
      in one press; a separate generator does one channel at a time, since you have only one
      generator to connect, and is the more trustworthy of the two because it does not inherit
      step 1's error.
    </p>

    <h4>With a patch cable (easiest)</h4>
    <ol>
      <li>Calibrate the output first — this option is refused until you have.</li>
      <li>
        Patch <strong>both</strong> outputs straight back to their own inputs. Both are measured
        together, so both need a cable; an input left unconnected reads as silence and the step
        refuses it, naming the channel.
      </li>
      <li>Choose <strong>This card's output, through a patch cable</strong>.</li>
      <li>
        Pick a <strong>Play at</strong> level. −20 dB is a good default: quiet enough that the input
        cannot clip, loud enough to sit well clear of the card's noise floor.
      </li>
      <li>
        Press <strong>Estimate peak input mV</strong>. Both outputs play, both inputs are measured,
        and both figures are stored in one step.
      </li>
    </ol>
    <p>
      Both channels together rather than one at a time is not only fewer presses: the two figures are
      measured under identical conditions, so the <strong>Diff</strong> between them is meaningful
      immediately — and that difference is the reading that catches a wiring mistake.
    </p>
    <p>
      Because the output is calibrated, the app knows what it is playing in volts — so the cables are
      a source of known voltage and no second generator is needed. The one cost:
      <strong>the figures are linked</strong>. Any error in the output calibration lands directly
      in both of these, where a separate generator would be independent of it.
    </p>

    <h4>With a signal generator</h4>
    <ol>
      <li>
        Feed the input from a generator whose output voltage you know — measured with the same
        voltmeter, ideally.
      </li>
      <li>
        Choose <strong>A generator of known voltage</strong>, and pick which
        <strong>Input channel</strong> it is connected to. This is the only route that asks, because
        it is the only one that does a channel at a time.
      </li>
      <li>Enter that voltage, with the right <strong>mV rms</strong> / <strong>mV peak</strong> unit.</li>
      <li>Press <strong>Estimate peak input mV</strong>.</li>
    </ol>
    <p>
      The app silences its own generator while it measures — anything it were playing would be
      measured along with your source. Move the generator to the other channel, switch
      <strong>Input channel</strong>, and repeat.
    </p>

    <h3>The level bars</h3>
    <p>
      Two bars above the results table show what each input is hearing, live, on a
      −90…0 dBFS scale — the range a calibration cares about: the clip point at the top, the card's
      noise floor off the bottom. They move while a tone is playing or a measurement is running, and
      they turn <span class="text-red-500">red</span> past −0.5 dBFS, which is the point at which a
      reading is no longer the source's level and the step refuses it.
    </p>
    <p>
      They are worth watching before pressing anything: a bar that stays at the bottom while the tone
      plays is a cable on the wrong channel, which is far quicker to see here than to deduce from the
      Diff afterwards.
    </p>

    <h3>Reading the result</h3>
    <p>
      The table shows each input's full scale, and a <strong>Diff</strong> row once both are known.
      A few tenths of a dB is two ordinary channels of one card. Several dB is a wiring or
      channel-pair mistake, and the app says so — check the cable is on the channel you selected.
    </p>
    <p>
      Both steps refuse obviously wrong readings rather than storing them: an input within half a dB
      of full scale is clipping, so the level it reports is not the source's; one below −60 dBFS is
      the card's own noise floor, which usually means nothing is connected.
    </p>

    <h2>What a sound card cannot do</h2>
    <table>
      <thead>
        <tr>
          <th>Difference</th>
          <th>What follows</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>No input attenuator</td>
          <td>
            One fixed range, so there is no autoranging and no Attenuation control. Headroom is set
            by your front-end, and by the card's own input trim if it has one. If a measurement
            clips, turn the source or the generator down.
          </td>
        </tr>
        <tr>
          <td>No output attenuator</td>
          <td>
            One fixed output full scale. The generator's noise floor does not improve at low levels
            the way a QA403's does when it drops to a narrower output range.
          </td>
        </tr>
        <tr>
          <td>AC-coupled at both ends</td>
          <td>
            There is a lowest usable frequency, set in the dialog as
            <strong>Lowest usable frequency</strong> (20 Hz by default). A chirp asked to start below
            it is <em>raised</em> to it and the run says so. This matters: below that corner the card
            measures its own high-pass and reports it as the DUT's roll-off, with normal coherence
            and nothing to suggest otherwise. A DC-coupled front-end fixes only the input side — the
            card's own output coupling still limits the stimulus.
          </td>
        </tr>
        <tr>
          <td>Rates are the card's</td>
          <td>
            Only those of 48/96/192 kHz the hardware accepts. A card offering none of them is
            refused at connect rather than half-working.
          </td>
        </tr>
      </tbody>
    </table>

    <h2>If something is wrong</h2>
    <table>
      <thead>
        <tr>
          <th>Symptom</th>
          <th>Likely cause</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>The device list is empty, or says the audio library would not start</td>
          <td>
            No audio devices, or the app's audio library failed to load. The dialog prints the
            reason rather than pretending there are no cards.
          </td>
        </tr>
        <tr>
          <td>No ASIO devices at all</td>
          <td>
            The card's ASIO driver is not installed. Install the manufacturer's driver and restart
            the app — the device list is read once at start-up, so a newly installed driver needs a
            restart rather than the Refresh button.
          </td>
        </tr>
        <tr>
          <td>The card is listed but will not open</td>
          <td>
            An ASIO device is exclusive: another application already has it. Close whatever else is
            using it.
          </td>
        </tr>
        <tr>
          <td>“This pairing accepts none of 48, 96 or 192 kHz”</td>
          <td>
            The two chosen devices cannot agree on a rate the app measures at — usually a mismatched
            input and output. Try the ASIO entry for the card instead.
          </td>
        </tr>
        <tr>
          <td>Levels look right but are not volts</td>
          <td>Not calibrated yet. The status bar says <strong>Uncalibrated</strong>.</td>
        </tr>
      </tbody>
    </table>

    <p class="manual-see-also">
      See also
      <a href="#" @click.prevent="showPage('wiring-basics')">Wiring basics</a>,
      <a href="#" @click.prevent="showPage('frequency-response')">Frequency response</a> and
      <a href="#" @click.prevent="showPage('settings')">Settings</a>.
    </p>
  </div>
</template>
