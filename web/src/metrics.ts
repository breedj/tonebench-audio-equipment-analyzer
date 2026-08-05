// Pure derivation logic for the metrics tiles shown in MetricsPanel.vue / SystemMetricsPanel.vue.
// Kept free of Vue so every formula is directly unit-testable (see metrics.test.ts) — components
// just call these and render the result.

import type { MetersFrame } from './streaming/protocol'
import type { AveragingMode, GeneratorTone } from './api/client'

export interface MetricTile {
  label: string
  value: string
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

export function formatAveraging(mode: AveragingMode, count: number): string {
  if (mode === 'Off') return 'Off'
  return mode === 'Linear' ? `Linear x${count}` : `Exponential τ=${count}`
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
 * Full per-channel tile grid (Freq/Gain/THD/THD+N/N+D/N-D/SNR/Pk/RMS/Note/GenN), per the formula
 * table derived from the reference screenshot. `generatorTones` is that same channel's tones
 * (the generator is per-channel — see GeneratorPanel.vue) — Gain and GenN both key off it.
 */
export function channelTiles(meters: MetersFrame | null, generatorTones: GeneratorTone[], loadOhms: number): MetricTile[] {
  if (!meters) return []
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

    { label: 'THD+N %', value: `${m.thdNPercent.toFixed(5)} %` },
    { label: 'THD+N dB', value: `${thdNDb.toFixed(2)} dB` },
    { label: 'THD+N dBr', value: `${thdNDbv.toFixed(2)} dBr` },
    { label: 'THD+N dBV', value: `${thdNDbv.toFixed(2)} dBV` },
    { label: 'N+D dBr', value: `${thdNDbv.toFixed(2)} dBr` },
    { label: 'N+D dBV', value: `${thdNDbv.toFixed(2)} dBV` },

    { label: 'N-D dBr', value: `${noiseOnlyDbv.toFixed(2)} dBr` },
    { label: 'N-D dBV', value: `${noiseOnlyDbv.toFixed(2)} dBV` },
    { label: 'SNR dB', value: `${m.snrDb.toFixed(2)} dB` },
    { label: 'SNR dBr', value: `${noiseOnlyDbv.toFixed(2)} dBr` },

    { label: 'Pk dBr', value: `${m.peakDbv.toFixed(2)} dBr` },
    { label: 'Pk dBV', value: `${m.peakDbv.toFixed(2)} dBV` },
    { label: 'Pk Vrms', value: formatVrms(peakVrms) },
    { label: 'Pk Pwr', value: formatWatts(powerWatts(peakVrms, loadOhms), loadOhms) },

    { label: 'RMS dBr', value: `${m.totalRmsDbv.toFixed(2)} dBr` },
    { label: 'RMS dBV', value: `${m.totalRmsDbv.toFixed(2)} dBV` },
    { label: 'RMS dBu', value: `${dbvToDbu(m.totalRmsDbv).toFixed(2)} dBu` },
    { label: 'RMS Volts', value: formatVrms(rmsVrms) },
    { label: 'RMS Pwr', value: formatWatts(powerWatts(rmsVrms, loadOhms), loadOhms) },
  ]

  generatorTones.forEach((tone, i) => {
    const toneVrms = dbvToVrms(tone.ampDbv)
    tiles.push({
      label: `Gen${i + 1}`,
      value: `${(tone.freqHz / 1000).toFixed(4)} kHz\n${tone.ampDbv.toFixed(2)} dBV / ${formatVrms(toneVrms)}`,
    })
  })

  return tiles
}

export interface SystemTilesInput {
  fftSize: number
  window: string
  averaging: AveragingMode
  averageCount: number
  inputRangeDbv: number | undefined
  binSizeHz: number | undefined
}

/** Channel-independent Sys:* tiles, rendered once (see SystemMetricsPanel.vue). */
export function systemTiles(input: SystemTilesInput): MetricTile[] {
  return [
    { label: 'Sys:FFT', value: formatFftSize(input.fftSize) },
    { label: 'Sys:Averages', value: formatAveraging(input.averaging, input.averageCount) },
    { label: 'Sys:Window', value: input.window },
    {
      label: 'Sys:FullScale',
      value: input.inputRangeDbv === undefined ? '—' : `${input.inputRangeDbv >= 0 ? '+' : ''}${input.inputRangeDbv} dBV`,
    },
    { label: 'Sys:Resolution', value: input.binSizeHz === undefined ? '—' : `${input.binSizeHz.toFixed(2)} Hz` },
  ]
}
