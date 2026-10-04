// The simulator's DUT models, coefficient for coefficient as `SimulatedDut.For` builds them
// (src/Tonebench.Device/Simulation/SimulatedDut.cs). Edit the two together: a model changed on one side
// and not the other leaves the manual drawing a response the simulator no longer produces, and
// nothing but biquad.test.ts — which pins a handful of measured figures — would notice.
//
// Only the linear part of each model is a Cascade, because only that has a transfer function to
// draw. The four nonlinear models are memoryless (or nearly so) transfer curves in waveshapers.ts,
// and the oscillator is a recursion in oscillator.ts.
import {
  bandPass,
  Cascade,
  fromAnalog,
  gain,
  highPass,
  lowPass,
  peaking,
  sectionResponse,
  type Biquad,
} from './biquad'

/** No DUT at all: the generator reaches the input unaltered. */
export function straightWire(sampleRateHz: number): Cascade {
  return new Cascade([], sampleRateHz)
}

/**
 * A push-pull valve amplifier with an output transformer. The primary inductance rolls the bottom
 * off below 12 Hz with a small feedback lift at 45 Hz; leakage inductance against winding
 * capacitance resonates at 38 kHz, 4 dB up, and the response falls away above 46 kHz.
 */
export function tubeAmplifier(sampleRateHz: number): Cascade {
  return new Cascade(
    [
      highPass(12, 0.75, sampleRateHz),
      peaking(45, 3, 1.8, sampleRateHz),
      peaking(38_000, 3.5, 4, sampleRateHz),
      lowPass(46_000, 0.9, sampleRateHz),
    ],
    sampleRateHz,
  )
}

/** The loudspeaker's wall reflection: a metre further round than the direct path, 12 dB down and inverted. */
export const LOUDSPEAKER_REFLECTION = { delayMs: 3, gain: -0.25 } as const

/**
 * A two-way loudspeaker in a room: a sealed-box alignment at 45 Hz, a cabinet panel at 230 Hz
 * (Q 20, +4 dB), the crossover's dip at 900 Hz, the cone's first breakup at 4.2 kHz (Q 12, +6 dB),
 * a 19 kHz roll-off, and the reflection above.
 */
export function loudspeaker(sampleRateHz: number): Cascade {
  return new Cascade(
    [
      highPass(45, 1, sampleRateHz),
      peaking(230, 20, 4, sampleRateHz),
      peaking(900, 1.4, -3, sampleRateHz),
      peaking(4_200, 12, 6, sampleRateHz),
      lowPass(19_000, 0.9, sampleRateHz),
    ],
    sampleRateHz,
    LOUDSPEAKER_REFLECTION,
  )
}

export const BANDPASS_CENTRE_HZ = 1000
export const BANDPASS_Q = 20
/** What a second-order resonance loses per period of its own centre frequency: 20·log10(e)·π/Q. */
export const DB_PER_PERIOD_NUMERATOR = 27.2875
export const BANDPASS_DB_PER_PERIOD = DB_PER_PERIOD_NUMERATOR / BANDPASS_Q

/** One second-order band-pass at 1 kHz, Q 20: 50 Hz wide, unity at the centre, nothing else. */
export function bandpassFilter(sampleRateHz: number): Cascade {
  return new Cascade([bandPass(BANDPASS_CENTRE_HZ, BANDPASS_Q, sampleRateHz)], sampleRateHz)
}

export const MARGINAL_CENTRE_HZ = 1000
export const MARGINAL_Q = 500
export const MARGINAL_GAIN_DB = 12

/** Flat, plus one Q-500 resonance at 1 kHz, 12 dB up, that rings for a third of a second. */
export function marginallyStable(sampleRateHz: number): Cascade {
  return new Cascade([peaking(MARGINAL_CENTRE_HZ, MARGINAL_Q, MARGINAL_GAIN_DB, sampleRateHz)], sampleRateHz)
}

/**
 * The RIAA playback curve — time constants 3180, 318 and 75 µs — normalised to unity at 1 kHz, by
 * the matched-z transform plus one corrective zero on the negative real axis, solved so 20 kHz
 * reads the analog figure at this sample rate. A port of `Biquad.RiaaPlayback`, whose remarks say
 * why it is not the bilinear transform.
 */
export function riaaPlayback(sampleRateHz: number): Biquad {
  const t1 = 3180e-6
  const t2 = 318e-6
  const t3 = 75e-6
  const period = 1 / sampleRateHz

  const zero = Math.exp(-period / t2)
  const pole1 = Math.exp(-period / t1)
  const pole3 = Math.exp(-period / t3)
  const matched: Biquad = { b0: 1, b1: -zero, b2: 0, a1: -(pole1 + pole3), a2: pole1 * pole3 }

  const analogMagnitude = (freqHz: number): number => {
    const w = 2 * Math.PI * freqHz
    const num = Math.hypot(1, w * t2)
    const den = Math.hypot(1, w * t1) * Math.hypot(1, w * t3)
    return num / den
  }
  const mag = (s: Biquad, omega: number) => {
    const r = sectionResponse(s, omega)
    return Math.hypot(r.re, r.im)
  }

  const omega1k = (2 * Math.PI * 1000) / sampleRateHz
  const omega20k = (2 * Math.PI * 20_000) / sampleRateHz
  const wanted = analogMagnitude(20_000) / analogMagnitude(1000)
  const have = mag(matched, omega20k) / mag(matched, omega1k)

  // |1 + a e^(-jw)| / (1 + a) = t  =>  a² (1 − t²) + 2a (cos w − t²) + (1 − t²) = 0.
  const t = wanted / have
  const c = Math.cos(omega20k)
  let a = 0
  if (t < 1) {
    const qa = 1 - t * t
    const qb = 2 * (c - t * t)
    const disc = qb * qb - 4 * qa * qa
    if (disc >= 0) {
      const root = (-qb - Math.sqrt(disc)) / (2 * qa)
      if (root > 0 && root < 1) a = root
    }
  }

  const shaped: Biquad = {
    b0: matched.b0,
    b1: matched.b1 + a * matched.b0,
    b2: a * matched.b1,
    a1: matched.a1,
    a2: matched.a2,
  }
  const g = 1 / mag(shaped, omega1k)
  return { b0: shaped.b0 * g, b1: shaped.b1 * g, b2: shaped.b2 * g, a1: shaped.a1, a2: shaped.a2 }
}

/** A phono preamplifier: the RIAA playback curve, unity at 1 kHz. The one DUT with gain. */
export function phonoRiaa(sampleRateHz: number): Cascade {
  return new Cascade([riaaPlayback(sampleRateHz)], sampleRateHz)
}

export const PROCESSOR_LATENCY_MS = 12
export const PROCESSOR_CORNER_HZ = 20_000

/** A digital processor: a fourth-order Butterworth low-pass at 20 kHz and 12 ms of latency. */
export function digitalProcessor(sampleRateHz: number): Cascade {
  return new Cascade(
    [lowPass(PROCESSOR_CORNER_HZ, 0.5412, sampleRateHz), lowPass(PROCESSOR_CORNER_HZ, 1.3066, sampleRateHz)],
    sampleRateHz,
    null,
    PROCESSOR_LATENCY_MS,
  )
}

// --- Impedance fixtures: `SimulatedImpedanceFixture`'s numbers, and the dividers `SimulatedDut.For`
//     builds from them. Each cascade is H = Z / (R + Z), the junction over the generator side. -----

export const FIXTURE = {
  referenceOhms: 100,
  resistiveOhms: 8,
  reactiveOhms: 6,
  reactiveHenry: 1e-3,
  speakerReOhms: 6.4,
  speakerPeakOhms: 60,
  speakerResonanceHz: 45,
  speakerQ: 2.5,
  capacitiveFarad: 47e-6,
  capacitiveEsrOhms: 0.3,
  shortOhms: 0.12,
  shortHenry: 0.6e-6,
  openStrayFarad: 100e-12,
} as const

export function fixtureResistive(sampleRateHz: number): Cascade {
  const { referenceOhms: r, resistiveOhms } = FIXTURE
  return new Cascade([gain(resistiveOhms / (r + resistiveOhms))], sampleRateHz)
}

/** H(s) = (Rs + sL) / ((R + Rs) + sL). */
export function fixtureReactive(sampleRateHz: number): Cascade {
  const { referenceOhms: r, reactiveOhms: rs, reactiveHenry: l } = FIXTURE
  return new Cascade([fromAnalog(0, l, rs, 0, l, r + rs, sampleRateHz)], sampleRateHz)
}

/** A voice-coil resistance in series with a parallel RLC, seen across the reference resistor: one biquad exactly. */
export function fixtureLoudspeaker(sampleRateHz: number): Cascade {
  const { referenceOhms: r, speakerReOhms: re, speakerPeakOhms: peak, speakerResonanceHz, speakerQ } = FIXTURE
  const w0 = 2 * Math.PI * speakerResonanceHz
  const damping = w0 / speakerQ
  return new Cascade(
    [fromAnalog(re, peak * damping, re * w0 * w0, r + re, (r + peak) * damping, (r + re) * w0 * w0, sampleRateHz)],
    sampleRateHz,
  )
}

/** Z = ESR + 1/(sC): H = (1 + s·ESR·C) / (1 + s (R + ESR) C). */
export function fixtureCapacitive(sampleRateHz: number): Cascade {
  const { referenceOhms: r, capacitiveEsrOhms: esr, capacitiveFarad: c } = FIXTURE
  return new Cascade([fromAnalog(0, esr * c, 1, 0, (r + esr) * c, 1, sampleRateHz)], sampleRateHz)
}

/** The leads shorted: the reactive fixture's form at lead-sized values. */
export function fixtureShort(sampleRateHz: number): Cascade {
  const { referenceOhms: r, shortOhms: rs, shortHenry: l } = FIXTURE
  return new Cascade([fromAnalog(0, l, rs, 0, l, r + rs, sampleRateHz)], sampleRateHz)
}

/** Nothing across the terminals but stray capacitance: H = 1 / (1 + sRC). */
export function fixtureOpen(sampleRateHz: number): Cascade {
  const { referenceOhms: r, openStrayFarad: c } = FIXTURE
  return new Cascade([fromAnalog(0, 0, 1, 0, r * c, 1, sampleRateHz)], sampleRateHz)
}

/**
 * The harmonic stack the simulated *generator* carries on every tone when a linear DUT is
 * selected — `SimulatedSignalProfile.DefaultTriodeHarmonicDbc`: H2 at −46 dBc, H3 at −60, and each
 * order above that 12 dB further down, through H9. Level-independent by construction, which is why
 * every linear model reads the same THD at every drive.
 */
export const GENERATOR_HARMONIC_DBC: readonly number[] = [-46, -60, -72, -84, -96, -108, -120, -132]

/** The white noise floor the simulator adds when its Noise checkbox is on, dBV/√Hz. */
export const NOISE_DENSITY_DBV_PER_ROOT_HZ = -150

/** The simulated mains hum: 50 Hz at this level, then two more harmonics each 6 dB down. */
export const HUM_LEVEL_DBV = -90
export const HUM_HARMONICS = [
  { freqHz: 50, dbv: -90 },
  { freqHz: 100, dbv: -96 },
  { freqHz: 150, dbv: -102 },
] as const
