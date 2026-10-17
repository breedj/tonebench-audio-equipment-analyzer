import { parseAnnotations, type ChartAnnotation } from '../measurements/chartAnnotations'
import type {
  DecayPalette,
  FrequencyResponseParameters,
  NyquistRadiusName,
  ResponseDelayView,
  ResponsePhaseKindName,
  ResponseView,
  SPlaneScaleName,
  SurfaceModeName,
  SteppedResponseParameters,
} from '../api/client'
import { isSteppedResponseKind } from '../measurements/kinds'
import { PALETTE_NAMES } from '../measurements/decay'
import { isDelayView } from './delayAxis'

/**
 * The Bode chart's display state — every prop `FrequencyResponseChart` takes that is a preference
 * rather than data. App.vue holds these as refs for the screen; the report builds one from a stored
 * measurement type's `params_json` to render the same chart headless, and both go through
 * `responseDisplayFromParams` so a type restores the same way on screen and on paper.
 */
export interface ResponseDisplay {
  view: ResponseView
  showLeft: boolean
  showRight: boolean
  showPhase: boolean
  showCoherence: boolean
  showOneDb: boolean
  showThreeDb: boolean
  thickLines: boolean
  showDataPoints: boolean
  /** The Gain (dB) view's own y window. */
  yTopDb: number
  yBottomDb: number
  /** The FR (dBV) view's own y window. */
  yTopDbv: number
  yBottomDbv: number
  xFromHz: number
  xToHz: number
  phaseTopDeg: number
  phaseBottomDeg: number
  /** Which delay curve rides the Bode chart's delay axis, and that axis' window in µs. */
  delayView: ResponseDelayView
  delayTopUs: number
  delayBottomUs: number
  /** The Nyquist and Nichols projections' own settings — see ResponsePolarChart. */
  nyquistRadius: NyquistRadiusName
  nyquistRadiusFloorDb: number
  /**
   * Nichols: whether the gain axis is fitted to the curve, and the window it uses when it is not.
   *
   * On by default, which is what every build before the control did — so a stored type keeps the
   * window it appeared to have. The phase axis has no pair of its own on purpose: it carries
   * unwrapped degrees, so its span belongs to the DUT rather than to a preference (see
   * `nicholsViewport`), and the plotted frequency range is what narrows it.
   */
  nicholsAutoGain: boolean
  nicholsTopDb: number
  nicholsBottomDb: number
  /**
   * Pole-zero map: whether the window is fitted to the roots, and the window in hertz when it is
   * not — σ/2π along x, jω/2π up y, signed. A fitted window is drawn with equal aspect so ζ reads as
   * an angle; a hand-set one is honoured exactly instead.
   */
  poleZeroAutoWindow: boolean
  poleZeroXMinHz: number
  poleZeroXMaxHz: number
  poleZeroYMinHz: number
  poleZeroYMaxHz: number
  invertLoopSign: boolean
  restoreDelay: boolean
  /** Draw the Poles and zeros panel's model over the measurement on Nyquist and Nichols. */
  showFittedModel: boolean
  /**
   * Both s-plane views: leave the fit's extrapolated roots off the plot. A *plot* setting only —
   * the fit, the Poles and zeros table and H(s) all keep them, because a rational fit is solved as
   * one function and its residual is quoted against all of it.
   */
  hideOutOfBandRoots: boolean
  /** Both s-plane views: how radius is laid out — see response/splaneScale.ts. */
  surfaceScale: SPlaneScaleName
  /** The |H(s)| surface's layout: flat heat map, or oblique relief. */
  surfaceMode: SurfaceModeName
  /** The |H(s)| surface's colour window, as dB offsets from the passband reference. */
  surfaceTopDb: number
  surfaceBottomDb: number
  surfacePalette: DecayPalette
  surfaceContours: boolean
  /**
   * The notes drawn on this measurement's charts, as stored -- across every view, so one report
   * render can print a table per picture. See measurements/chartAnnotations.ts.
   */
  annotations: ChartAnnotation[]
}

/** What a fresh measurement type shows before anything is stored — the literals App.vue starts from. */
export const DEFAULT_RESPONSE_DISPLAY: Readonly<ResponseDisplay> = Object.freeze({
  annotations: [],
  // FR (dBV) rather than Gain (dB): the absolute level is what a response measurement starts as,
  // and a gain is only meaningful once a reference channel is in the picture. A type that was
  // saved showing Gain keeps showing it -- this is what a fresh one opens on.
  view: 'Dbv',
  showLeft: true,
  showRight: false,
  showPhase: false,
  showCoherence: false,
  showOneDb: true,
  showThreeDb: true,
  thickLines: true,
  showDataPoints: false,
  yTopDb: 10,
  yBottomDb: -10,
  yTopDbv: 10,
  yBottomDbv: -10,
  xFromHz: 10,
  xToHz: 100000,
  phaseTopDeg: 180,
  phaseBottomDeg: -180,
  delayView: 'None',
  delayTopUs: 1000,
  delayBottomUs: -1000,
  nyquistRadius: 'Linear',
  nyquistRadiusFloorDb: -40,
  nicholsAutoGain: true,
  nicholsTopDb: 20,
  nicholsBottomDb: -40,
  poleZeroAutoWindow: true,
  poleZeroXMinHz: -100000,
  poleZeroXMaxHz: 100000,
  poleZeroYMinHz: -100000,
  poleZeroYMaxHz: 100000,
  invertLoopSign: false,
  restoreDelay: false,
  showFittedModel: true,
  // On since 2026-10-06, at Joost's request. The roots it hides are the ones the sweep never
  // constrained, and on a wide span they routinely sit decades out — which, with the window fitted
  // to the roots, squeezes every real one into the origin marker. A type saved with the box
  // explicitly off keeps it off; one saved before the box existed picks this up, which is the
  // behaviour a default change is supposed to have.
  hideOutOfBandRoots: true,
  surfaceScale: 'Linear',
  surfaceMode: 'Heatmap',
  surfaceTopDb: 40,
  surfaceBottomDb: -60,
  surfacePalette: 'Jet',
  surfaceContours: true,
})

type StoredDisplay = Partial<FrequencyResponseParameters & SteppedResponseParameters>

/**
 * The display state a measurement type's `params_json` restores, over `base`.
 *
 * Every field is optional and guarded, so a type saved before one of them existed keeps whatever
 * `base` holds — on screen that is what is currently showing, which is the behaviour the app always
 * had. JSON that does not parse restores nothing. Note that a *result's* `params_json` carries no
 * `chart*` fields at all (MeasurementSaveService serialises the run settings only), so a stored
 * result's display always comes from its measurement type, not from the result.
 */
export function responseDisplayFromParams(
  paramsJson: string | null | undefined,
  base: Readonly<ResponseDisplay> = DEFAULT_RESPONSE_DISPLAY,
): ResponseDisplay {
  const display: ResponseDisplay = { ...base }
  if (!paramsJson) return display

  let stored: StoredDisplay
  try {
    stored = JSON.parse(paramsJson) as StoredDisplay
  } catch {
    return display
  }
  if (!stored || typeof stored !== 'object') return display

  if (stored.chartView === 'Dbv' || stored.chartView === 'Gain') display.view = stored.chartView
  if (typeof stored.chartShowLeft === 'boolean') display.showLeft = stored.chartShowLeft
  if (typeof stored.chartShowRight === 'boolean') display.showRight = stored.chartShowRight
  if (typeof stored.chartShowPhase === 'boolean') display.showPhase = stored.chartShowPhase
  if (typeof stored.chartShowCoherence === 'boolean') display.showCoherence = stored.chartShowCoherence
  if (typeof stored.chartShowOneDb === 'boolean') display.showOneDb = stored.chartShowOneDb
  if (typeof stored.chartShowThreeDb === 'boolean') display.showThreeDb = stored.chartShowThreeDb
  if (typeof stored.chartThickLines === 'boolean') display.thickLines = stored.chartThickLines
  if (typeof stored.chartShowDataPoints === 'boolean') display.showDataPoints = stored.chartShowDataPoints
  if (typeof stored.chartYTopDb === 'number') display.yTopDb = stored.chartYTopDb
  if (typeof stored.chartYBottomDb === 'number') display.yBottomDb = stored.chartYBottomDb
  if (typeof stored.chartYTopDbv === 'number') display.yTopDbv = stored.chartYTopDbv
  if (typeof stored.chartYBottomDbv === 'number') display.yBottomDbv = stored.chartYBottomDbv
  if (typeof stored.chartXFromHz === 'number' && stored.chartXFromHz > 0) display.xFromHz = stored.chartXFromHz
  if (typeof stored.chartXToHz === 'number' && stored.chartXToHz > 0) display.xToHz = stored.chartXToHz
  if (typeof stored.chartPhaseTopDeg === 'number') display.phaseTopDeg = stored.chartPhaseTopDeg
  if (typeof stored.chartPhaseBottomDeg === 'number') display.phaseBottomDeg = stored.chartPhaseBottomDeg
  if (isDelayView(stored.chartDelayView)) display.delayView = stored.chartDelayView
  if (typeof stored.chartDelayTopUs === 'number' && Number.isFinite(stored.chartDelayTopUs)) display.delayTopUs = stored.chartDelayTopUs
  if (typeof stored.chartDelayBottomUs === 'number' && Number.isFinite(stored.chartDelayBottomUs)) display.delayBottomUs = stored.chartDelayBottomUs
  if (stored.chartNyquistRadius === 'Linear' || stored.chartNyquistRadius === 'Db') display.nyquistRadius = stored.chartNyquistRadius
  if (typeof stored.chartNyquistRadiusFloorDb === 'number' && stored.chartNyquistRadiusFloorDb < 0)
    display.nyquistRadiusFloorDb = stored.chartNyquistRadiusFloorDb
  if (typeof stored.chartNicholsAutoGain === 'boolean') display.nicholsAutoGain = stored.chartNicholsAutoGain
  if (typeof stored.chartNicholsTopDb === 'number' && Number.isFinite(stored.chartNicholsTopDb))
    display.nicholsTopDb = stored.chartNicholsTopDb
  if (typeof stored.chartNicholsBottomDb === 'number' && Number.isFinite(stored.chartNicholsBottomDb))
    display.nicholsBottomDb = stored.chartNicholsBottomDb
  if (typeof stored.chartPoleZeroAutoWindow === 'boolean') display.poleZeroAutoWindow = stored.chartPoleZeroAutoWindow
  if (typeof stored.chartPoleZeroXMinHz === 'number' && Number.isFinite(stored.chartPoleZeroXMinHz))
    display.poleZeroXMinHz = stored.chartPoleZeroXMinHz
  if (typeof stored.chartPoleZeroXMaxHz === 'number' && Number.isFinite(stored.chartPoleZeroXMaxHz))
    display.poleZeroXMaxHz = stored.chartPoleZeroXMaxHz
  if (typeof stored.chartPoleZeroYMinHz === 'number' && Number.isFinite(stored.chartPoleZeroYMinHz))
    display.poleZeroYMinHz = stored.chartPoleZeroYMinHz
  if (typeof stored.chartPoleZeroYMaxHz === 'number' && Number.isFinite(stored.chartPoleZeroYMaxHz))
    display.poleZeroYMaxHz = stored.chartPoleZeroYMaxHz
  if (typeof stored.chartInvertLoopSign === 'boolean') display.invertLoopSign = stored.chartInvertLoopSign
  if (typeof stored.chartRestoreDelay === 'boolean') display.restoreDelay = stored.chartRestoreDelay
  if (typeof stored.chartShowFittedModel === 'boolean') display.showFittedModel = stored.chartShowFittedModel
  if (typeof stored.chartHideOutOfBandRoots === 'boolean') display.hideOutOfBandRoots = stored.chartHideOutOfBandRoots
  if (stored.chartSurfaceScale === 'Linear' || stored.chartSurfaceScale === 'LogModulus')
    display.surfaceScale = stored.chartSurfaceScale
  if (stored.chartSurfaceMode === 'Heatmap' || stored.chartSurfaceMode === 'Relief' || stored.chartSurfaceMode === 'Slice')
    display.surfaceMode = stored.chartSurfaceMode
  if (typeof stored.chartSurfaceTopDb === 'number' && Number.isFinite(stored.chartSurfaceTopDb))
    display.surfaceTopDb = stored.chartSurfaceTopDb
  if (typeof stored.chartSurfaceBottomDb === 'number' && Number.isFinite(stored.chartSurfaceBottomDb))
    display.surfaceBottomDb = stored.chartSurfaceBottomDb
  if (isDecayPalette(stored.chartSurfacePalette)) display.surfacePalette = stored.chartSurfacePalette
  if (typeof stored.chartSurfaceContours === 'boolean') display.surfaceContours = stored.chartSurfaceContours

  display.annotations = parseAnnotations(
    (stored as { chartAnnotations?: unknown }).chartAnnotations,
    { view: 'Bode', xQuantity: 'hz', yQuantity: 'dbv' },
  )

  return display
}

/** A stored palette name is only honoured when it is one the app still has a ramp for. */
function isDecayPalette(value: unknown): value is DecayPalette {
  return typeof value === 'string' && (PALETTE_NAMES as readonly string[]).includes(value)
}

/**
 * What phase a run of this kind measured, from its as-run settings — the same rule the two runners
 * apply (`FrequencyResponseRunner` always reports per-trace phase; `SteppedResponseRequest.PhaseKind`
 * is the stepped rule), so a stored result's chart labels its phase the way the live one did.
 */
export function responsePhaseKindFromParams(kind: string, paramsJson: string | null | undefined): ResponsePhaseKindName {
  if (!isSteppedResponseKind(kind)) return 'PerTrace'

  let stored: Partial<SteppedResponseParameters>
  try {
    stored = paramsJson ? (JSON.parse(paramsJson) as Partial<SteppedResponseParameters>) : {}
  } catch {
    return 'None'
  }
  if (!stored || typeof stored !== 'object') return 'None'

  if (stored.generateRight === false) return 'None'
  return stored.rightChannelIsReference === true ? 'DutResponse' : 'ChannelDifference'
}

/** Whether a run's settings made the right input the reference — which hides the right curve, since it is then the stimulus. */
export function responseRightChannelIsReferenceFromParams(paramsJson: string | null | undefined): boolean {
  try {
    const stored = paramsJson ? (JSON.parse(paramsJson) as { rightChannelIsReference?: unknown }) : null
    return stored?.rightChannelIsReference === true
  } catch {
    return false
  }
}

/**
 * The gating App.vue applies between the display preferences and what the chart is asked to draw:
 * the right curve is hidden when that input is the reference, phase when the run measured none, and
 * coherence on anything but a chirp (a stepped run's coherence is 1.0 by construction).
 */
export function responseChartFlags(
  display: Readonly<ResponseDisplay>,
  context: { kind: string; phaseKind: ResponsePhaseKindName; rightChannelIsReference: boolean; hasTraces: boolean },
): Pick<ResponseDisplay, 'showRight' | 'showPhase' | 'showCoherence' | 'delayView'> {
  const phaseAvailable = context.phaseKind !== 'None' || !context.hasTraces
  return {
    showRight: !context.rightChannelIsReference && display.showRight,
    showPhase: phaseAvailable && display.showPhase,
    showCoherence: !isSteppedResponseKind(context.kind) && display.showCoherence,
    // The delay curves are derived from the phase, so they are gated by exactly what gates it.
    delayView: phaseAvailable ? display.delayView : 'None',
  }
}
