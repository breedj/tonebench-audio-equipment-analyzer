/**
 * The arithmetic an imported simulation's settings imply, and how to read a stored one's settings
 * back out of its `params_json`.
 *
 * <p>In a `.ts` because nothing in a `.vue` is testable here, and shared because there are two
 * dialogs asking the same questions: the import, which has a file in front of it, and the
 * re-analysis, which has only the stored samples. If the two disagreed about the largest usable
 * FFT size or about how coarse is too coarse, one of them would offer a setting the other refuses.</p>
 *
 * <p><b>The one thing to understand here</b> is that the bin width is <code>1 / usableSeconds</code>
 * and the FFT size cannot change it. The analysis rate is <i>derived</i> — `fftSize / usableSeconds`
 * — so the record always spans the whole run, and a larger FFT only raises the rate and computes
 * more empty spectrum above the audio band. Resolution comes from simulating for longer and from
 * nothing else, which is why `resolution` exists: it has to be said while the fix is still
 * available.</p>
 */

/** Offered in both dialogs; the server clamps to its own bounds whatever is sent. */
export const SIMULATION_FFT_SIZES = [
  1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576,
] as const

export const SIMULATION_WINDOWS = [
  'Hann',
  'BlackmanHarris',
  'FlatTop',
  'Kaiser5',
  'Kaiser7',
  'Rectangular',
] as const

/** The smallest FFT the server will accept — `SimulationAnalysis.MinFftSize`. */
export const MIN_SIMULATION_FFT_SIZE = 1024

/**
 * How many bins below the measurement band's lowest frequency counts as comfortable. Mirrors
 * `SimulationImportService.ResolutionWarning`, which is what writes the same judgement onto the
 * stored result.
 */
const COMFORTABLE_BINS = 4

/** Below this many bins the figures that integrate over the band stop meaning much. */
const USABLE_BINS = 2

export interface SimulationFitInput {
  /** Where the run ends, in absolute simulation seconds. */
  endTimeS: number
  /** Where the analysis starts, in absolute simulation seconds. */
  startTimeS: number
  /** The grid the samples are on — the simulator's own rate after any bounded regrid. */
  nativeRateHz: number
  fftSize: number
  measurementLoFreqHz: number
  measurementHiFreqHz: number
}

export type SimulationResolution = 'ok' | 'coarse' | 'tooCoarse'

export interface SimulationFit {
  /** Seconds of the run this analysis covers. */
  usableS: number
  /** The derived analysis rate, `fftSize / usableS`. */
  analysisRateHz: number
  /** `1 / usableS` — what a longer simulation, and only a longer simulation, improves. */
  binWidthHz: number
  /** The top of the spectrum the derived rate reaches. */
  nyquistHz: number
  /** The largest power of two the available samples can fill without interpolating past them. */
  maxFftSize: number
  fftTooLarge: boolean
  /** True when the derived Nyquist does not reach the top of the measurement band. */
  nyquistTooLow: boolean
  /** How many bins fall below the band's lowest frequency. */
  binsBelowBandEdge: number
  resolution: SimulationResolution
  /** How long the run would have to save for `COMFORTABLE_BINS` bins below the band edge, in ms. */
  neededMs: number
}

/**
 * The largest power-of-two FFT the available samples can fill. Doubles from
 * {@link MIN_SIMULATION_FFT_SIZE} while the next size still fits, which is
 * `SimulationAnalysis.LargestFftSize` exactly.
 */
export function largestSimulationFftSize(usableS: number, nativeRateHz: number): number {
  const samples = usableS * nativeRateHz
  let size = MIN_SIMULATION_FFT_SIZE
  const largest = SIMULATION_FFT_SIZES[SIMULATION_FFT_SIZES.length - 1]!
  while (size * 2 <= samples && size < largest) size *= 2
  return size
}

export function simulationFit(input: SimulationFitInput): SimulationFit {
  const usableS = Math.max(0, input.endTimeS - input.startTimeS)
  const analysisRateHz = usableS > 0 ? input.fftSize / usableS : 0
  const binWidthHz = usableS > 0 ? 1 / usableS : 0
  const maxFftSize = largestSimulationFftSize(usableS, input.nativeRateHz)

  const binsBelowBandEdge =
    binWidthHz > 0 ? input.measurementLoFreqHz / binWidthHz : Number.POSITIVE_INFINITY
  const resolution: SimulationResolution = !Number.isFinite(binsBelowBandEdge)
    ? 'ok'
    : binsBelowBandEdge >= COMFORTABLE_BINS
      ? 'ok'
      : binsBelowBandEdge < USABLE_BINS
        ? 'tooCoarse'
        : 'coarse'

  return {
    usableS,
    analysisRateHz,
    binWidthHz,
    nyquistHz: analysisRateHz / 2,
    maxFftSize,
    fftTooLarge: input.fftSize > maxFftSize,
    nyquistTooLow: analysisRateHz > 0 && analysisRateHz / 2 < input.measurementHiFreqHz,
    binsBelowBandEdge,
    resolution,
    neededMs:
      input.measurementLoFreqHz > 0 ? (COMFORTABLE_BINS / input.measurementLoFreqHz) * 1e3 : 0,
  }
}

/** Where an imported simulation came from — `SimulationSource`, as params_json carries it. */
export interface StoredSimulationSource {
  path: string
  title: string
  plotname: string
  leftTrace: string
  leftUnit: string
  rightTrace: string | null
  rightUnit: string | null
  nativeRateHz: number
  pointCount: number
  sourceStartS: number
  sourceEndS: number
  warnings: string[]
}

/**
 * Everything a stored simulation can be re-analysed at, read back out of its own params_json.
 *
 * <p>`pcmStartS` is where the stored samples begin and is <b>not</b> the same as `startTimeS` once
 * a re-analysis has moved the start forward: the samples start where the import left them, so a
 * later analysis can skip into them and can never recover anything earlier. Null on a row written
 * before that was recorded, where the two are equal — see `SimulationParameters.PcmStartS`.</p>
 */
export interface StoredSimulation {
  source: StoredSimulationSource
  startTimeS: number
  levelOffsetDb: number
  periodicRecord: boolean
  /** The DC taken off each channel at import, in that channel's own unit. */
  removedDcV: number[] | null
  pcmStartS: number | null
  fftSize: number
  window: string
  measurementLoFreqHz: number
  measurementHiFreqHz: number
}

/**
 * Reads the simulation block out of a stored FFT result's `params_json`, or null when there is
 * none — i.e. when the result is an ordinary measured capture, or the document is something this
 * build cannot read. Never throws: a result the user has merely clicked on must not fail on its
 * own stored parameters.
 */
export function parseStoredSimulation(paramsJson: string | null | undefined): StoredSimulation | null {
  if (!paramsJson) return null

  let parsed: unknown
  try {
    parsed = JSON.parse(paramsJson)
  } catch {
    return null
  }

  const params = parsed as Record<string, unknown> | null
  const simulation = params?.['simulation'] as Record<string, unknown> | null | undefined
  const source = simulation?.['source'] as Record<string, unknown> | null | undefined
  if (!params || !simulation || !source) return null

  const num = (value: unknown, fallback: number): number =>
    typeof value === 'number' && Number.isFinite(value) ? value : fallback

  return {
    source: {
      path: typeof source['path'] === 'string' ? source['path'] : '',
      title: typeof source['title'] === 'string' ? source['title'] : '',
      plotname: typeof source['plotname'] === 'string' ? source['plotname'] : '',
      leftTrace: typeof source['leftTrace'] === 'string' ? source['leftTrace'] : '',
      leftUnit: typeof source['leftUnit'] === 'string' ? source['leftUnit'] : 'V',
      rightTrace: typeof source['rightTrace'] === 'string' ? source['rightTrace'] : null,
      rightUnit: typeof source['rightUnit'] === 'string' ? source['rightUnit'] : null,
      nativeRateHz: num(source['nativeRateHz'], 0),
      pointCount: num(source['pointCount'], 0),
      sourceStartS: num(source['sourceStartS'], 0),
      sourceEndS: num(source['sourceEndS'], 0),
      warnings: Array.isArray(source['warnings'])
        ? (source['warnings'] as unknown[]).filter((w): w is string => typeof w === 'string')
        : [],
    },
    startTimeS: num(simulation['startTimeS'], 0),
    levelOffsetDb: num(simulation['levelOffsetDb'], 0),
    periodicRecord: simulation['periodicRecord'] !== false,
    removedDcV: Array.isArray(simulation['removedDcV'])
      ? (simulation['removedDcV'] as unknown[]).map((v) => num(v, 0))
      : null,
    pcmStartS: typeof simulation['pcmStartS'] === 'number' ? simulation['pcmStartS'] : null,
    fftSize: num(params['fftSize'], 8192),
    window: typeof params['window'] === 'string' ? params['window'] : 'Hann',
    measurementLoFreqHz: num(params['measurementLoFreqHz'], 20),
    measurementHiFreqHz: num(params['measurementHiFreqHz'], 20000),
  }
}

/**
 * Where the stored samples begin, which is the earliest a re-analysis may start from. Falls back
 * to the analysis start for a row written before the samples' own start was recorded, where the
 * two are equal by construction.
 */
export function samplesStartS(stored: StoredSimulation): number {
  return stored.pcmStartS ?? stored.startTimeS
}
