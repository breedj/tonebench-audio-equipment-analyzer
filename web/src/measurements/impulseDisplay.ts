import { parseAnnotations, type ChartAnnotation } from './chartAnnotations'
import type {
  BurstDecayResolutionName,
  DecayChartMode,
  DecayPalette,
  ImpulseResponseParameters,
  ResponseDelayView,
} from '../api/client'
import { isDelayView } from '../response/delayAxis'
import {
  DECAY_DEPTH_PERCENT,
  DECAY_ROTATION_DEG,
  DEFAULT_CSD_ANALYSIS,
  csdSpanMs,
  type CsdAnalysis,
} from './decay'

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
  /** The gated response's delay curve and its axis window in µs — see ResponseDisplay. */
  delayView: ResponseDelayView
  delayTopUs: number
  delayBottomUs: number
  freqFromHz: number
  freqToHz: number
  /** The two decay views: how the surface is drawn, and how each is analysed. */
  decayMode: DecayChartMode
  decayPalette: DecayPalette
  decayColoredCurves: boolean
  /** Fill between the curves, and draw them in one colour. See DecaySurfaceChart. */
  decayFilled: boolean
  /** The oblique projection: the lean in degrees and the depth as a percentage of the plot. */
  decayRotationDeg: number
  decayDepthPercent: number
  decayRangeDb: number
  csd: CsdAnalysis
  burstMaxPeriods: number
  burstPointsPerPeriod: number
  burstResolution: BurstDecayResolutionName
  /**
   * The notes drawn on this measurement's charts, as stored -- across every view, so one report
   * render can print a table per picture. See measurements/chartAnnotations.ts.
   */
  annotations: ChartAnnotation[]
}

/**
 * What a fresh impulse measurement shows before anything is stored — the literals App.vue starts
 * from. It took the sample rate until 2026-10-24, when the CSD analysis stopped being derived from
 * it; see DEFAULT_CSD_ANALYSIS.
 */
export function defaultImpulseDisplay(): ImpulseDisplay {
  return {
    annotations: [],
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
    delayView: 'None',
    delayTopUs: 1000,
    delayBottomUs: -1000,
    freqFromHz: 20,
    // The whole span a 96 kHz measurement covers, not the audio band: the decay charts are the ones
    // read on this screen, and clipping them at 20 kHz hides the top two thirds of what was measured.
    freqToHz: 96000,
    decayMode: 'Waterfall',
    decayPalette: 'Jet',
    decayColoredCurves: true,
    // On: a filled surface is the picture a decay is read for, and the stack of bare curves is the
    // one you drop to when a single curve's shape is the question. `decayColoredCurves` is left at
    // its own default because it decides what the *unfilled* stack looks like, which is a separate
    // answer and the one that applies once this is turned off.
    decayFilled: true,
    decayRotationDeg: DECAY_ROTATION_DEG.default,
    decayDepthPercent: DECAY_DEPTH_PERCENT.default,
    decayRangeDb: 30,
    csd: { ...DEFAULT_CSD_ANALYSIS },
    burstMaxPeriods: 30,
    burstPointsPerPeriod: 8,
    burstResolution: 'PreferFrequency',
  }
}

/** The same thing as a shared constant, for callers with nothing of their own to start from. */
export const DEFAULT_IMPULSE_DISPLAY: Readonly<ImpulseDisplay> = Object.freeze(defaultImpulseDisplay())

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
  if (isDelayView(stored.chartDelayView)) display.delayView = stored.chartDelayView
  if (typeof stored.chartDelayTopUs === 'number' && Number.isFinite(stored.chartDelayTopUs)) display.delayTopUs = stored.chartDelayTopUs
  if (typeof stored.chartDelayBottomUs === 'number' && Number.isFinite(stored.chartDelayBottomUs)) display.delayBottomUs = stored.chartDelayBottomUs
  if (typeof stored.chartFreqFromHz === 'number' && stored.chartFreqFromHz > 0) display.freqFromHz = stored.chartFreqFromHz
  if (typeof stored.chartFreqToHz === 'number' && stored.chartFreqToHz > 0) display.freqToHz = stored.chartFreqToHz
  if (stored.chartDecayMode) display.decayMode = stored.chartDecayMode
  if (stored.chartDecayPalette) display.decayPalette = stored.chartDecayPalette
  if (typeof stored.chartDecayColoredCurves === 'boolean') display.decayColoredCurves = stored.chartDecayColoredCurves
  if (typeof stored.chartDecayFilled === 'boolean') display.decayFilled = stored.chartDecayFilled
  if (typeof stored.chartDecayRotationDeg === 'number' && Number.isFinite(stored.chartDecayRotationDeg)) {
    display.decayRotationDeg = stored.chartDecayRotationDeg
  }
  if (typeof stored.chartDecayDepthPercent === 'number' && stored.chartDecayDepthPercent > 0) {
    display.decayDepthPercent = stored.chartDecayDepthPercent
  }
  if (typeof stored.chartDecayRangeDb === 'number' && stored.chartDecayRangeDb > 0) display.decayRangeDb = stored.chartDecayRangeDb
  if (typeof stored.chartCsdFftLength === 'number') display.csd.fftLength = stored.chartCsdFftLength
  // The span and the slice count, or — for a type stored before the controls were said in
  // milliseconds — the shift and block count they replaced, converted at that measurement's own
  // rate. The rate is the stored span doubled, which is where every other reader of these params
  // gets it from.
  if (typeof stored.chartCsdSlices === 'number') display.csd.slices = stored.chartCsdSlices
  else if (typeof stored.chartCsdBlockCount === 'number') display.csd.slices = stored.chartCsdBlockCount

  if (typeof stored.chartCsdStartBeforePeakMs === 'number') {
    display.csd.startBeforePeakMs = stored.chartCsdStartBeforePeakMs
  }

  if (typeof stored.chartCsdSpanMs === 'number') {
    display.csd.spanMs = stored.chartCsdSpanMs
  } else if (typeof stored.chartCsdBlockShiftSamples === 'number' && typeof stored.frequencySpanHz === 'number') {
    display.csd.spanMs = csdSpanMs(stored.chartCsdBlockShiftSamples, display.csd.slices, stored.frequencySpanHz * 2)
  }
  if (stored.chartCsdWindowMode) display.csd.windowMode = stored.chartCsdWindowMode
  if (typeof stored.chartCsdRiseTimeMs === 'number') display.csd.riseTimeMs = stored.chartCsdRiseTimeMs
  if (typeof stored.chartBurstMaxPeriods === 'number') display.burstMaxPeriods = stored.chartBurstMaxPeriods
  if (typeof stored.chartBurstPointsPerPeriod === 'number') display.burstPointsPerPeriod = stored.chartBurstPointsPerPeriod
  if (stored.chartBurstResolution) display.burstResolution = stored.chartBurstResolution

  display.annotations = parseAnnotations(
    (stored as { chartAnnotations?: unknown }).chartAnnotations,
    { view: 'Impulse', xQuantity: 'ms', yQuantity: 'volt' },
  )

  return display
}
