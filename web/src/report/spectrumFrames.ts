import type { GeneratorTone, MeasurementDetail } from '../api/client'
import { StreamMsgType, type HarmonicMarker, type MetersFrame, type SpectrumFrame, type StreamChannel } from '../streaming/protocol'

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
