import type { ImdSweepPoint, StoredSweepPoint, ThdSweepPoint } from '../api/client'
import type { ImdSweepPointFrame } from '../streaming/protocol'

/**
 * The one place an IMD sweep point is mapped onto the distortion-point shape ThdSweepChart draws.
 *
 * An IMD point (total, per-order 2..5, 6+, sine-equivalent level, power, gain, noise) fits the
 * THD point's slots one for one, so the 1000-line chart, its alignment, overlay styling, legend
 * and CSV export stay single-sourced, with only the trace *labels* switching (sweep/points.ts's
 * `SweepTraceFamily`). The pun is deliberate and confined to this file:
 *
 * - `thdPercent`        ← the IMD total
 * - `harmonicPercent`   ← IM2..IM5 (index 0 = order 2, stops at the first absent order)
 * - `d6PlusPercent`     ← the power sum of every order from 6 up (0 when none)
 * - `fundamentalDbv`    ← the measured output's sine-equivalent level, 20·log10(V1 + V2)
 * - `generatorDbv`      ← the generator's sine-equivalent level (the sweep's own axis)
 * - `gainDb`, `powerWatt`, `noisePercent` as they are
 * - `freqHz`            ← the low tone (every point shares it, as an amplitude sweep's do)
 * - `thdNPercent`, `snrDb` ← NaN: an IMD point has neither, and NaN is what the chart skips
 * - the normalized figures ← null: there is no normalized method for IMD
 *
 * Anything that reads `thdPercent` as "THD" off one of these points is reading a label, and the
 * label is the family's to supply.
 */

function powerSumPercent(percents: number[]): number {
  let sum = 0
  for (const p of percents) sum += p * p
  return Math.sqrt(sum)
}

function fromOrders(orderPercent: number[]): { harmonicPercent: number[]; d6PlusPercent: number } {
  return {
    harmonicPercent: orderPercent.slice(0, 4),
    d6PlusPercent: powerSumPercent(orderPercent.slice(4)),
  }
}

/** A live sweep-point frame, as the chart's point. */
export function imdFrameToDistortionPoint(frame: ImdSweepPointFrame): ThdSweepPoint {
  return {
    freqHz: frame.toneLowHz,
    channel: frame.channel,
    fundamentalDbv: frame.sineEquivalentDbv,
    gainDb: frame.gainDb,
    powerWatt: frame.powerWatt,
    thdPercent: frame.imdPercent,
    thdNPercent: Number.NaN,
    snrDb: Number.NaN,
    ...fromOrders(frame.orderPercent),
    noisePercent: frame.noisePercent,
    generatorDbv: frame.generatorSineEquivalentDbv,
    previewFft: frame.previewFft,
    previewScope: frame.previewScope,
    normalizedThdPercent: null,
    normalizedHarmonicPercent: null,
    normalizedD6PlusPercent: null,
  }
}

/** A status endpoint's point (GET /api/imd-sweep/status), as the chart's point. */
export function imdStatusPointToDistortionPoint(point: ImdSweepPoint): ThdSweepPoint {
  return {
    freqHz: point.toneLowHz,
    channel: point.channel,
    fundamentalDbv: point.sineEquivalentDbv,
    gainDb: point.gainDb,
    powerWatt: point.powerWatt,
    thdPercent: point.imdPercent,
    thdNPercent: Number.NaN,
    snrDb: Number.NaN,
    ...fromOrders(point.orderPercent),
    noisePercent: point.noisePercent,
    generatorDbv: point.generatorSineEquivalentDbv,
    previewFft: point.previewFft,
    previewScope: point.previewScope,
    normalizedThdPercent: null,
    normalizedHarmonicPercent: null,
    normalizedD6PlusPercent: null,
  }
}

/**
 * A stored IMD sweep's points (GET /api/measurements/{id}/sweep-points), as the chart's points.
 * Metric names are MeasurementSaveService.BuildImdSweepMetrics's: `imd_percent`, `im{k}_percent`,
 * `fundamental_dbv` (the sine-equivalent output), `generator_dbv`, `gain_db`, `power_watt`,
 * `noise_percent`. An order absent from the rows is the end of the list, never a zero.
 */
export function toImdSweepPoints(stored: StoredSweepPoint[]): ThdSweepPoint[] {
  const points: ThdSweepPoint[] = []
  for (const point of stored) {
    for (const [channel, metrics] of Object.entries(point.metricsByChannel)) {
      const value = (name: string) => (name in metrics ? metrics[name]! : Number.NaN)

      const orderPercent: number[] = []
      for (let order = 2; `im${order}_percent` in metrics; order++) orderPercent.push(metrics[`im${order}_percent`]!)

      const previews = point.previewsByChannel?.[Number(channel)]

      points.push({
        freqHz: point.freqHz,
        channel: Number(channel),
        fundamentalDbv: value('fundamental_dbv'),
        gainDb: value('gain_db'),
        powerWatt: value('power_watt'),
        thdPercent: value('imd_percent'),
        thdNPercent: Number.NaN,
        snrDb: Number.NaN,
        ...fromOrders(orderPercent),
        noisePercent: value('noise_percent'),
        generatorDbv: value('generator_dbv'),
        previewFft: previews?.fft ?? null,
        previewScope: previews?.scope ?? null,
        normalizedThdPercent: null,
        normalizedHarmonicPercent: null,
        normalizedD6PlusPercent: null,
      })
    }
  }
  return points
}
