import type { GeneratorTone, MeasurementDetail } from '../api/client'
import {
  StreamMsgType,
  type HarmonicMarker,
  type IntermodulationFrame,
  type IntermodulationProductFrame,
  type MetersFrame,
  type MultitoneBandFrame,
  type MultitoneFrame,
  type NoiseFiguresFrame,
  type SpectrumFrame,
  type StreamChannel,
  type WeightingSpec,
} from '../streaming/protocol'

/**
 * A stored FFT result's channels as the frames SpectrumChart draws live — so a report of a stored
 * result draws it as the measurement it was (the live colours, with its min/max band) and not as a
 * history overlay of some other measurement. The stored columns are the same decimated
 * (freq, min, max) triples the stream carries, so this is a repack, not a resample; the header's
 * x0/dx are the span's ends, which is all the chart reads them for.
 */
export function storedSpectrumFrames(detail: MeasurementDetail): Record<number, SpectrumFrame> {
  const frames: Record<number, SpectrumFrame> = {}
  for (const channel of detail.channelsData) {
    const columns = channel.columns
    if (columns.length === 0) continue

    const packed = new Float32Array(columns.length * 3)
    for (let i = 0; i < columns.length; i++) {
      packed[i * 3] = columns[i].freqHz
      packed[i * 3 + 1] = columns[i].minDbv
      packed[i * 3 + 2] = columns[i].maxDbv
    }

    const loFreqHz = columns[0].freqHz
    const hiFreqHz = columns[columns.length - 1].freqHz
    frames[channel.channel] = {
      header: {
        msgType: StreamMsgType.Spectrum,
        channel: channel.channel as StreamChannel,
        flags: 0,
        seq: 0,
        x0: loFreqHz,
        dx: hiFreqHz,
        nPoints: columns.length,
      },
      loFreqHz,
      hiFreqHz,
      columns: packed,
    }
  }
  return frames
}

/**
 * A stored FFT result's metric rows as the meters frame the metric tiles and the harmonics table
 * read live — so a report of a stored result prints the same tiles, from the same formulas
 * (channelTiles), as the screen showed when it was saved. MeasurementSaveService writes one row per
 * meter (`thd_percent`, `snr_db`, `fundamental_dbv`, ...) and a `h{n}_hz`/`h{n}_dbc` pair per
 * harmonic; a channel saved without a fundamental has no rows and gets no frame. What a stored
 * result does not keep — the other peaks, the averaging count — is left empty.
 */
export function storedMetersFrames(detail: MeasurementDetail): Record<number, MetersFrame> {
  const frames: Record<number, MetersFrame> = {}
  const binSizeHz = detail.fftSize > 0 ? detail.sampleRateHz / detail.fftSize : 0

  for (const channel of detail.channelsData) {
    const m = channel.metrics
    if (typeof m.fundamental_hz !== 'number' || typeof m.fundamental_dbv !== 'number') continue

    const orders = Object.keys(m)
      .map((key) => /^h(\d+)_dbc$/.exec(key))
      .filter((match): match is RegExpExecArray => match !== null)
      .map((match) => Number(match[1]))
      .sort((a, b) => a - b)
    const harmonics: HarmonicMarker[] = orders.map((order) => ({
      freqHz: m[`h${order}_hz`] ?? m.fundamental_hz * order,
      dbc: m[`h${order}_dbc`],
    }))
    // The live frame's first entry is always the fundamental itself (see HarmonicsTable); a saved
    // list that starts at H2 gets it put back so the table reads the same.
    if (orders[0] !== 1) harmonics.unshift({ freqHz: m.fundamental_hz, dbc: 0 })

    frames[channel.channel] = {
      header: {
        msgType: StreamMsgType.Meters,
        channel: channel.channel as StreamChannel,
        flags: 0,
        seq: 0,
        x0: 0,
        dx: binSizeHz,
        nPoints: harmonics.length,
      },
      fundamentalFreqHz: m.fundamental_hz,
      binSizeHz,
      thdPercent: m.thd_percent ?? 0,
      thdNPercent: m.thd_n_percent ?? 0,
      snrDb: m.snr_db ?? 0,
      fundamentalDbv: m.fundamental_dbv,
      noiseFloorDbv: m.noise_floor_dbv ?? 0,
      peakDbv: m.peak_dbv ?? 0,
      totalRmsDbv: m.total_rms_dbv ?? 0,
      harmonics,
      otherPeaks: [],
      averagesTaken: 0,
      held: true,
    }
  }
  return frames
}

/**
 * A stored FFT result's `band_rms_dbv` / `noise_density_dbv_per_root_hz` rows as the noise frame
 * the tiles read live, with the channel's own stored ENBW and the weighting from the result's
 * params_json (passed in: the rows do not carry it). A result saved before the rows existed gets
 * no frame, and so no noise tiles -- a figure it never measured is not back-filled.
 */
export function storedNoiseFrames(detail: MeasurementDetail, weighting: WeightingSpec, band: { loHz: number; hiHz: number }): Record<number, NoiseFiguresFrame> {
  const frames: Record<number, NoiseFiguresFrame> = {}
  for (const channel of detail.channelsData) {
    const m = channel.metrics
    if (typeof m.band_rms_dbv !== 'number' || typeof m.noise_density_dbv_per_root_hz !== 'number') continue
    frames[channel.channel] = {
      header: {
        msgType: StreamMsgType.NoiseFigures,
        channel: channel.channel as StreamChannel,
        flags: 0,
        seq: 0,
        x0: band.loHz,
        dx: band.hiHz,
        nPoints: 0,
      },
      measurementLoHz: band.loHz,
      measurementHiHz: band.hiHz,
      bandRmsDbv: m.band_rms_dbv,
      noiseDensityDbvPerRootHz: m.noise_density_dbv_per_root_hz,
      enbwHz: channel.enbwHz ?? 0,
      weighting,
      held: true,
    }
  }
  return frames
}

/**
 * A stored FFT result's `imd_*` metric rows as the intermodulation frame the IMD tiles and the
 * Intermodulation table read live — see MeasurementSaveService.IntermodulationMetrics for the
 * names. A channel saved without an IMD figure (no two-tone spec at the time) has no `imd_percent`
 * row and gets no frame. The method and max order are in the result's params_json
 * (`imdMethod`/`imdMaxOrder`), not in the rows, so they are passed in.
 */
export function storedIntermodulationFrames(
  detail: MeasurementDetail,
  analysis: { method: 'Smpte' | 'Ccif'; maxOrder: number },
): Record<number, IntermodulationFrame> {
  const frames: Record<number, IntermodulationFrame> = {}

  for (const channel of detail.channelsData) {
    const m = channel.metrics
    if (typeof m.imd_percent !== 'number' || typeof m.imd_tone_low_hz !== 'number' || typeof m.imd_tone_high_hz !== 'number') continue

    const perOrder = Object.keys(m)
      .map((key) => /^im(\d+)_percent$/.exec(key))
      .filter((match): match is RegExpExecArray => match !== null)
      .map((match) => ({ order: Number(match[1]), percent: m[match[0]] }))
      .sort((a, b) => a.order - b.order)

    const products: IntermodulationProductFrame[] = []
    for (let i = 0; typeof m[`imd_p${i}_hz`] === 'number'; i++) {
      products.push({
        freqHz: m[`imd_p${i}_hz`],
        dbv: m[`imd_p${i}_dbv`] ?? 0,
        dbc: m[`imd_p${i}_dbc`] ?? 0,
        m: m[`imd_p${i}_m`] ?? 0,
        n: m[`imd_p${i}_n`] ?? 0,
        order: m[`imd_p${i}_order`] ?? 0,
      })
    }

    frames[channel.channel] = {
      header: {
        msgType: StreamMsgType.Intermodulation,
        channel: channel.channel as StreamChannel,
        flags: 0,
        seq: 0,
        x0: m.imd_tone_low_hz,
        dx: m.imd_tone_high_hz,
        nPoints: products.length,
      },
      toneLowHz: m.imd_tone_low_hz,
      toneHighHz: m.imd_tone_high_hz,
      method: analysis.method,
      maxOrder: analysis.maxOrder,
      toneLowDbv: m.imd_tone_low_dbv ?? 0,
      toneHighDbv: m.imd_tone_high_dbv ?? 0,
      imdPercent: m.imd_percent,
      sineEquivalentDbv: m.imd_sine_equivalent_dbv ?? 0,
      combinedRmsDbv: m.imd_combined_rms_dbv ?? 0,
      referenceDbv: m.imd_reference_dbv ?? 0,
      perOrder,
      products,
      held: true,
    }
  }
  return frames
}

/**
 * A stored FFT result's `mt_*` / `tdn_*` metric rows as the multitone frame the TD+N tiles and the
 * Multitone table read live — see MeasurementSaveService.MultitoneMetrics for the names. A channel
 * saved without a comb playing has no `tdn_percent` row and gets no frame. A gap with no `mt_b{i}_*`
 * rows was unresolved when saved (the save writes none for those), and reads back that way.
 */
export function storedMultitoneFrames(detail: MeasurementDetail): Record<number, MultitoneFrame> {
  const frames: Record<number, MultitoneFrame> = {}

  for (const channel of detail.channelsData) {
    const m = channel.metrics
    if (typeof m.tdn_percent !== 'number' || typeof m.mt_tone_count !== 'number') continue

    const tones: { freqHz: number; dbv: number }[] = []
    for (let i = 0; i < m.mt_tone_count && typeof m[`mt_t${i}_hz`] === 'number'; i++) {
      tones.push({ freqHz: m[`mt_t${i}_hz`], dbv: m[`mt_t${i}_dbv`] ?? 0 })
    }

    const bandCount = typeof m.mt_band_count === 'number' ? m.mt_band_count : Math.max(0, tones.length - 1)
    const bands: MultitoneBandFrame[] = []
    for (let i = 0; i < bandCount; i++) {
      const resolved = typeof m[`mt_b${i}_hz`] === 'number'
      bands.push({
        resolved,
        highestFreqHz: resolved ? m[`mt_b${i}_hz`] : 0,
        highestDbv: resolved ? (m[`mt_b${i}_dbv`] ?? 0) : 0,
        highestDbr: resolved ? (m[`mt_b${i}_dbr`] ?? 0) : 0,
        dnDbr: resolved ? (m[`mt_b${i}_dn_dbr`] ?? 0) : 0,
      })
    }

    const combDbv = typeof m.mt_comb_dbv === 'number' ? m.mt_comb_dbv : 0
    const tdn = m.tdn_percent / 100
    frames[channel.channel] = {
      header: {
        msgType: StreamMsgType.Multitone,
        channel: channel.channel as StreamChannel,
        flags: 0,
        seq: 0,
        x0: combDbv,
        dx: tdn,
        nPoints: tones.length,
      },
      combDbv,
      tdn,
      resolvedBands: typeof m.mt_resolved_bands === 'number' ? m.mt_resolved_bands : bands.filter((b) => b.resolved).length,
      tones,
      bands,
      held: true,
    }
  }
  return frames
}

/** The generator tones a stored result ran with, from its `genJson` — the left channel's, which the Gain tile reads. */
export function storedGeneratorTones(genJson: string | null): GeneratorTone[] {
  if (!genJson) return []
  try {
    const parsed = JSON.parse(genJson) as { left?: { tones?: GeneratorTone[] }; right?: { tones?: GeneratorTone[] } }
    const tones = parsed.left?.tones?.length ? parsed.left.tones : (parsed.right?.tones ?? [])
    return tones.filter((t) => typeof t.freqHz === 'number' && typeof t.ampDbv === 'number')
  } catch {
    return []
  }
}
