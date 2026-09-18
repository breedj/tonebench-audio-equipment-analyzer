import type {
  BurstDecayResolutionName,
  DecayChartMode,
  DecayPalette,
  ImpulseResponseParameters,
} from '../api/client'
import { defaultCsdAnalysis, type CsdAnalysis } from './decay'

/**
 * The impulse screen's display state — what each of its five views' charts takes that is a
 * preference rather than data, and the two decay analyses' settings. App.vue holds these as refs;
 * the report builds one from a stored measurement type's `params_json` to render the same charts
 * headless, and both go through `impulseDisplayFromParams`.
 */
export interface ImpulseDisplay {
  /** Impulse view. */
  normalised: boolean
  thickLines: boolean
  showDataPoints: boolean
  timeFromMs: number
  timeToMs: number
  /** The normalised impulse view's level window, dB below the impulse's own peak. */
  yTopDb: number
  yBottomDb: number
  /** Response and harmonics views. */
  showPhase: boolean
  showCoherence: boolean
  responseYTopDb: number
  responseYBottomDb: number
  harmonicsYTopDb: number
  harmonicsYBottomDb: number
  phaseTopDeg: number
  phaseBottomDeg: number
  freqFromHz: number
  freqToHz: number
  /** The two decay views: how the surface is drawn, and how each is analysed. */
  decayMode: DecayChartMode
  decayPalette: DecayPalette
  decayColoredCurves: boolean
  decayRangeDb: number
  csd: CsdAnalysis
  burstMaxPeriods: number
  burstPointsPerPeriod: number
  burstResolution: BurstDecayResolutionName
}

/**
 * What a fresh impulse measurement shows before anything is stored — the literals App.vue starts
 * from. The CSD analysis follows the sample rate (its settings are in samples), so the rate the
 * result was taken at is the one argument.
 */
export function defaultImpulseDisplay(sampleRateHz: number): ImpulseDisplay {
  return {
    normalised: false,
    thickLines: true,
    showDataPoints: false,
    timeFromMs: -1,
    timeToMs: 20,
    yTopDb: 0,
    yBottomDb: -60,
    showPhase: false,
    showCoherence: false,
    responseYTopDb: 0,
    responseYBottomDb: -120,
    harmonicsYTopDb: 0,
    harmonicsYBottomDb: -120,
    phaseTopDeg: 180,
    phaseBottomDeg: -180,
    freqFromHz: 20,
    freqToHz: 20000,
    decayMode: 'Waterfall',
    decayPalette: 'Jet',
    decayColoredCurves: true,
    decayRangeDb: 30,
    csd: defaultCsdAnalysis(sampleRateHz),
    burstMaxPeriods: 30,
    burstPointsPerPeriod: 8,
    burstResolution: 'PreferFrequency',
  }
}

/** For callers that have no rate to hand — the 48 kHz defaults App.vue itself starts from. */
export const DEFAULT_IMPULSE_DISPLAY: Readonly<ImpulseDisplay> = Object.freeze(defaultImpulseDisplay(48000))

/** The display state an impulse type's `params_json` restores, over `base`; guarded field by field like the others. */
export function impulseDisplayFromParams(
  paramsJson: string | null | undefined,
  base: Readonly<ImpulseDisplay> = DEFAULT_IMPULSE_DISPLAY,
): ImpulseDisplay {
  const display: ImpulseDisplay = { ...base, csd: { ...base.csd } }
  if (!paramsJson) return display

  let stored: Partial<ImpulseResponseParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<ImpulseResponseParameters>
  } catch {
    return display
  }
  if (!stored || typeof stored !== 'object') return display

  if (typeof stored.chartNormalisedImpulse === 'boolean') display.normalised = stored.chartNormalisedImpulse
  if (typeof stored.chartThickLines === 'boolean') display.thickLines = stored.chartThickLines
  if (typeof stored.chartShowDataPoints === 'boolean') display.showDataPoints = stored.chartShowDataPoints
  if (typeof stored.chartYTopDb === 'number') display.yTopDb = stored.chartYTopDb
  if (typeof stored.chartYBottomDb === 'number') display.yBottomDb = stored.chartYBottomDb
  if (typeof stored.chartTimeFromMs === 'number') display.timeFromMs = stored.chartTimeFromMs
  if (typeof stored.chartTimeToMs === 'number') display.timeToMs = stored.chartTimeToMs
  if (typeof stored.chartShowPhase === 'boolean') display.showPhase = stored.chartShowPhase
  if (typeof stored.chartShowCoherence === 'boolean') display.showCoherence = stored.chartShowCoherence
  if (typeof stored.chartResponseYTopDb === 'number') display.responseYTopDb = stored.chartResponseYTopDb
  if (typeof stored.chartResponseYBottomDb === 'number') display.responseYBottomDb = stored.chartResponseYBottomDb
  if (typeof stored.chartHarmonicsYTopDb === 'number') display.harmonicsYTopDb = stored.chartHarmonicsYTopDb
  if (typeof stored.chartHarmonicsYBottomDb === 'number') display.harmonicsYBottomDb = stored.chartHarmonicsYBottomDb
  if (typeof stored.chartPhaseTopDeg === 'number') display.phaseTopDeg = stored.chartPhaseTopDeg
  if (typeof stored.chartPhaseBottomDeg === 'number') display.phaseBottomDeg = stored.chartPhaseBottomDeg
  if (typeof stored.chartFreqFromHz === 'number' && stored.chartFreqFromHz > 0) display.freqFromHz = stored.chartFreqFromHz
  if (typeof stored.chartFreqToHz === 'number' && stored.chartFreqToHz > 0) display.freqToHz = stored.chartFreqToHz
  if (stored.chartDecayMode) display.decayMode = stored.chartDecayMode
  if (stored.chartDecayPalette) display.decayPalette = stored.chartDecayPalette
  if (typeof stored.chartDecayColoredCurves === 'boolean') display.decayColoredCurves = stored.chartDecayColoredCurves
  if (typeof stored.chartDecayRangeDb === 'number' && stored.chartDecayRangeDb > 0) display.decayRangeDb = stored.chartDecayRangeDb
  if (typeof stored.chartCsdFftLength === 'number') display.csd.fftLength = stored.chartCsdFftLength
  if (typeof stored.chartCsdBlockShiftSamples === 'number') display.csd.blockShiftSamples = stored.chartCsdBlockShiftSamples
  if (typeof stored.chartCsdBlockCount === 'number') display.csd.blockCount = stored.chartCsdBlockCount
  if (stored.chartCsdWindowMode) display.csd.windowMode = stored.chartCsdWindowMode
  if (typeof stored.chartCsdRiseTimeMs === 'number') display.csd.riseTimeMs = stored.chartCsdRiseTimeMs
  if (typeof stored.chartBurstMaxPeriods === 'number') display.burstMaxPeriods = stored.chartBurstMaxPeriods
  if (typeof stored.chartBurstPointsPerPeriod === 'number') display.burstPointsPerPeriod = stored.chartBurstPointsPerPeriod
  if (stored.chartBurstResolution) display.burstResolution = stored.chartBurstResolution

  return display
}
