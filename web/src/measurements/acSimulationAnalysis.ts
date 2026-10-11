/**
 * What an imported `.ac` sweep's settings imply, and how to read a stored one's settings back out
 * of its `params_json` — the frequency-domain counterpart of `simulationAnalysis.ts`.
 *
 * <p>Beside that module rather than inside it, for the reason its own server-side siblings are
 * separate: a transient run is described by a duration, a timestep and an FFT size, and a sweep by
 * a range, a point density and a band. Sharing one shape would make most of both optional.</p>
 *
 * <p>In a `.ts` because nothing in a `.vue` is testable here, and shared by the import and
 * re-analyse dialogs so the two cannot offer settings the other refuses.</p>
 */

/**
 * The highest reference-band edge the analyzer accepts — `BandwidthReferenceSpec.MaxHz`, which is
 * Nyquist at the QA403's fastest rate. A simulated sweep can run to megahertz, so this is the one
 * place the two disagree and the dialog has to say so: the *corners* are unconstrained, only the
 * band the reference level is taken over is capped. No amplifier's passband reference sits above
 * 96 kHz, so this costs nothing in practice.
 */
export const MAX_REFERENCE_HZ = 96_000

/** And the lowest: below the audio band, but not at DC, which has no logarithm. */
export const MIN_REFERENCE_HZ = 1

/** Below this the corner search is interpolating over gaps too wide to locate one in. */
const SPARSE_POINTS_PER_DECADE = 20

export type AcSweepDensity = 'ok' | 'sparse' | 'linear'

export interface AcSimulationFitInput {
  startFreqHz: number
  endFreqHz: number
  pointCount: number
  pointsPerDecade: number
  isLogSpaced: boolean
  analysisLowHz: number
  analysisHighHz: number
}

export interface AcSimulationFit {
  /** How many decades the sweep spans. */
  decades: number
  /** The spacing between points as a percentage — what bounds how closely a corner can be located. */
  stepPercent: number
  density: AcSweepDensity
  /** True when the sweep does not reach across the band being analysed. */
  bandNotCovered: boolean
  /** True when the analysis band is not a band at all. */
  bandInvalid: boolean
}

export function acSimulationFit(input: AcSimulationFitInput): AcSimulationFit {
  const decades =
    input.startFreqHz > 0 && input.endFreqHz > input.startFreqHz
      ? Math.log10(input.endFreqHz / input.startFreqHz)
      : 0

  const perDecade = input.pointsPerDecade
  const stepPercent = perDecade > 0 ? (Math.pow(10, 1 / perDecade) - 1) * 100 : 0

  const density: AcSweepDensity = !input.isLogSpaced
    ? 'linear'
    : perDecade > 0 && perDecade < SPARSE_POINTS_PER_DECADE
      ? 'sparse'
      : 'ok'

  const bandInvalid =
    !(input.analysisLowHz > 0) || !(input.analysisHighHz > input.analysisLowHz)

  return {
    decades,
    stepPercent,
    density,
    bandNotCovered:
      !bandInvalid &&
      input.pointCount > 0 &&
      (input.startFreqHz > input.analysisLowHz || input.endFreqHz < input.analysisHighHz),
    bandInvalid,
  }
}

/** Whether a reference band is one the analyzer will accept. */
export function referenceBandError(
  mode: string,
  lowHz: number,
  highHz: number,
): string | null {
  if (mode !== 'BandMedian') return null
  if (!(lowHz >= MIN_REFERENCE_HZ) || lowHz > MAX_REFERENCE_HZ)
    return `The reference band must start between ${MIN_REFERENCE_HZ} and ${MAX_REFERENCE_HZ.toLocaleString()} Hz.`
  if (!(highHz >= MIN_REFERENCE_HZ) || highHz > MAX_REFERENCE_HZ)
    return `The reference band must end between ${MIN_REFERENCE_HZ} and ${MAX_REFERENCE_HZ.toLocaleString()} Hz.`
  if (highHz <= lowHz) return 'The reference band must end above where it starts.'
  return null
}

/** Where an imported `.ac` sweep came from — `AcSimulationSource`, as params_json carries it. */
export interface StoredAcSimulationSource {
  path: string
  title: string
  plotname: string
  outputTrace: string
  outputUnit: string
  referenceTrace: string | null
  referenceUnit: string | null
  pointCount: number
  startFreqHz: number
  endFreqHz: number
  pointsPerDecade: number
  warnings: string[]
}

/**
 * Everything a stored `.ac` sweep can be re-analysed at, read back out of its own params_json.
 *
 * <p>`rightChannelIsReference` is only meaningful when `referenceTrace` is set: with both curves
 * stored it says which of them is the reference, and with one there is nothing to divide by.</p>
 */
export interface StoredAcSimulation {
  source: StoredAcSimulationSource
  levelOffsetDb: number
  analysisLowHz: number
  analysisHighHz: number
  bandwidthReference: string
  bandwidthReferenceLowHz: number
  bandwidthReferenceHighHz: number
  rightChannelIsReference: boolean
}

/**
 * Reads the AC block out of a stored response result's `params_json`, or null when there is none —
 * i.e. when the result is a measured sweep, or the document is something this build cannot read.
 * Never throws: a result the user has merely clicked on must not fail on its own parameters.
 */
export function parseStoredAcSimulation(
  paramsJson: string | null | undefined,
): StoredAcSimulation | null {
  if (!paramsJson) return null

  let parsed: unknown
  try {
    parsed = JSON.parse(paramsJson)
  } catch {
    return null
  }

  const params = parsed as Record<string, unknown> | null
  const simulation = params?.['acSimulation'] as Record<string, unknown> | null | undefined
  const source = simulation?.['source'] as Record<string, unknown> | null | undefined
  if (!params || !simulation || !source) return null

  const num = (value: unknown, fallback: number): number =>
    typeof value === 'number' && Number.isFinite(value) ? value : fallback
  const str = (value: unknown, fallback: string): string =>
    typeof value === 'string' ? value : fallback

  return {
    source: {
      path: str(source['path'], ''),
      title: str(source['title'], ''),
      plotname: str(source['plotname'], ''),
      outputTrace: str(source['outputTrace'], ''),
      outputUnit: str(source['outputUnit'], 'V'),
      referenceTrace: typeof source['referenceTrace'] === 'string' ? source['referenceTrace'] : null,
      referenceUnit: typeof source['referenceUnit'] === 'string' ? source['referenceUnit'] : null,
      pointCount: num(source['pointCount'], 0),
      startFreqHz: num(source['startFreqHz'], 0),
      endFreqHz: num(source['endFreqHz'], 0),
      pointsPerDecade: num(source['pointsPerDecade'], 0),
      warnings: Array.isArray(source['warnings'])
        ? (source['warnings'] as unknown[]).filter((w): w is string => typeof w === 'string')
        : [],
    },
    levelOffsetDb: num(simulation['levelOffsetDb'], 0),
    analysisLowHz: num(simulation['analysisLowHz'], 20),
    analysisHighHz: num(simulation['analysisHighHz'], 20000),
    bandwidthReference: str(params['bandwidthReference'], 'BandMedian'),
    bandwidthReferenceLowHz: num(params['bandwidthReferenceLowHz'], 200),
    bandwidthReferenceHighHz: num(params['bandwidthReferenceHighHz'], 2000),
    rightChannelIsReference: params['rightChannelIsReference'] === true,
  }
}

/** Whether this result imported both curves, which is what makes the reference swappable. */
export function hasReferenceCurve(stored: StoredAcSimulation): boolean {
  return stored.source.referenceTrace !== null
}

/**
 * What one sweep of a `.step`ped file is called in the import dialog: the simulator's own words
 * where there are any, and its position where there are not.
 *
 * <p>Here rather than in the dialog because the fallback is the whole of it, and it is the half
 * that only ever shows up on a QSpice file — QSpice prints its `.step` lines to the console and
 * writes no log, so every sweep of one arrives unlabelled, where every sweep of an LTspice run
 * arrives named. A dialog that reads `label` directly shows an empty checkbox list on one
 * simulator and not the other, which is exactly the kind of thing nothing in a `.vue` can test.</p>
 */
export function sweepLabel(sweep: { index: number; label: string | null }): string {
  return sweep.label === null || sweep.label.trim() === ''
    ? `Step ${sweep.index + 1}`
    : sweep.label
}
