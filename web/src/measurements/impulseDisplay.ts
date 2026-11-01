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
  DEFAULT_CSD_ANALYSIS,
  csdSpanMs,
  decayTopHz,
  type CsdAnalysis,
} from './decay'
import { decayViewDefaults, explodeZForAxisLengths, magnificationFromStored } from './decay3d'
import { allHarmonicOrders, parseHarmonicOrders } from './harmonicOrders'

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
  /** Which harmonic orders the Harmonics view draws — the rail's H2…H5 checkboxes. */
  harmonicOrders: number[]
  phaseTopDeg: number
  phaseBottomDeg: number
  /** The gated response's delay curve and its axis window in µs — see ResponseDisplay. */
  delayView: ResponseDelayView
  delayTopUs: number
  delayBottomUs: number
  freqFromHz: number
  freqToHz: number
  /**
   * The two decay views' own top of band, where every other view on this screen shares
   * {@link freqToHz}. See `DECAY_BAND_EDGE_FRACTION`, which is the whole of why it is separate:
   * the last stretch below Nyquist rings rather than decaying and draws as a wall, and that is
   * true of a decay surface and of nothing else on this screen.
   */
  decayFreqToHz: number
  /** The two decay views: how the surface is drawn, and how each is analysed. */
  decayMode: DecayChartMode
  decayPalette: DecayPalette
  decayColoredCurves: boolean
  /** Fill between the curves, and draw them in one colour. See DecaySurfaceChart. */
  decayFilled: boolean
  /**
   * Rule the 3D surface with lines along its cell edges — one setting per quantity since
   * 2026-11-01, because the two draw different pictures: along frequency is one curve per slice,
   * the spectrum at that moment, and along time is one rake per column, how that frequency
   * decays. Both off by default; there are twenty-odd thousand cells on a cumulative spectral
   * decay and ruling every edge of all of them is a wash rather than a mesh. Read by the 3D chart
   * alone.
   */
  decayMeshFrequency: boolean
  decayMeshTime: boolean
  /** The oblique projection: the lean in degrees and the depth as a percentage of the plot. */
  decayRotationDeg: number
  decayDepthPercent: number
  /**
   * The 3D surface's own view: where it sits and how large it is drawn. Stored so a view that took
   * some turning can be kept, which is the whole reason they are settings rather than only a drag.
   * Read by that chart alone — the Canvas 2D one has no camera.
   */
  decayMoveX: number
  decayMoveY: number
  decayMagnification: number
  /** How tall the surface's box is drawn against its own floor; see DECAY_EXPLODE_Z. */
  decayExplodeZ: number
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
    harmonicOrders: allHarmonicOrders(),
    phaseTopDeg: 180,
    phaseBottomDeg: -180,
    delayView: 'None',
    delayTopUs: 1000,
    delayBottomUs: -1000,
    freqFromHz: 20,
    // The whole span a 96 kHz measurement covers, not the audio band: the Bode and harmonics views
    // are read across everything that was measured, and clipping them at 20 kHz would hide the top
    // two thirds of it.
    freqToHz: 96000,
    // The decay views stop short of the band edge; `impulseDisplayFromParams` re-derives this from
    // whatever span the measurement was actually taken at, and this is the figure for the default.
    decayFreqToHz: decayTopHz(DEFAULT_SPAN_HZ),
    decayMode: 'Waterfall',
    decayPalette: 'Jet',
    decayColoredCurves: true,
    // On: a filled surface is the picture a decay is read for, and the stack of bare curves is the
    // one you drop to when a single curve's shape is the question. `decayColoredCurves` is left at
    // its own default because it decides what the *unfilled* stack looks like, which is a separate
    // answer and the one that applies once this is turned off.
    decayFilled: true,
    decayMeshFrequency: false,
    decayMeshTime: false,
    // The six view sliders, spread rather than listed, so that a new measurement and the Surface
    // view panel's Reset view button cannot come to disagree about what the default view is.
    ...decayViewDefaults(),
    decayRangeDb: 30,
    csd: { ...DEFAULT_CSD_ANALYSIS },
    burstMaxPeriods: 30,
    burstPointsPerPeriod: 8,
    burstResolution: 'PreferFrequency',
  }
}

/**
 * The span a new impulse measurement is created at, mirroring
 * `MeasurementSaveService.DefaultImpulseResponseParametersJson`. It is here only to give
 * {@link defaultImpulseDisplay} a Nyquist to derive the decay views' top of band from, for a
 * measurement whose own params say nothing.
 */
const DEFAULT_SPAN_HZ = 96000

/** The same thing as a shared constant, for callers with nothing of their own to start from. */
export const DEFAULT_IMPULSE_DISPLAY: Readonly<ImpulseDisplay> = Object.freeze(defaultImpulseDisplay())

/** The display state an impulse type's `params_json` restores, over `base`; guarded field by field like the others. */
export function impulseDisplayFromParams(
  paramsJson: string | null | undefined,
  base: Readonly<ImpulseDisplay> = DEFAULT_IMPULSE_DISPLAY,
): ImpulseDisplay {
  // Both of the fields that are objects are copied, not shared: a caller that edits what comes
  // back must not reach into `base`, which is usually the frozen DEFAULT_IMPULSE_DISPLAY.
  const display: ImpulseDisplay = { ...base, csd: { ...base.csd }, harmonicOrders: [...base.harmonicOrders] }
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
  // An empty list is a real answer (every curve switched off) and survives as one; a list this
  // build cannot read comes back null and leaves `base` alone, like every other field here.
  const harmonicOrders = parseHarmonicOrders((stored as { chartHarmonicOrders?: unknown }).chartHarmonicOrders)
  if (harmonicOrders) display.harmonicOrders = harmonicOrders
  if (typeof stored.chartPhaseTopDeg === 'number') display.phaseTopDeg = stored.chartPhaseTopDeg
  if (typeof stored.chartPhaseBottomDeg === 'number') display.phaseBottomDeg = stored.chartPhaseBottomDeg
  if (isDelayView(stored.chartDelayView)) display.delayView = stored.chartDelayView
  if (typeof stored.chartDelayTopUs === 'number' && Number.isFinite(stored.chartDelayTopUs)) display.delayTopUs = stored.chartDelayTopUs
  if (typeof stored.chartDelayBottomUs === 'number' && Number.isFinite(stored.chartDelayBottomUs)) display.delayBottomUs = stored.chartDelayBottomUs
  if (typeof stored.chartFreqFromHz === 'number' && stored.chartFreqFromHz > 0) display.freqFromHz = stored.chartFreqFromHz
  if (typeof stored.chartFreqToHz === 'number' && stored.chartFreqToHz > 0) display.freqToHz = stored.chartFreqToHz
  // The decay views' own top of band. Stored wins, as everywhere here — but where nothing is stored
  // this is *derived from the measurement's own Nyquist* rather than left at the default, because
  // the default is only right for a measurement taken at the default span. A measurement saved
  // before this field existed has nothing stored for it and so takes the derived figure, which is
  // the point: the wall it was drawing is not something anybody chose.
  const storedDecayTop = (stored as { chartDecayFreqToHz?: unknown }).chartDecayFreqToHz
  if (typeof storedDecayTop === 'number' && storedDecayTop > 0) display.decayFreqToHz = storedDecayTop
  else if (typeof stored.frequencySpanHz === 'number' && stored.frequencySpanHz > 0) {
    display.decayFreqToHz = decayTopHz(stored.frequencySpanHz)
  }
  if (stored.chartDecayMode) display.decayMode = stored.chartDecayMode
  // `Spectrum` was this ramp's name for a day, before it became Jet itself; a measurement saved
  // in between stores that, and the dropdown would render a value it has no option for.
  if (stored.chartDecayPalette) {
    display.decayPalette = (stored.chartDecayPalette as string) === 'Spectrum' ? 'Jet' : stored.chartDecayPalette
  }
  if (typeof stored.chartDecayColoredCurves === 'boolean') display.decayColoredCurves = stored.chartDecayColoredCurves
  if (typeof stored.chartDecayFilled === 'boolean') display.decayFilled = stored.chartDecayFilled
  // The one checkbox these two replaced, read on the way in and never written: a measurement saved
  // with the mesh on asked for every edge, so it opens with both. Nothing migrates the stored
  // document — the old field simply stops being written, as `chartCsdBlockShiftSamples` did.
  const wholeMesh = typeof stored.chartDecayMeshLines === 'boolean' ? stored.chartDecayMeshLines : null
  display.decayMeshFrequency =
    typeof stored.chartDecayMeshFrequency === 'boolean'
      ? stored.chartDecayMeshFrequency
      : wholeMesh ?? display.decayMeshFrequency
  display.decayMeshTime =
    typeof stored.chartDecayMeshTime === 'boolean'
      ? stored.chartDecayMeshTime
      : wholeMesh ?? display.decayMeshTime
  if (typeof stored.chartDecayRotationDeg === 'number' && Number.isFinite(stored.chartDecayRotationDeg)) {
    display.decayRotationDeg = stored.chartDecayRotationDeg
  }
  if (typeof stored.chartDecayDepthPercent === 'number' && stored.chartDecayDepthPercent > 0) {
    display.decayDepthPercent = stored.chartDecayDepthPercent
  }
  // Finite rather than positive: Move takes either sign and zero is a legal value for it, so the
  // usual `> 0` guard every window field here carries would silently drop a centred view.
  if (typeof stored.chartDecayMoveX === 'number' && Number.isFinite(stored.chartDecayMoveX)) {
    display.decayMoveX = stored.chartDecayMoveX
  }
  if (typeof stored.chartDecayMoveY === 'number' && Number.isFinite(stored.chartDecayMoveY)) {
    display.decayMoveY = stored.chartDecayMoveY
  }
  // Through magnificationFromStored, because this control was re-based on 2026-10-29: it used
  // to run to 2000 and is a tenth of that now, so a stored figure above the new ceiling is one
  // written on the old scale.
  if (typeof stored.chartDecayMagnification === 'number' && stored.chartDecayMagnification > 0) {
    display.decayMagnification = magnificationFromStored(stored.chartDecayMagnification)
  }
  // A measurement stored before 2026-10-30 also carries the two light angles, and nothing reads
  // them: vis-graph3d has no light to position. They stay in its params_json, ignored.
  // How tall the box is drawn. A measurement stored before 2026-10-29 has three axis lengths
  // instead, and what it chose is the *ratio* of the height to the floor — so that is what is
  // carried over, through the same formula the slider drives. The floor's own two lengths are
  // dropped: setting them apart only tilted the picture, which is why there is one slider now.
  if (typeof stored.chartDecayExplodeZ === 'number' && stored.chartDecayExplodeZ > 0) {
    display.decayExplodeZ = stored.chartDecayExplodeZ
  } else if (
    typeof stored.chartDecayAxisLengthX === 'number' && stored.chartDecayAxisLengthX > 0 &&
    typeof stored.chartDecayAxisLengthY === 'number' && stored.chartDecayAxisLengthY > 0
  ) {
    display.decayExplodeZ = explodeZForAxisLengths(stored.chartDecayAxisLengthX, stored.chartDecayAxisLengthY)
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
