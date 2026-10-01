<script setup lang="ts">
import WiringDiagram from '../../components/WiringDiagram.vue'
import { useManual } from '../../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Wiring: a power amplifier</h1>

    <p>
      The signal path is the same as any
      <button type="button" class="manual-link" @click="showPage('wiring-line-level')">
        line-level DUT
      </button>
      — drive the input, measure the output — but three things change: the output needs a load, the
      level can exceed what the analyzer will survive, and the ground between the two boxes becomes
      the thing that decides your noise floor.
    </p>

    <h2>Before anything is switched on</h2>
    <ul>
      <li>
        <strong>+32 dBV is 40 V rms, and it is a limit, not a range.</strong> Into 8 Ω that is
        200 W. The input range list goes to 42 dBV, but that is an internal attenuator ahead of the
        ADC and it does not raise the ceiling. Anything that can swing past 40 V rms needs the
        divider below.
      </li>
      <li>
        <strong>Never run a valve amplifier without a load.</strong> An output transformer driven
        into an open circuit can arc over and take the transformer or the valves with it. Fit the
        load first, switch on second.
      </li>
      <li>
        <strong>Work out the drive level before you apply it.</strong> An amplifier with 26 dB of
        gain reaches 1 W into 8 Ω (+9 dBV) from about −17 dBV in. Start below that.
      </li>
    </ul>

    <h2>The connection</h2>

    <WiringDiagram
      :terminate="['in-l-', 'in-r+', 'in-r-']"
      :height="400"
      alt="Output L plus drives the amplifier's input; the amplifier's plus terminal goes to Input L plus and its minus terminal is the cable's shell, with the load resistor beyond the tap. All three unused inputs carry shorting plugs."
      caption="One coaxial cable at the terminals, the load on top of it, and a plug on IN L−."
    >
      <template #default="{ cable, path }">
        <rect x="190" y="270" width="210" height="70" rx="4" class="w-block-fill" />
        <text x="295" y="300" text-anchor="middle" class="w-label">Power amplifier</text>
        <text x="295" y="318" text-anchor="middle" class="w-note">one channel</text>

        <!-- Output terminals, then the load beyond the measurement tap -->
        <path class="w-thin" :d="path([[400, 295], [600, 295]])" />
        <path class="w-thin" :d="path([[400, 325], [600, 325]])" />
        <path class="w-thin" :d="path([[600, 295], [600, 299]])" />
        <path class="w-thin" :d="path([[600, 321], [600, 325]])" />
        <rect x="588" y="299" width="24" height="22" class="w-block" />
        <text x="600" y="345" text-anchor="middle" class="w-note">8 Ω load</text>

        <path class="w-cable w-drive" :d="cable('out-l+', 270, [242, 270])" />
        <path class="w-cable w-measure" :d="cable('in-l+', 295, [445, 295])" />
        <circle cx="445" cy="295" r="3.2" class="w-node" />
        <text x="452" y="289" class="w-note">+ terminal — centre conductor</text>

        <!-- The − terminal is the same cable's shell, i.e. the analyzer's ground. -->
        <circle cx="530" cy="325" r="3.2" class="w-node" />
        <path class="w-thin" :d="path([[530, 325], [530, 352]])" />
        <path class="w-thin" :d="path([[520, 352], [540, 352]])" />
        <path class="w-thin" :d="path([[524, 357], [536, 357]])" />
        <path class="w-thin" :d="path([[527, 362], [533, 362]])" />
        <text x="452" y="320" class="w-note">− terminal</text>
        <text x="530" y="376" text-anchor="middle" class="w-note">the cable's shell</text>

        <text x="250" y="212" class="w-label w-drive">Drive</text>
        <text x="330" y="246" text-anchor="end" class="w-label w-measure">Measure</text>
        <text x="300" y="394" text-anchor="middle" class="w-note">
          The load hangs beyond the tap, as close to the terminals as it will go.
        </text>
      </template>
    </WiringDiagram>

    <ul>
      <li>
        <strong>OUT L+ to the amplifier's input</strong> with an ordinary single-ended cable, kept
        short and away from the output stage. With 26 dB of gain, anything picked up on this cable
        arrives at the analyzer twenty times larger than the same pickup on the output cable.
      </li>
      <li>
        <strong>The amplifier's + terminal to IN L+</strong>, with the same cable's shell on the
        amplifier's − terminal. One ordinary coaxial cable, exactly as on the line-level page: the
        centre conductor carries the signal and the shell is the ground the analyzer measures it
        against.
      </li>
      <li>
        <strong>A shorting plug stays on IN L−</strong>, as on every measurement. Nothing here is
        measured differentially, and an open − input costs 33 dB of noise floor whatever is on the
        + one.
      </li>
      <li>
        <strong>The load goes beyond the measurement tap.</strong> Take the measuring cable from
        the terminals themselves and fit the load resistor on top of it — a Kelvin connection, so
        load current does not show up as a voltage in what you are measuring. It matters most for
        <button type="button" class="manual-link" @click="showPage('wiring-output-impedance')">
          output impedance
        </button>, where the whole answer is a fraction of an ohm.
      </li>
      <li>
        <strong>The load must be non-inductive and within a few tens of centimetres.</strong> One
        microhenry is 0.13 Ω at 20 kHz, which is more than a good amplifier's entire output
        impedance up there. An ordinary wirewound resistor is worse than its value suggests.
      </li>
      <li><strong>Shorting plugs on IN R+ and IN R−</strong> while the right channel is idle.</li>
    </ul>

    <h3>Never ground one side of a bridged output</h3>
    <p>
      A bridged or BTL amplifier drives both of its speaker terminals, and neither is at ground. The
      measuring cable's shell puts one of them on the analyzer's chassis — which shorts half the
      amplifier and can destroy it. The same is true of most class-D modules. If the output is
      bridged, measure it through an external differential probe or an isolating transformer that
      presents a ground-referenced signal to the analyzer, and set any attenuation it introduces as
      the external gain below.
    </p>

    <h3>Two cables means two ground paths</h3>
    <p>
      The drive cable's shell and the measuring cable's shell both join the two chassis, and the
      loop they enclose is where mains hum gets in. Nothing about the wiring can remove the second
      path, so manage the loop instead: keep both cables short, run them together rather than around
      opposite sides of the bench, keep the drive cable away from the output stage, and put the
      computer and the amplifier on the same outlet. A laptop on its battery is the quickest way to
      find out how much of a floor is the mains and how much is the amplifier.
    </p>

    <h2>When the amplifier is bigger than the analyzer</h2>
    <p>
      Past 40 V rms, attenuate outside the analyzer. A resistive divider is enough as long as its
      source impedance stays low — a kilohm or so, which with cable and input capacitance still puts
      the bandwidth above a megahertz.
    </p>

    <figure class="manual-figure">
      <svg
        viewBox="0 0 560 200"
        role="img"
        aria-label="A resistive divider: 14.85 kilohm in series from the amplifier's output, 1 kilohm shunt to ground, with the junction going to the analyzer's left input."
      >
        <rect x="20" y="40" width="70" height="90" rx="3" />
        <text x="55" y="80" text-anchor="middle" class="label">Power</text>
        <text x="55" y="96" text-anchor="middle" class="label">amp</text>

        <line x1="90" y1="60" x2="130" y2="60" />
        <rect x="130" y="50" width="60" height="20" />
        <text x="160" y="44" text-anchor="middle" class="label">14.85 kΩ</text>
        <line x1="190" y1="60" x2="400" y2="60" />
        <circle cx="280" cy="60" r="3.5" class="node" />

        <line x1="280" y1="60" x2="280" y2="86" />
        <rect x="270" y="86" width="20" height="48" />
        <text x="298" y="114" class="label">1 kΩ</text>
        <line x1="280" y1="134" x2="280" y2="160" />

        <line x1="90" y1="110" x2="110" y2="110" />
        <line x1="110" y1="110" x2="110" y2="160" />
        <line x1="110" y1="160" x2="400" y2="160" />

        <circle cx="400" cy="60" r="3.5" class="node" />
        <text x="410" y="56" class="label">Input L+</text>
        <text x="410" y="70" class="label muted">(and its shell)</text>

        <line x1="400" y1="160" x2="400" y2="168" />
        <line x1="390" y1="168" x2="410" y2="168" />
        <line x1="394" y1="173" x2="406" y2="173" />
        <line x1="398" y1="178" x2="402" y2="178" />
      </svg>
      <figcaption>
        A 24 dB divider: 15.85:1, so 56.6 V rms (400 W into 8 Ω) arrives as 3.57 V rms. Tell the app
        about it with an external gain of −24 dB.
      </figcaption>
    </figure>

    <ul>
      <li>
        <strong>Enter the attenuation in the status bar's <em>Ext. gain</em> panel</strong> (click
        the badge or the pencil beside it), per input, as a negative number: −24 for the divider
        above, −20 for a plain 10:1. Every level
        the app reports is then the amplifier's, not the analyzer's — spectrum, meters, sweeps,
        output-referred drive targets, all of it — while the input range it chooses still refers to
        what the analyzer actually sees.
      </li>
      <li>
        <strong>Set both channels if both go through a divider.</strong> A measurement that
        references the right input — output impedance with the complex method, an impedance fixture
        — has the divider on one side only, and the app cannot guess which.
      </li>
      <li>
        <strong>Resistors at ten times the working dissipation.</strong> 400 W into 8 Ω puts about
        3.6 mA through a 15.85 kΩ divider, so 200 mW total; a 1 W shunt and a 2 W series resistor
        keep them cool enough not to drift or add low-frequency thermal distortion.
      </li>
      <li>
        <strong>Take the divider out for noise measurements.</strong> Its own thermal noise is
        around 4 nV/√Hz, and with the amplifier idling there is nothing to attenuate.
      </li>
    </ul>

    <h2>Related</h2>
    <p>
      Two amplifier measurements have their own wiring:
      <button type="button" class="manual-link" @click="showPage('wiring-output-impedance')">
        output impedance and damping factor
      </button>
      needs two loads and a pause in the middle, and
      <button type="button" class="manual-link" @click="showPage('wiring-crosstalk')">crosstalk</button>
      needs both channels loaded at once.
    </p>
  </div>
</template>
