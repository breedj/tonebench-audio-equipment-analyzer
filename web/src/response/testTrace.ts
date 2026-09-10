import type { ResponseTrace } from '../composables/useFrequencyResponse'

/**
 * A `ResponseTrace` built from explicit columns, for tests.
 *
 * Kept out of the test files themselves because both `complexPlane.test.ts` and `margins.test.ts`
 * need it and the bandwidth scalars are pure noise to either of them.
 */
export function makeTrace(over: {
  freqHz: number[]
  magnitudeMaxDb: number[]
  magnitudeMinDb?: number[]
  phaseDeg: number[]
  coherence?: number[]
}): ResponseTrace {
  const bandwidth = { dropDb: 3, symmetric: false, lowerHz: 0, upperHz: 0, bandwidthHz: 0, lowerClamped: false, upperClamped: false }
  return {
    kind: 'Gain',
    startFreqHz: over.freqHz[0] ?? 0,
    endFreqHz: over.freqHz[over.freqHz.length - 1] ?? 0,
    freqHz: over.freqHz,
    // Defaults to min === max, which is what a stepped run genuinely produces.
    magnitudeMinDb: over.magnitudeMinDb ?? over.magnitudeMaxDb,
    magnitudeMaxDb: over.magnitudeMaxDb,
    phaseDeg: over.phaseDeg,
    coherence: over.coherence ?? over.freqHz.map(() => 1),
    oneDb: bandwidth,
    threeDb: bandwidth,
    peakDb: 0,
    peakFreqHz: 0,
    referenceDb: 0,
    referenceMode: 'BandMedian',
    referenceFellBackToPeak: false,
  }
}

/**
 * A three-pole low pass, `L(f) = K / (1 + jf/fc)³`, sampled on a log grid.
 *
 * Chosen as the margin fixture because both margins have closed forms:
 *
 * - phase crossover where `3·atan(f/fc) = 180°`, i.e. `f = fc·tan(60°) = fc·√3`, where
 *   `(1 + (f/fc)²)^{3/2} = 8`, so `GM = 20·log10(8/K)` exactly;
 * - gain crossover where `(1 + (f/fc)²)^{3/2} = K`, i.e. `f = fc·√(K^{2/3} − 1)`, and
 *   `PM = 180° − 3·atan(f/fc)`.
 *
 * The phase is naturally monotonic from 0° to −270°, so it needs no unwrapping and the fixture
 * cannot accidentally depend on one.
 */
export function threePole(K: number, fcHz: number, freqHz: number[]) {
  const magnitudeMaxDb: number[] = []
  const phaseDeg: number[] = []

  for (const f of freqHz) {
    const x = f / fcHz
    magnitudeMaxDb.push(20 * Math.log10(K) - 30 * Math.log10(1 + x * x))
    phaseDeg.push((-3 * Math.atan(x) * 180) / Math.PI)
  }

  return makeTrace({ freqHz, magnitudeMaxDb, phaseDeg })
}

/** The closed-form margins of `threePole`, computed independently of anything under test. */
export function threePoleExpected(K: number, fcHz: number) {
  const gainCrossoverHz = fcHz * Math.sqrt(Math.pow(K, 2 / 3) - 1)
  const phaseAtGc = (-3 * Math.atan(gainCrossoverHz / fcHz) * 180) / Math.PI
  return {
    gainCrossoverHz,
    phaseMarginDeg: 180 + phaseAtGc,
    phaseCrossoverHz: fcHz * Math.sqrt(3),
    gainMarginDb: 20 * Math.log10(8 / K),
  }
}

/** A log-spaced grid, `perOctave` points per octave from `fromHz` up to and including `toHz`. */
export function logGrid(fromHz: number, toHz: number, perOctave: number): number[] {
  const out: number[] = []
  const step = Math.pow(2, 1 / perOctave)
  for (let f = fromHz; f < toHz; f *= step) out.push(f)
  out.push(toHz)
  return out
}
