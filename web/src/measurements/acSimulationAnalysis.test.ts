import { describe, expect, it } from 'vitest'
import {
  acSimulationFit,
  hasReferenceCurve,
  MAX_REFERENCE_HZ,
  parseStoredAcSimulation,
  referenceBandError,
  sweepLabel,
} from './acSimulationAnalysis'

/** The EL84 reference sweep: `.ac dec 100 0.1 5Meg`, 771 points over 7.7 decades. */
const EL84 = {
  startFreqHz: 0.1,
  endFreqHz: 5e6,
  pointCount: 771,
  pointsPerDecade: 100,
  isLogSpaced: true,
  analysisLowHz: 20,
  analysisHighHz: 20_000,
}

describe('acSimulationFit', () => {
  it('reads the reference sweep as 7.7 decades at 2.3 % a step', () => {
    const fit = acSimulationFit(EL84)
    expect(fit.decades).toBeCloseTo(7.699, 3)
    expect(fit.stepPercent).toBeCloseTo(2.329, 3)
    expect(fit.density).toBe('ok')
    expect(fit.bandNotCovered).toBe(false)
  })

  it('calls a linear sweep out, because a log chart draws almost none of it', () => {
    expect(acSimulationFit({ ...EL84, isLogSpaced: false }).density).toBe('linear')
  })

  it('calls a sparse sweep out, and says how coarse it is', () => {
    // dec 5 is 58 % between points, which locates a -3 dB corner no better than that.
    const fit = acSimulationFit({ ...EL84, pointsPerDecade: 5 })
    expect(fit.density).toBe('sparse')
    expect(fit.stepPercent).toBeCloseTo(58.49, 2)
  })

  it('says when the sweep does not reach across the band being analysed', () => {
    expect(acSimulationFit({ ...EL84, endFreqHz: 10_000 }).bandNotCovered).toBe(true)
    expect(acSimulationFit({ ...EL84, startFreqHz: 100 }).bandNotCovered).toBe(true)
    expect(acSimulationFit(EL84).bandNotCovered).toBe(false)
  })

  it('reports a band that is not a band, and does not also call it uncovered', () => {
    const fit = acSimulationFit({ ...EL84, analysisLowHz: 20_000, analysisHighHz: 20 })
    expect(fit.bandInvalid).toBe(true)
    expect(fit.bandNotCovered).toBe(false)
  })
})

describe('referenceBandError', () => {
  it('accepts an ordinary passband reference', () => {
    expect(referenceBandError('BandMedian', 200, 2000)).toBeNull()
  })

  it('caps the band at the analyzer’s own ceiling, which a simulated sweep can run past', () => {
    // The sweep reaches 5 MHz; the band the reference level is taken over cannot.
    expect(referenceBandError('BandMedian', 200, 5e6)).toContain(MAX_REFERENCE_HZ.toLocaleString())
    expect(referenceBandError('BandMedian', 200, MAX_REFERENCE_HZ)).toBeNull()
  })

  it('refuses a band that ends where it starts', () => {
    expect(referenceBandError('BandMedian', 2000, 2000)).toContain('above where it starts')
  })

  it('checks nothing under a peak reference, whose edges are carried but never read', () => {
    expect(referenceBandError('Peak', 5e6, 1)).toBeNull()
  })
})

/** A stored result's params_json, in the shape `AcSimulationAnalysis.ToParameters` writes. */
function paramsJson(
  overrides: Record<string, unknown> = {},
  simulation: Record<string, unknown> = {},
  source: Record<string, unknown> = {},
): string {
  return JSON.stringify({
    frequencySpanHz: 5e6,
    resolutionMultiplier: 1,
    levelMode: 'NotApplicable',
    levelValue: 0,
    levelUnit: '',
    rightChannelIsReference: true,
    smoothingDenominator: 0,
    averageCount: 1,
    bandwidthReference: 'BandMedian',
    bandwidthReferenceLowHz: 200,
    bandwidthReferenceHighHz: 2000,
    ...overrides,
    acSimulation: {
      levelOffsetDb: 0,
      analysisLowHz: 20,
      analysisHighHz: 20000,
      ...simulation,
      source: {
        path: 'C:\\sims\\EL84.raw',
        title: '* EL84 PP',
        plotname: 'AC Analysis',
        outputTrace: 'V(out)',
        outputUnit: 'V',
        referenceTrace: 'V(in)',
        referenceUnit: 'V',
        pointCount: 771,
        startFreqHz: 0.1,
        endFreqHz: 5e6,
        pointsPerDecade: 100,
        warnings: [],
        ...source,
      },
    },
  })
}

describe('parseStoredAcSimulation', () => {
  it('reads the settings a re-analysis prefills from', () => {
    const stored = parseStoredAcSimulation(paramsJson())!

    expect(stored.analysisLowHz).toBe(20)
    expect(stored.analysisHighHz).toBe(20000)
    expect(stored.levelOffsetDb).toBe(0)
    expect(stored.bandwidthReference).toBe('BandMedian')
    expect(stored.bandwidthReferenceLowHz).toBe(200)
    expect(stored.rightChannelIsReference).toBe(true)
    expect(stored.source.outputTrace).toBe('V(out)')
    expect(stored.source.referenceTrace).toBe('V(in)')
    expect(stored.source.pointsPerDecade).toBe(100)
  })

  it('is null for a measured sweep rather than a guess at one', () => {
    // A chirp's params_json has every field this one does except the block that matters.
    expect(
      parseStoredAcSimulation(
        JSON.stringify({ frequencySpanHz: 96000, resolutionMultiplier: 4, smoothingDenominator: 24 }),
      ),
    ).toBeNull()
    expect(parseStoredAcSimulation(null)).toBeNull()
    expect(parseStoredAcSimulation('')).toBeNull()
  })

  it('survives a params_json this build cannot read', () => {
    expect(parseStoredAcSimulation('{ not json')).toBeNull()
    expect(parseStoredAcSimulation('null')).toBeNull()
  })

  it('knows when there is no second curve to divide by', () => {
    const one = parseStoredAcSimulation(paramsJson({ rightChannelIsReference: false }, {}, { referenceTrace: null }))!
    const two = parseStoredAcSimulation(paramsJson())!

    expect(hasReferenceCurve(one)).toBe(false)
    expect(hasReferenceCurve(two)).toBe(true)
  })
})

describe('sweepLabel', () => {
  it('uses the simulator own words when the log gave any', () => {
    expect(sweepLabel({ index: 0, label: 'freq=1000' })).toBe('freq=1000')
    expect(sweepLabel({ index: 7, label: 'ra=29000' })).toBe('ra=29000')
  })

  // Every QSpice sweep arrives here: it prints its `.step` lines to the console and writes no log.
  it('numbers a sweep the log could not name, from one', () => {
    expect(sweepLabel({ index: 0, label: null })).toBe('Step 1')
    expect(sweepLabel({ index: 2, label: null })).toBe('Step 3')
    expect(sweepLabel({ index: 1, label: '   ' })).toBe('Step 2')
  })
})
