// The manual's table of contents. One entry per page; `kind` is what wires a measurement screen's
// own help button to the right page (see manualPageForKind), so a new measurement kind needs a
// page here as well as an entry in measurements/kinds.ts.
//
// There are two *editions* of the manual over one page list. The `reference` edition is the full
// manual: every control, every convention, the measured figures behind them. The `guide` edition is
// a second, shorter manual in plain language for someone meeting an audio analyzer for the first
// time — fewer numbers, no formulas, one job per page. Neither replaces the other, and a page in
// either one may name its `counterpart` in the other, which is what puts the cross-link banner at
// the top of both (see manualCounterpart): the link is declared once, on the guide page, and the
// reference page finds it by searching backwards.
//
// Page bodies are plain-markup Vue components under ./pages, loaded lazily by ManualDialog so the
// manual costs nothing until it is opened.
import type { Component } from 'vue'
import { defineAsyncComponent, h } from 'vue'
import {
  CROSSTALK_KIND,
  OUTPUT_IMPEDANCE_KIND,
  FFT_SPECTRUM_KIND,
  FREQUENCY_RESPONSE_KIND,
  IMD_VS_AMPLITUDE_KIND,
  IMPEDANCE_KIND,
  IMPULSE_RESPONSE_KIND,
  STEPPED_RESPONSE_KIND,
  THD_VS_AMPLITUDE_KIND,
  THD_VS_FREQUENCY_KIND,
} from '../measurements/kinds'

/** Which of the two manuals a page belongs to. */
export type ManualEdition = 'guide' | 'reference'

/** Grouping headers of the plain-language guide. */
export type GuideGroup = 'Start here' | 'What the numbers mean' | 'Things to measure' | 'Afterwards'

/** Grouping headers of the full manual. */
export type ReferenceGroup = 'Basics' | 'Wiring' | 'Measurements' | 'Simulator' | 'Reference'

export type ManualGroup = GuideGroup | ReferenceGroup

export interface ManualPage {
  id: string
  title: string
  /** One line under the title in the index. */
  summary: string
  /** Which measurement kind's help button lands here, if any. Reference pages only. */
  kind?: string
  /** Grouping header in the index sidebar. One of its own edition's groups. */
  group: ManualGroup
  /** Which manual this page is part of. Stamped by the arrays below, never written per page. */
  edition: ManualEdition
  /** The page in the other edition that covers the same ground, if there is one. */
  counterpart?: string
  component: Component
}

/** A page as the arrays below declare it: the edition comes from which array it is in. */
type ManualPageEntry = Omit<ManualPage, 'edition'>

export interface ManualEditionInfo {
  id: ManualEdition
  /** On the edition switcher. */
  title: string
  /** Under the title on that edition's contents page, and as the switcher's tooltip. */
  blurb: string
  groups: readonly ManualGroup[]
}

/**
 * Shown when a manual page's chunk cannot be fetched.
 *
 * Manual pages are the only lazily-loaded chunks in this app, so they are the only thing that can
 * fail this way — and it does happen: a tab left open across a rebuild holds an `index.html` naming
 * chunk filenames that no longer exist, so every page 404s. Vue's default for a failed async
 * component is to render <em>nothing</em>, which is a blank page with no clue as to why, and that is
 * exactly how it presented. The server now sends `Cache-Control: no-cache` for `index.html` so a
 * reload always picks up current filenames; this is the backstop that says so when it still happens.
 */
const ManualPageUnavailable: Component = {
  name: 'ManualPageUnavailable',
  setup() {
    return () =>
      h('div', { class: 'text-sm' }, [
        h('h1', 'This page could not be loaded'),
        h('p', 'The manual loads each page on demand, and this one did not arrive.'),
        h(
          'p',
          'That normally means the app was updated while this tab was open, so it is asking for a ' +
            'version of the page that no longer exists. Reload the page and it will come back.',
        ),
        h(
          'button',
          {
            type: 'button',
            class:
              'mt-2 rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800',
            onClick: () => window.location.reload(),
          },
          'Reload',
        ),
      ])
  },
}

/** A lazily-loaded page body, with the blank-page failure above turned into something readable. */
function manualPage(loader: () => Promise<Component>): Component {
  return defineAsyncComponent({ loader, errorComponent: ManualPageUnavailable })
}

/**
 * The plain-language guide.
 *
 * Written for someone whose first question is "what do I plug in and what am I looking at", not
 * "which convention is this figure referred to". Each page states the few numbers it needs and
 * hands the reader to its `counterpart` in the full manual for the rest, so the two stay in one
 * relationship rather than drifting into two half-manuals.
 */
const GUIDE_PAGES: ManualPageEntry[] = [
  {
    id: 'guide-welcome',
    title: 'What this app does',
    summary: 'What an audio analyzer measures, what you need on the bench, and how to try it with no hardware.',
    group: 'Start here',
    counterpart: 'getting-started',
    component: manualPage(() => import('./pages/guide/GuideWelcomePage.vue')),
  },
  {
    id: 'guide-connecting',
    title: 'Plugging everything in',
    summary: 'The eight sockets, the four plugs that must always be fitted, and the two ways to damage something.',
    group: 'Start here',
    counterpart: 'wiring-basics',
    component: manualPage(() => import('./pages/guide/GuideConnectingPage.vue')),
  },
  {
    id: 'guide-sound-card',
    title: 'Using a sound card instead',
    summary: 'No QA403? Use an audio interface: choosing it, and the five minutes with a multimeter that teach it what a volt is.',
    group: 'Start here',
    counterpart: 'sound-card',
    component: manualPage(() => import('./pages/guide/GuideSoundCardPage.vue')),
  },
  {
    id: 'guide-first-measurement',
    title: 'Your first measurement',
    summary: 'A loopback, a 1 kHz tone, and a spectrum on screen — ten minutes, step by step.',
    group: 'Start here',
    counterpart: 'fft-spectrum',
    component: manualPage(() => import('./pages/guide/GuideFirstMeasurementPage.vue')),
  },
  {
    id: 'guide-screen',
    title: 'Finding your way around',
    summary: 'The three columns, the green button, the chart, and where your results go.',
    group: 'Start here',
    counterpart: 'charts',
    component: manualPage(() => import('./pages/guide/GuideScreenPage.vue')),
  },
  {
    id: 'guide-decibels',
    title: 'Decibels and volts',
    summary: 'What dB, dBV and a minus sign in front of a level actually tell you — with a table instead of a formula.',
    group: 'What the numbers mean',
    component: manualPage(() => import('./pages/guide/GuideDecibelsPage.vue')),
  },
  {
    id: 'guide-distortion',
    title: 'Distortion in plain words',
    summary: 'Harmonics, THD, THD+N — what they are, which numbers are good, and what they sound like.',
    group: 'What the numbers mean',
    counterpart: 'thd-vs-frequency',
    component: manualPage(() => import('./pages/guide/GuideDistortionPage.vue')),
  },
  {
    id: 'guide-noise',
    title: 'Noise, hum and the floor',
    summary: 'The grass along the bottom of the chart, the spikes at 50 Hz, and how to tell whose fault they are.',
    group: 'What the numbers mean',
    component: manualPage(() => import('./pages/guide/GuideNoisePage.vue')),
  },
  {
    id: 'guide-words',
    title: 'Words you will see',
    summary: 'Twenty-odd terms from the panels and the axes, one line each.',
    group: 'What the numbers mean',
    counterpart: 'glossary',
    component: manualPage(() => import('./pages/guide/GuideWordsPage.vue')),
  },
  {
    id: 'guide-response',
    title: 'Measuring frequency response',
    summary: 'Whether your amplifier treats the bass the same as the treble — one sweep, one curve.',
    group: 'Things to measure',
    counterpart: 'frequency-response',
    component: manualPage(() => import('./pages/guide/GuideResponsePage.vue')),
  },
  {
    id: 'guide-amplifier',
    title: 'Checking an amplifier',
    summary: 'Five measurements in the order to make them, with the settings to use and what a healthy result looks like.',
    group: 'Things to measure',
    component: manualPage(() => import('./pages/guide/GuideAmplifierPage.vue')),
  },
  {
    id: 'guide-loudspeaker',
    title: 'Measuring a loudspeaker',
    summary: 'A resistor, two clips and an impedance curve — the easiest useful measurement on the bench.',
    group: 'Things to measure',
    counterpart: 'impedance',
    component: manualPage(() => import('./pages/guide/GuideLoudspeakerPage.vue')),
  },
  {
    id: 'guide-keeping-results',
    title: 'Keeping your results',
    summary: 'Projects, measurements and results — which is which, and how to compare last week with today.',
    group: 'Afterwards',
    counterpart: 'saving',
    component: manualPage(() => import('./pages/guide/GuideKeepingResultsPage.vue')),
  },
  {
    id: 'guide-problems',
    title: 'When something looks wrong',
    summary: 'The dozen things that go wrong most often, what each looks like on screen, and what to do.',
    group: 'Afterwards',
    component: manualPage(() => import('./pages/guide/GuideProblemsPage.vue')),
  },
]

/** The full manual. */
const REFERENCE_PAGES: ManualPageEntry[] = [
  {
    id: 'getting-started',
    title: 'Getting started',
    summary: 'The device, projects, measurements, and the Run/Stop model.',
    group: 'Basics',
    component: manualPage(() => import('./pages/GettingStartedPage.vue')),
  },
  {
    id: 'projects',
    title: 'Projects',
    summary: 'Project files, the Project menu, the welcome screen and the recent list.',
    group: 'Basics',
    component: manualPage(() => import('./pages/ProjectsPage.vue')),
  },
  {
    id: 'charts',
    title: 'Reading the charts',
    summary: 'Cursors, zooming, autofit, colours and the off-scale markers.',
    group: 'Basics',
    component: manualPage(() => import('./pages/ChartsPage.vue')),
  },
  {
    id: 'wiring-basics',
    title: 'Connections and cables',
    summary: 'The eight BNCs, shorting plugs, the +32 dBV ceiling, and keeping hum out.',
    group: 'Wiring',
    component: manualPage(() => import('./pages/wiring/WiringBasicsPage.vue')),
  },
  {
    id: 'wiring-loopback',
    title: 'Wiring: loopback',
    summary: 'Output straight back to input — the analyzer measuring itself.',
    group: 'Wiring',
    component: manualPage(() => import('./pages/wiring/WiringLoopbackPage.vue')),
  },
  {
    id: 'wiring-line-level',
    title: 'Wiring: a line-level DUT',
    summary: 'One channel in, one channel out — and the reference tap for phase.',
    group: 'Wiring',
    component: manualPage(() => import('./pages/wiring/WiringLineLevelPage.vue')),
  },
  {
    id: 'wiring-power-amp',
    title: 'Wiring: a power amplifier',
    summary: 'The load, the quiet ground, and the divider for anything past 40 V.',
    group: 'Wiring',
    component: manualPage(() => import('./pages/wiring/WiringPowerAmpPage.vue')),
  },
  {
    id: 'wiring-impedance',
    title: 'Wiring: the impedance fixture',
    summary: 'The series reference resistor, the two taps, and the calibration standards.',
    group: 'Wiring',
    component: manualPage(() => import('./pages/wiring/WiringImpedancePage.vue')),
  },
  {
    id: 'wiring-crosstalk',
    title: 'Wiring: crosstalk',
    summary: 'Both channels loaded, the victim shorted, and why the returns do not cross.',
    group: 'Wiring',
    component: manualPage(() => import('./pages/wiring/WiringCrosstalkPage.vue')),
  },
  {
    id: 'wiring-output-impedance',
    title: 'Wiring: output impedance',
    summary: 'Two loads, a Kelvin tap and the reference loop.',
    group: 'Wiring',
    component: manualPage(() => import('./pages/wiring/WiringOutputImpedancePage.vue')),
  },
  {
    id: 'fft-spectrum',
    title: 'FFT analysis',
    summary: 'Spectrum, THD, harmonics and noise floor from a single FFT capture.',
    kind: FFT_SPECTRUM_KIND,
    group: 'Measurements',
    component: manualPage(() => import('./pages/FftSpectrumPage.vue')),
  },
  {
    id: 'thd-vs-frequency',
    title: 'THD vs frequency',
    summary: 'Distortion against frequency at one drive level.',
    kind: THD_VS_FREQUENCY_KIND,
    group: 'Measurements',
    component: manualPage(() => import('./pages/ThdVsFrequencyPage.vue')),
  },
  {
    id: 'thd-vs-amplitude',
    title: 'THD vs amplitude',
    summary: 'Distortion against level at one frequency — the clipping knee.',
    kind: THD_VS_AMPLITUDE_KIND,
    group: 'Measurements',
    component: manualPage(() => import('./pages/ThdVsAmplitudePage.vue')),
  },
  {
    id: 'imd-vs-amplitude',
    title: 'IMD vs amplitude',
    summary: 'Two-tone intermodulation (SMPTE, DIN, CCIF) against level.',
    kind: IMD_VS_AMPLITUDE_KIND,
    group: 'Measurements',
    component: manualPage(() => import('./pages/ImdVsAmplitudePage.vue')),
  },
  {
    id: 'frequency-response',
    title: 'Frequency response',
    summary: 'A chirp sweep: gain, phase, coherence, bandwidth, Nyquist and Nichols.',
    kind: FREQUENCY_RESPONSE_KIND,
    group: 'Measurements',
    component: manualPage(() => import('./pages/FrequencyResponsePage.vue')),
  },
  {
    id: 'impedance',
    title: 'Impedance',
    summary: 'Impedance in ohms and phase, through a series reference resistor, with an open/short fixture calibration.',
    kind: IMPEDANCE_KIND,
    group: 'Measurements',
    component: manualPage(() => import('./pages/ImpedancePage.vue')),
  },
  {
    id: 'stepped-response',
    title: 'Stepped sine response',
    summary: 'The same curves, one tone at a time — slow, quiet and literal.',
    kind: STEPPED_RESPONSE_KIND,
    group: 'Measurements',
    component: manualPage(() => import('./pages/SteppedResponsePage.vue')),
  },
  {
    id: 'crosstalk',
    title: 'Crosstalk',
    summary: 'How much one channel leaks into the other, in dB against frequency, one direction or both.',
    kind: CROSSTALK_KIND,
    group: 'Measurements',
    component: manualPage(() => import('./pages/CrosstalkPage.vue')),
  },
  {
    id: 'output-impedance',
    title: 'Output impedance and damping factor',
    summary: 'An amplifier\u2019s output impedance in ohms and its damping factor, from its gain with and without the load.',
    kind: OUTPUT_IMPEDANCE_KIND,
    group: 'Measurements',
    component: manualPage(() => import('./pages/OutputImpedancePage.vue')),
  },
  {
    id: 'impulse-response',
    title: 'Impulse response',
    summary: 'Gating out the room, and the distortion the same sweep leaves behind.',
    kind: IMPULSE_RESPONSE_KIND,
    group: 'Measurements',
    component: manualPage(() => import('./pages/ImpulseResponsePage.vue')),
  },
  {
    id: 'sound-card',
    title: 'Using a sound card',
    summary: 'A sound card as a calibrated analyzer: choosing the card and host API, both calibration steps, and what it cannot do that a QA403 can.',
    group: 'Basics',
    component: manualPage(() => import('./pages/SoundCardPage.vue')),
  },
  {
    id: 'simulator',
    title: 'Using the simulator',
    summary: 'Measuring without hardware: how the simulator is wired, and what each DUT model is for.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/SimulatorPage.vue')),
  },
  {
    id: 'sim-straight-wire',
    title: 'Straight wire',
    summary: 'No DUT: the bare generator, what the Noise, Hum and Harmonics boxes add to it, and what the analysis reads with nothing in the path.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/StraightWirePage.vue')),
  },
  {
    id: 'sim-tube-amp',
    title: 'Tube amp',
    summary: 'A transformer-coupled valve amplifier, with the settings and expected result of every measurement against it.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/TubeAmpPage.vue')),
  },
  {
    id: 'sim-loudspeaker',
    title: 'Loudspeaker',
    summary: 'A two-way speaker in a room: the impulse gate, the gated response and both decay surfaces.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/LoudspeakerPage.vue')),
  },
  {
    id: 'sim-bandpass',
    title: 'Bandpass',
    summary: 'A Q-20 resonance whose decay is 1.36 dB per period on paper — the calibration target for the decay charts.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/BandpassPage.vue')),
  },
  {
    id: 'sim-marginally-stable',
    title: 'Marginally stable',
    summary: 'A Q-500 resonance that a smoothed Bode plot hides and a burst decay cannot.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/MarginallyStablePage.vue')),
  },
  {
    id: 'sim-oscillating',
    title: 'Oscillating',
    summary: 'An amplifier singing on its own: what every measurement does when the DUT is not answering the stimulus.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/OscillatingPage.vue')),
  },
  {
    id: 'sim-phono-riaa',
    title: 'Phono stage (RIAA)',
    summary: 'The RIAA curve: the one DUT with gain, and what a 40 dB tilt does to the bandwidth rows and the THD sweep.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/PhonoRiaaPage.vue')),
  },
  {
    id: 'sim-dsp-processor',
    title: 'DSP processor (12 ms)',
    summary: 'A 20 kHz brick wall with 12 ms of latency: the delay readout and the impulse that is not at zero.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/DspProcessorPage.vue')),
  },
  {
    id: 'sim-butterworth',
    title: 'Butterworth LP (2 kHz)',
    summary: 'The maximally flat fifth-order low-pass, and the four classic alignments side by side.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/ButterworthPage.vue')),
  },
  {
    id: 'sim-chebyshev',
    title: 'Chebyshev LP (2 kHz)',
    summary: '1 dB of passband ripple bought a transition twice as steep — and a ring, and a delay peak.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/ChebyshevPage.vue')),
  },
  {
    id: 'sim-bessel',
    title: 'Bessel LP (2 kHz)',
    summary: 'A flat 193 µs group delay and the gentlest roll-off: the model for the delay columns.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/BesselPage.vue')),
  },
  {
    id: 'sim-elliptic',
    title: 'Elliptic LP (2 kHz)',
    summary: 'Two nulls, the steepest skirt of the four, and a stopband that stops at −60 dB.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/EllipticPage.vue')),
  },
  {
    id: 'sim-triode',
    title: 'SE triode',
    summary: 'H2 rising 1 dB per dB: the straight line on a THD-vs-amplitude sweep.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/TriodePage.vue')),
  },
  {
    id: 'sim-class-ab',
    title: 'Class AB (crossover)',
    summary: 'Distortion that gets worse as the level falls: the hump.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/ClassAbPage.vue')),
  },
  {
    id: 'sim-solid-state',
    title: 'Solid state (clips at −3 dBV)',
    summary: 'A floor and then a wall: clipping on the amplitude sweep.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/SolidStatePage.vue')),
  },
  {
    id: 'sim-output-transformer',
    title: 'Output transformer (saturating)',
    summary: 'Third harmonic rising 18 dB per octave towards the bass: the THD-vs-frequency curve of a real output stage.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/OutputTransformerPage.vue')),
  },
  {
    id: 'sim-fixture-8-ohm',
    title: 'Fixture: 8 Ω',
    summary: 'The impedance calibration target: 8.000 Ω at 0.00° from end to end, or something is wrong.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/FixtureResistivePage.vue')),
  },
  {
    id: 'sim-fixture-rl',
    title: 'Fixture: 6 Ω + 1 mH',
    summary: 'A series RL: reactance, phase, and the simulator’s bilinear warping at the top of the band.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/FixtureReactivePage.vue')),
  },
  {
    id: 'sim-fixture-capacitor',
    title: 'Fixture: 47 µF',
    summary: 'A capacitor with ESR: the mirror of the RL, and the sign of the phase axis.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/FixtureCapacitorPage.vue')),
  },
  {
    id: 'sim-fixture-loudspeaker',
    title: 'Fixture: loudspeaker',
    summary: 'A driver’s impedance: 6.4 Ω of voice coil and a 60 Ω resonance at 45 Hz.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/FixtureLoudspeakerPage.vue')),
  },
  {
    id: 'sim-fixture-standards',
    title: 'Fixture: short and open',
    summary: 'The two calibration standards, the drive level they need, and the calibration flow end to end.',
    group: 'Simulator',
    component: manualPage(() => import('./pages/sim/FixtureStandardsPage.vue')),
  },
  {
    id: 'view-nyquist',
    title: 'The Nyquist view',
    summary: 'G(jω) as one contour, the critical point, and where the margins are.',
    group: 'Reference',
    component: manualPage(() => import('./pages/views/NyquistViewPage.vue')),
  },
  {
    id: 'view-nichols',
    title: 'The Nichols view',
    summary: 'The same contour on gain/phase axes, where both margins are distances.',
    group: 'Reference',
    component: manualPage(() => import('./pages/views/NicholsViewPage.vue')),
  },
  {
    id: 'view-pole-zero',
    title: 'The pole–zero map',
    summary: 'The fitted model\u2019s roots in the s-plane — a different plane from the other two.',
    group: 'Reference',
    component: manualPage(() => import('./pages/views/PoleZeroViewPage.vue')),
  },
  {
    id: 'view-s-plane',
    title: 'The |H(s)| surface',
    summary: 'The fitted model’s magnitude over the whole s-plane — and the formula behind it.',
    group: 'Reference',
    component: manualPage(() => import('./pages/views/SPlaneViewPage.vue')),
  },
  {
    id: 'saving',
    title: 'Projects, saving and history',
    summary: 'What gets stored where, overlays, CSV export, screenshots and PDF reports, deleting.',
    group: 'Reference',
    component: manualPage(() => import('./pages/SavingPage.vue')),
  },
  {
    id: 'settings',
    title: 'Configuration and status bar',
    summary: 'Global settings, attenuation, and what the warnings mean.',
    group: 'Reference',
    component: manualPage(() => import('./pages/SettingsPage.vue')),
  },
  {
    id: 'glossary',
    title: 'Glossary',
    summary: 'What the terms mean, A to Z — and which convention this analyzer uses for each.',
    group: 'Reference',
    component: manualPage(() => import('./pages/GlossaryPage.vue')),
  },
]

export const MANUAL_EDITIONS: readonly ManualEditionInfo[] = [
  {
    id: 'guide',
    title: 'Simple guide',
    blurb: 'Plain language, no formulas: what to plug in, what to press, and what the numbers mean.',
    groups: ['Start here', 'What the numbers mean', 'Things to measure', 'Afterwards'],
  },
  {
    id: 'reference',
    title: 'Full manual',
    blurb: 'Every screen, every control and every convention, with the measured figures behind them.',
    groups: ['Basics', 'Wiring', 'Measurements', 'Simulator', 'Reference'],
  },
]

/** The edition a page belongs to is stamped here, so no entry above can claim the wrong one. */
function inEdition(edition: ManualEdition, pages: ManualPageEntry[]): ManualPage[] {
  return pages.map((page) => ({ ...page, edition }))
}

export const MANUAL_PAGES: ManualPage[] = [
  ...inEdition('guide', GUIDE_PAGES),
  ...inEdition('reference', REFERENCE_PAGES),
]

export function manualEdition(id: ManualEdition): ManualEditionInfo {
  return MANUAL_EDITIONS.find((e) => e.id === id) ?? MANUAL_EDITIONS[0]
}

/** The pages of one edition, in reading order — which is also the order the ← → links walk. */
export function manualPagesIn(edition: ManualEdition): ManualPage[] {
  return MANUAL_PAGES.filter((p) => p.edition === edition)
}

export function manualPagesInGroup(group: ManualGroup): ManualPage[] {
  return MANUAL_PAGES.filter((p) => p.group === group)
}

/**
 * The page in the other edition covering the same ground, in either direction.
 *
 * The relationship is declared once, on the guide page, and followed backwards from the reference
 * page — so the fifty pages of the full manual need no edit to gain their link, and a link cannot
 * exist in one direction only.
 */
export function manualCounterpart(page: ManualPage | null): ManualPage | null {
  if (page === null) return null
  if (page.counterpart) return manualPageById(page.counterpart)
  return MANUAL_PAGES.find((p) => p.counterpart === page.id) ?? null
}

export function manualPageById(id: string | null): ManualPage | null {
  if (id === null) return null
  return MANUAL_PAGES.find((p) => p.id === id) ?? null
}

/**
 * The help page a measurement screen's own "?" button opens. Null for a kind with no page yet — the
 * caller falls back to the index.
 *
 * Deliberately the *full* manual's page: that button means "what does this control do", which is the
 * question the reference edition answers. A reader who wants the simpler version reaches it from the
 * banner at the top of that page, which is one press away and says what it is.
 */
export function manualPageForKind(kind: string | null | undefined): ManualPage | null {
  if (!kind) return null
  return MANUAL_PAGES.find((p) => p.edition === 'reference' && p.kind === kind) ?? null
}
