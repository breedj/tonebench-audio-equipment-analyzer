// Pure derivation logic for the metrics tiles shown in MetricsPanel.vue / SystemMetricsPanel.vue.
// Kept free of Vue so every formula is directly unit-testable (see metrics.test.ts) — components
// just call these and render the result.

import type { IntermodulationFrame, MetersFrame, NoiseFiguresFrame } from './streaming/protocol'
import type { AveragingMode, GeneratorKind, GeneratorTone, MultitoneSettings, WhiteNoiseSettings } from './api/client'
import { weightingQualifier } from './weighting'

export interface MetricTile {
  label: string
  value: string
  /**
   * The weighting the figure was integrated under ("A", "468", "A+U1"), on the noise-integrated
   * tiles only -- THD+N, N+D, N-D, SNR and the three noise tiles. Absent when unweighted, and
   * always absent on the fundamental, THD, Gain, Pk and RMS tiles, which no weighting touches.
   */
  qualifier?: string
}

/**
 * The label backing the single, channel-independent generator summary tile (see generatorTile()
 * below and GeneratorMetricsPanel.vue) — both channels play the same stimulus, so unlike every
 * other entry here this isn't a per-channel L:/R: tile. Selected/removed through the same
 * useMetricSelection.ts list as AVAILABLE_METRIC_LABELS (MetricPickerDialog.vue offers it
 * alongside them), just rendered by a different component.
 */
export const GEN_METRIC_LABEL = 'Gen'

/**
 * The fixed, user-selectable subset of channelTiles()'s labels, in the same order. Used to back
 * MetricPickerDialog.vue and to filter MetricsPanel.vue's display down to what's selected — see
 * useMetricSelection.ts.
 */
export const AVAILABLE_METRIC_LABELS = [
  'Freq Hz',
  'Note',
  'Gain dB',
  'THD %',
  'THD dB',
  'THD dBr',
  'THD dBV',
  'IMD %',
  'IMD dB',
  'THD+N %',
  'THD+N dB',
  'THD+N dBr',
  'THD+N dBV',
  'N+D dBr',
  'N+D dBV',
  'N-D dBr',
  'N-D dBV',
  'SNR dB',
  'SNR dBr',
  'Pk dBr',
  'Pk dBV',
  'Pk Vrms',
  'Pk Pwr',
  'RMS dBr',
  'RMS dBV',
  'RMS dBu',
  'RMS Volts',
  'RMS Pwr',
  'RMS dBV(bw)',
  'Noise dBV/√Hz',
  'Noise in nV/√Hz',
] as const

/**
 * The noise density referred to the DUT's input: density − gain, as volts. −150 dBV/√Hz at 0 dB
 * is 31.6 nV/√Hz; through 20 dB of gain the same output floor is 3.16 nV/√Hz at the input.
 */
export function noiseInputReferredNvPerRootHz(noiseDensityDbvPerRootHz: number, dutGainDb: number): number {
  const gain = Number.isFinite(dutGainDb) ? dutGainDb : 0
  return Math.pow(10, (noiseDensityDbvPerRootHz - gain) / 20) * 1e9
}

/** "31.6 nV/√Hz", "3.16 µV/√Hz" above a thousand. */
export function formatNvPerRootHz(nv: number): string {
  if (!Number.isFinite(nv)) return '—'
  if (nv >= 1000) return `${(nv / 1000).toPrecision(3)} µV/√Hz`
  return `${nv.toPrecision(3)} nV/√Hz`
}

export function dbvToVrms(dbv: number): number {
  return Math.pow(10, dbv / 20)
}

export function vrmsToDbv(vrms: number): number {
  return 20 * Math.log10(Math.max(vrms, Number.EPSILON))
}

// 0 dBu = 0.775 Vrms, 0 dBV = 1 Vrms -> dBu = dBV + 20*log10(1/0.775).
const DBU_OFFSET = 20 * Math.log10(1 / 0.775)

export function dbvToDbu(dbv: number): number {
  return dbv + DBU_OFFSET
}

/** 20*log10(percent/100) — converts a THD-style "% of fundamental" ratio to dBc. */
export function ratioPercentToDb(percent: number): number {
  return 20 * Math.log10(Math.max(percent, Number.EPSILON) / 100)
}

export function powerWatts(vrms: number, loadOhms: number): number {
  return (vrms * vrms) / loadOhms
}

/** Turns a dBc-style ratio (relative to the fundamental) back into an absolute dBV level. */
export function absoluteLevelDbv(fundamentalDbv: number, relativeDb: number): number {
  return fundamentalDbv + relativeDb
}

export function formatVrms(vrms: number): string {
  return Math.abs(vrms) < 1 ? `${(vrms * 1000).toFixed(1)} mVrms` : `${vrms.toFixed(4)} Vrms`
}

export function formatWatts(watts: number, loadOhms: number): string {
  return Math.abs(watts) < 1 ? `${(watts * 1000).toFixed(2)} mW (${loadOhms}Ω)` : `${watts.toFixed(3)} W (${loadOhms}Ω)`
}

/** "131072" -> "128k" (binary-K, matching the FFT_SIZES list's own convention); anything else passes through. */
export function formatFftSize(n: number): string {
  return n >= 1024 && n % 1024 === 0 ? `${n / 1024}k` : `${n}`
}

/**
 * `taken` is how many spectra have contributed so far (see MetersFrame.averagesTaken) — counts up
 * to `count` as averaging fills its window, then holds there. Omitted (not just 0) whenever no
 * meters frame has arrived yet, so a fresh/idle panel reads "Linear x8" rather than a premature
 * "Linear x8 (0/8)".
 */
export function formatAveraging(mode: AveragingMode, count: number, taken?: number): string {
  if (mode === 'Off') return 'Off'
  const progress = taken === undefined ? '' : ` (${Math.min(taken, count)}/${count})`
  return (mode === 'Linear' ? `Linear x${count}` : `Exponential τ=${count}`) + progress
}

const NOTE_NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']

/**
 * Nearest equal-tempered note name (A4 = 440 Hz) with a rough cents-deviation indicator (0-3
 * +/- symbols, more symbols = further off-pitch). QuantAsylum's exact cents-bucket thresholds
 * for the real app aren't published anywhere we could verify against — this reproduces the
 * general shape (a note name plus a rough sharp/flat indicator), not their exact bucketing.
 */
export function noteName(freqHz: number): string {
  if (!(freqHz > 0)) return '—'

  const midi = 69 + 12 * Math.log2(freqHz / 440)
  const rounded = Math.round(midi)
  const cents = (midi - rounded) * 100
  const name = NOTE_NAMES[((rounded % 12) + 12) % 12]
  const octave = Math.floor(rounded / 12) - 1

  const absCents = Math.abs(cents)
  const symbolCount = absCents < 5 ? 0 : absCents < 15 ? 1 : absCents < 30 ? 2 : 3
  const suffix = (cents >= 0 ? '+' : '-').repeat(symbolCount)

  return `${name}${octave}${suffix}`
}

/**
 * Full per-channel tile grid (Freq/Gain/THD/THD+N/N+D/N-D/SNR/Pk/RMS/Note), per the formula table
 * derived from the reference screenshot. `generatorTones` is the shared stimulus's *actual*
 * (possibly bin-snapped) tones as last reported by DeviceStatus — used only for the Gain
 * calculation here; the generator summary shown to the user is a single tile independent of
 * channel, see generatorTile() below.
 */
export function channelTiles(
  meters: MetersFrame | null,
  generatorTones: GeneratorTone[],
  loadOhms: number,
  /**
   * The channel's two-tone IMD figure, present only while the pipeline has an IMD spec (see
   * useSpectrumStream). Null adds no IMD tiles at all rather than "0 %" — no reading is not a reading.
   */
  imd: IntermodulationFrame | null = null,
  /** The pipeline has an IMD spec it could not resolve this tick (tones too close at this FFT size): the tiles show a dash rather than vanish. */
  imdUnresolved = false,
  /**
   * The channel's band RMS, noise density and the weighting in effect (see useSpectrumStream). It
   * arrives every tick, fundamental or not, so the three noise tiles are shown even when `meters`
   * is null -- a noise measurement with the generator off is exactly that case.
   */
  noise: NoiseFiguresFrame | null = null,
  /** The DUT gain the input-referred noise tile divides by, in dB. */
  dutGainDb = 0,
): MetricTile[] {
  const qualifier = weightingQualifier(noise?.weighting) ?? undefined
  const weighted = (tile: MetricTile): MetricTile => (qualifier ? { ...tile, qualifier } : tile)
  const noiseTiles: MetricTile[] = noise
    ? [
        weighted({ label: 'RMS dBV(bw)', value: `${noise.bandRmsDbv.toFixed(2)} dBV` }),
        weighted({ label: 'Noise dBV/√Hz', value: `${noise.noiseDensityDbvPerRootHz.toFixed(2)} dBV/√Hz` }),
        weighted({ label: 'Noise in nV/√Hz', value: formatNvPerRootHz(noiseInputReferredNvPerRootHz(noise.noiseDensityDbvPerRootHz, dutGainDb)) }),
      ]
    : []

  if (!meters) return noiseTiles
  const m = meters

  const thdDb = ratioPercentToDb(m.thdPercent)
  const thdNDb = ratioPercentToDb(m.thdNPercent)
  const thdDbv = absoluteLevelDbv(m.fundamentalDbv, thdDb)
  const thdNDbv = absoluteLevelDbv(m.fundamentalDbv, thdNDb)
  // Noise-only absolute level (harmonics excluded) — identical to the already-sent noiseFloorDbv
  // field; SNR dBr and N-D dBr/dBV are all the same underlying quantity under different names.
  const noiseOnlyDbv = m.noiseFloorDbv

  const peakVrms = dbvToVrms(m.peakDbv)
  const rmsVrms = dbvToVrms(m.totalRmsDbv)

  const gainDb = generatorTones.length > 0 ? m.fundamentalDbv - generatorTones[0].ampDbv : null

  const tiles: MetricTile[] = [
    { label: 'Freq Hz', value: `${m.fundamentalFreqHz.toFixed(3)} Hz` },
    { label: 'Note', value: noteName(m.fundamentalFreqHz) },
    { label: 'Gain dB', value: gainDb === null ? '—' : `${gainDb.toFixed(2)} dB` },

    { label: 'THD %', value: `${m.thdPercent.toFixed(5)} %` },
    { label: 'THD dB', value: `${thdDb.toFixed(2)} dB` },
    { label: 'THD dBr', value: `${thdDbv.toFixed(2)} dBr` },
    { label: 'THD dBV', value: `${thdDbv.toFixed(2)} dBV` },

    ...(imd
      ? [
          { label: 'IMD %', value: `${imd.imdPercent.toFixed(5)} %` },
          { label: 'IMD dB', value: `${ratioPercentToDb(imd.imdPercent).toFixed(2)} dB` },
        ]
      : imdUnresolved
        ? [
            { label: 'IMD %', value: '—' },
            { label: 'IMD dB', value: '—' },
          ]
        : []),

    weighted({ label: 'THD+N %', value: `${m.thdNPercent.toFixed(5)} %` }),
    weighted({ label: 'THD+N dB', value: `${thdNDb.toFixed(2)} dB` }),
    weighted({ label: 'THD+N dBr', value: `${thdNDbv.toFixed(2)} dBr` }),
    weighted({ label: 'THD+N dBV', value: `${thdNDbv.toFixed(2)} dBV` }),
    weighted({ label: 'N+D dBr', value: `${thdNDbv.toFixed(2)} dBr` }),
    weighted({ label: 'N+D dBV', value: `${thdNDbv.toFixed(2)} dBV` }),

    weighted({ label: 'N-D dBr', value: `${noiseOnlyDbv.toFixed(2)} dBr` }),
    weighted({ label: 'N-D dBV', value: `${noiseOnlyDbv.toFixed(2)} dBV` }),
    weighted({ label: 'SNR dB', value: `${m.snrDb.toFixed(2)} dB` }),
    weighted({ label: 'SNR dBr', value: `${noiseOnlyDbv.toFixed(2)} dBr` }),

    { label: 'Pk dBr', value: `${m.peakDbv.toFixed(2)} dBr` },
    { label: 'Pk dBV', value: `${m.peakDbv.toFixed(2)} dBV` },
    { label: 'Pk Vrms', value: formatVrms(peakVrms) },
    { label: 'Pk Pwr', value: formatWatts(powerWatts(peakVrms, loadOhms), loadOhms) },

    { label: 'RMS dBr', value: `${m.totalRmsDbv.toFixed(2)} dBr` },
    { label: 'RMS dBV', value: `${m.totalRmsDbv.toFixed(2)} dBV` },
    { label: 'RMS dBu', value: `${dbvToDbu(m.totalRmsDbv).toFixed(2)} dBu` },
    { label: 'RMS Volts', value: formatVrms(rmsVrms) },
    { label: 'RMS Pwr', value: formatWatts(powerWatts(rmsVrms, loadOhms), loadOhms) },

    ...noiseTiles,
  ]

  return tiles
}

// Multitone's comb is log-spaced over a fixed band the generator form doesn't expose as separate
// controls (see GeneratorChannelPanel.vue's "Fixed 5 Hz – 20 kHz band" note) — mirrored here from
// MultitoneGenerator.cs's own constants so the tile can be computed from the staged form alone,
// without waiting for DeviceStatus to report the server-derived comb (which goes silent on Stop —
// see useGeneratorStimulus.ts).
const MULTITONE_BAND_START_HZ = 5
const MULTITONE_BAND_END_HZ = 20_000

/** Tone count for a given tones-per-octave, matching MultitoneGenerator.Build's f_i = 5 * 2^(i/n) walk. */
export function multitoneToneCount(tonesPerOctave: number): number {
  if (!(tonesPerOctave >= 1)) return 0
  return Math.floor(tonesPerOctave * Math.log2(MULTITONE_BAND_END_HZ / MULTITONE_BAND_START_HZ)) + 1
}

/**
 * The single, channel-independent "Gen" summary tile (see GEN_METRIC_LABEL) — one tile
 * regardless of stimulus kind, computed from the staged generator form (useGeneratorStimulus.ts)
 * rather than from live tones, so it keeps showing the configured stimulus after Stop instead of
 * disappearing along with DeviceStatus's silenced generator state. Null for kinds with nothing
 * useful to summarise (no tones staged yet, or a kind with no UI of its own).
 */
export function generatorTile(
  kind: GeneratorKind,
  tones: GeneratorTone[],
  multitone: MultitoneSettings,
  noise: WhiteNoiseSettings,
): MetricTile | null {
  switch (kind) {
    case 'Tones': {
      if (tones.length === 0) return null
      const value = tones.map((t) => `${t.freqHz.toFixed(3)} Hz, ${t.ampDbv.toFixed(2)} dBV`).join('\n')
      return { label: GEN_METRIC_LABEL, value }
    }
    case 'Multitone': {
      const count = multitoneToneCount(multitone.tonesPerOctave)
      return {
        label: GEN_METRIC_LABEL,
        value: `(${count} tones), ${MULTITONE_BAND_START_HZ} Hz - ${MULTITONE_BAND_END_HZ / 1000} kHz`,
      }
    }
    case 'WhiteNoise':
      return { label: GEN_METRIC_LABEL, value: `White noise, ${noise.levelDbv.toFixed(2)} dBV` }
    default:
      return null
  }
}

export interface SystemTilesInput {
  fftSize: number
  window: string
  averaging: AveragingMode
  averageCount: number
  /** How many spectra have contributed to the current average so far — see MetersFrame.averagesTaken. */
  averagesTaken: number | undefined
  inputRangeDbv: number | undefined
  binSizeHz: number | undefined
}

/**
 * Channel-independent Sys:* tiles, rendered once (see SystemMetricsPanel.vue). Averages moved out
 * of this grid into its own readout above the chart (see App.vue) — formatAveraging is what that
 * reads directly, not a Sys:* tile here anymore.
 */
export function systemTiles(input: SystemTilesInput): MetricTile[] {
  return [
    { label: 'Sys:FFT', value: formatFftSize(input.fftSize) },
    { label: 'Sys:Window', value: input.window },
    {
      label: 'Sys:FullScale',
      value: input.inputRangeDbv === undefined ? '—' : `${input.inputRangeDbv >= 0 ? '+' : ''}${input.inputRangeDbv} dBV`,
    },
    { label: 'Sys:Resolution', value: input.binSizeHz === undefined ? '—' : `${input.binSizeHz.toFixed(2)} Hz` },
  ]
}
