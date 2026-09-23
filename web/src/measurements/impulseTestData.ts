import type { HarmonicCurveJson, ImpulseTraceJson } from '../api/client'

/**
 * Impulse-response fixtures for tests: a decimated impulse trace and harmonic curves in the shapes
 * the wire and the stored endpoints use. Shared by the readings tests and the report builder's, so
 * both assert against the same numbers.
 */
export function impulseTestTrace(over: Partial<ImpulseTraceJson> = {}): ImpulseTraceJson {
  return {
    kind: 'Left',
    timeS: [-0.005, 0, 0.005, 0.01],
    minVolts: [-0.001, 0.9, -0.05, -0.002],
    maxVolts: [0.001, 0.954, 0.05, 0.002],
    peakVolts: 0.954,
    sampleRateHz: 192000,
    fullSampleCount: 4032,
    gatePreMs: 5,
    gatePostMs: 16,
    // 1 / (5 + 16) ms.
    resolutionLimitHz: 47.6,
    ...over,
  }
}

/**
 * One harmonic order over a log grid from 20 Hz to its ceiling, at a level that falls 6 dB per
 * decade — arbitrary, but monotone, so a test can say which point a lookup landed on.
 */
export function harmonicTestCurve(order: number, upperValidHz: number, levelAt1kDbc: number): HarmonicCurveJson {
  const fundamentalHz: number[] = []
  const levelDbc: number[] = []
  for (let f = 20; f <= upperValidHz; f *= 2 ** (1 / 6)) {
    fundamentalHz.push(Number(f.toFixed(4)))
    levelDbc.push(levelAt1kDbc - 6 * Math.log10(f / 1000))
  }
  return { order, fundamentalHz, levelDbc, upperValidHz }
}
