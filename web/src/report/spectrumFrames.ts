import type { MeasurementDetail } from '../api/client'
import { StreamMsgType, type SpectrumFrame, type StreamChannel } from '../streaming/protocol'

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
