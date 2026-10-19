import { describe, expect, it } from 'vitest'
import {
  largestSimulationFftSize,
  parseStoredSimulation,
  samplesStartS,
  simulationFit,
  sweepImportBlocker,
} from './simulationAnalysis'

const EL84 = {
  endTimeS: 0.1,
  startTimeS: 0.005,
  nativeRateHz: 1_000_000,
  fftSize: 8192,
  measurementLoFreqHz: 20,
  measurementHiFreqHz: 20_000,
}

describe('simulationFit', () => {
  it('takes the bin width from the duration and not from the FFT size', () => {
    const sizes = [4096, 8192, 16384, 32768]
    const widths = sizes.map((fftSize) => simulationFit({ ...EL84, fftSize }).binWidthHz)

    // The whole point of this measurement: one duration, one resolution, whatever the FFT size.
    for (const width of widths) expect(width).toBeCloseTo(1 / 0.095, 9)

    // What the FFT size does move is the rate, i.e. how much empty spectrum is computed above the
    // band — proportionally, so the ratio is the ratio of the sizes.
    const rates = sizes.map((fftSize) => simulationFit({ ...EL84, fftSize }).analysisRateHz)
    expect(rates[1]! / rates[0]!).toBeCloseTo(2, 9)
    expect(rates[3]! / rates[0]!).toBeCloseTo(8, 9)
  })

  it('reads the reference run as 10.53 Hz bins at 86.2 kHz', () => {
    const fit = simulationFit(EL84)
    expect(fit.usableS).toBeCloseTo(0.095, 9)
    expect(fit.binWidthHz).toBeCloseTo(10.5263, 3)
    expect(fit.analysisRateHz).toBeCloseTo(8192 / 0.095, 6)
    expect(fit.nyquistHz).toBeCloseTo(8192 / 0.095 / 2, 6)
  })

  it('says when the derived Nyquist falls short of the band', () => {
    // 1024 points over 95 ms is a 10.8 kHz rate, i.e. 5.4 kHz of spectrum against a 20 kHz band.
    expect(simulationFit({ ...EL84, fftSize: 1024 }).nyquistTooLow).toBe(true)
    expect(simulationFit(EL84).nyquistTooLow).toBe(false)
  })

  it('bounds the FFT size by the samples actually available', () => {
    // 95 000 samples: 65536 fits, 131072 does not.
    expect(simulationFit(EL84).maxFftSize).toBe(65536)
    expect(simulationFit({ ...EL84, fftSize: 65536 }).fftTooLarge).toBe(false)
    expect(simulationFit({ ...EL84, fftSize: 131072 }).fftTooLarge).toBe(true)
  })

  it('counts the available samples from the start time, not from the whole run', () => {
    // Analysing from 95 ms of a 100 ms run leaves 5000 samples, so 4096 is the ceiling.
    expect(simulationFit({ ...EL84, startTimeS: 0.095 }).maxFftSize).toBe(4096)
  })

  it('grades the resolution against the bottom of the band, which is what binds', () => {
    // 10.53 Hz bins: 20 Hz is 1.9 bins in — too coarse — while 1 kHz is ninety-five.
    expect(simulationFit(EL84).resolution).toBe('tooCoarse')
    expect(simulationFit({ ...EL84, measurementLoFreqHz: 1000 }).resolution).toBe('ok')

    // Between the two thresholds: 30 Hz is 2.85 bins in.
    expect(simulationFit({ ...EL84, measurementLoFreqHz: 30 }).resolution).toBe('coarse')

    // And what it would take: four bins below 20 Hz is 200 ms saved.
    expect(simulationFit(EL84).neededMs).toBeCloseTo(200, 6)
  })

  it('does not grade a run with no duration as coarse', () => {
    // Nothing to analyse is its own refusal elsewhere; it is not a resolution complaint.
    const fit = simulationFit({ ...EL84, startTimeS: EL84.endTimeS })
    expect(fit.usableS).toBe(0)
    expect(fit.resolution).toBe('ok')
    expect(fit.binWidthHz).toBe(0)
  })

  it('never offers an FFT size below the floor the server accepts', () => {
    expect(largestSimulationFftSize(0, 1_000_000)).toBe(1024)
    expect(largestSimulationFftSize(1e-6, 1_000_000)).toBe(1024)
  })
})

/** A stored result's params_json, in the shape `SimulationAnalysis.ToParameters` writes. */
function paramsJson(overrides: Record<string, unknown> = {}, simulation: Record<string, unknown> = {}): string {
  return JSON.stringify({
    fftSize: 8192,
    window: 'Hann',
    measurementLoFreqHz: 20,
    measurementHiFreqHz: 20000,
    sampleRateHz: 86231.58,
    ...overrides,
    simulation: {
      startTimeS: 0.005,
      levelOffsetDb: -6,
      periodicRecord: true,
      removedDcV: [0.0104],
      pcmStartS: 0.005,
      source: {
        path: 'C:\\sims\\EL84.raw',
        title: '* EL84 PP',
        plotname: 'Transient Analysis',
        leftTrace: 'V(out)',
        leftUnit: 'V',
        rightTrace: null,
        rightUnit: null,
        nativeRateHz: 1_000_000,
        pointCount: 95004,
        sourceStartS: 0.005,
        sourceEndS: 0.1,
        warnings: ['This run saves 95 ms'],
      },
      ...simulation,
    },
  })
}

describe('parseStoredSimulation', () => {
  it('reads the settings a re-analysis prefills from', () => {
    const stored = parseStoredSimulation(paramsJson())!

    expect(stored.startTimeS).toBeCloseTo(0.005, 9)
    expect(stored.levelOffsetDb).toBe(-6)
    expect(stored.periodicRecord).toBe(true)
    expect(stored.fftSize).toBe(8192)
    expect(stored.window).toBe('Hann')
    expect(stored.measurementHiFreqHz).toBe(20000)
    expect(stored.source.leftTrace).toBe('V(out)')
    expect(stored.source.leftUnit).toBe('V')
    expect(stored.source.sourceEndS).toBeCloseTo(0.1, 9)
    expect(stored.removedDcV).toEqual([0.0104])
  })

  it('is null for a measured capture rather than a guess at one', () => {
    expect(parseStoredSimulation(JSON.stringify({ fftSize: 65536, window: 'Hann' }))).toBeNull()
    expect(parseStoredSimulation(null)).toBeNull()
    expect(parseStoredSimulation('')).toBeNull()
  })

  it('survives a params_json this build cannot read', () => {
    // A result the user has merely clicked on must not fail on its own stored parameters.
    expect(parseStoredSimulation('{ not json')).toBeNull()
    expect(parseStoredSimulation('null')).toBeNull()
  })

  describe('where the samples begin', () => {
    it('is the recorded samples start once an analysis has moved forward of it', () => {
      const stored = parseStoredSimulation(paramsJson({}, { startTimeS: 0.05, pcmStartS: 0.005 }))!
      expect(stored.startTimeS).toBeCloseTo(0.05, 9)
      expect(samplesStartS(stored)).toBeCloseTo(0.005, 9)
    })

    it('falls back to the analysis start on a row written before it was recorded', () => {
      // Null there does not mean zero: it means the two were equal, which is what the import
      // writes. Reading it as zero would offer a start time whose samples do not exist.
      const stored = parseStoredSimulation(paramsJson({}, { startTimeS: 0.02, pcmStartS: undefined }))!
      expect(stored.pcmStartS).toBeNull()
      expect(samplesStartS(stored)).toBeCloseTo(0.02, 9)
    })
  })
})

describe('sweepImportBlocker', () => {
  // The report: a single 245 ms EL84 transient, opened on a THD-vs-frequency measurement, greyed
  // the Import button and said nothing. One run cannot be a curve, and the dialog draws no list of
  // runs for a file with no `.step`, so a sentence is the only place the reason can go.
  it('says why a file with no steps cannot be a sweep, and names the screen that takes it', () => {
    const why = sweepImportBlocker(true, 1, 1)
    expect(why).not.toBeNull()
    expect(why).toContain('.step')
    expect(why).toContain('FFT analysis')
  })

  it('asks for two ticks once the file does hold steps', () => {
    expect(sweepImportBlocker(true, 51, 1)).toContain('at least two')
    expect(sweepImportBlocker(true, 51, 0)).toContain('at least two')
    expect(sweepImportBlocker(true, 51, 2)).toBeNull()
  })

  // Every other screen that takes a transient makes one spectrum per run, so one run is the
  // ordinary case there and nothing is refused.
  it('blocks nothing where the runs are not being made into a sweep', () => {
    expect(sweepImportBlocker(false, 1, 1)).toBeNull()
    expect(sweepImportBlocker(false, 51, 0)).toBeNull()
  })
})
