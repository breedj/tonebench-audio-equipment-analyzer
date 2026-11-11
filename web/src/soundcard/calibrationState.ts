import type { SoundCardConfig } from '../api/client'

/**
 * Whether this card's stored full scales still describe the hardware in the path.
 *
 * <p>A full scale is a property of <i>one device and one channel pair</i>: it is the figure that
 * turns samples into volts, so if either changes after the step has run, every reading afterwards
 * is wrong by whatever the two differ by — silently, because nothing downstream can tell a
 * calibrated card from a mis-calibrated one. Until 2026-11-11 only <c>calibratedUtc</c> was
 * recorded, which says <i>when</i> and not <i>what</i>.</p>
 *
 * <p>Separate from <c>SoundCardDialog.vue</c> because nothing in a <c>.vue</c> is testable under
 * the node environment vitest runs in, and because the one rule that matters here is easy to get
 * backwards — see {@link calibrationState}'s third bullet.</p>
 */

/** Which direction a reading belongs to. The two are reported separately because they are measured separately. */
export type CalibrationDirection = 'input' | 'output'

export interface CalibrationDrift {
  direction: CalibrationDirection
  /** The device and channel pair the stored figure was measured on. */
  measuredOn: string
  /** What that direction names now. */
  nowOn: string
}

export interface CalibrationState {
  /** Directions with no stored full scale at all — the card reports dBFS rather than volts. */
  uncalibrated: CalibrationDirection[]
  /** Directions whose stored full scale was measured on hardware that is no longer selected. */
  drifted: CalibrationDrift[]
}

/** How a device and its channel pair are named in a message: "MOTU M Series (ASIO), channels 1/2". */
export function describeDevice(hostApi: string, deviceName: string, channelOffset: number): string {
  const name = deviceName.trim() === '' ? 'nothing' : deviceName.trim()
  const api = hostApi.trim() === '' ? '' : ` (${hostApi.trim()})`
  return `${name}${api}, channels ${channelOffset + 1}/${channelOffset + 2}`
}

/**
 * What to say about this card's calibration, if anything.
 *
 * <p>Three rules, and the third is the one that would be wrong by default:</p>
 *
 * <ul>
 *   <li><b>No stored full scale is "uncalibrated"</b>, per direction — the input needs both
 *   channels, since a card calibrated on the left alone converts one channel to volts and the
 *   other to nothing.</li>
 *
 *   <li><b>A stored full scale measured on a device or a channel pair that is no longer selected
 *   has drifted.</b> Either half counts: a calibration taken on inputs 3/4 and applied to 1/2 is
 *   as wrong as one taken on another card, and is the easier of the two to do by accident.</li>
 *
 *   <li><b>Provenance that was never recorded is not drift.</b> Every document written before this
 *   existed carries empty strings, and reading those as "differs" would tell every bench on earth
 *   that its calibration was stale the first time it opened this dialog — a warning that is both
 *   false and unfixable except by recalibrating. Unknown means no complaint; the next run of
 *   either step records it and the check starts working.</li>
 * </ul>
 */
export function calibrationState(config: SoundCardConfig | null): CalibrationState {
  const uncalibrated: CalibrationDirection[] = []
  const drifted: CalibrationDrift[] = []
  if (!config) return { uncalibrated, drifted }

  if (config.outputFullScalePeakMv == null) {
    uncalibrated.push('output')
  } else if (hasDrifted(
    config.outputCalibratedHostApi, config.outputCalibratedDeviceName, config.outputCalibratedChannelOffset,
    config.outputHostApi, config.outputDeviceName, config.outputChannelOffset,
  )) {
    drifted.push({
      direction: 'output',
      measuredOn: describeDevice(
        config.outputCalibratedHostApi, config.outputCalibratedDeviceName, config.outputCalibratedChannelOffset),
      nowOn: describeDevice(config.outputHostApi, config.outputDeviceName, config.outputChannelOffset),
    })
  }

  // Both channels, or the card converts one to volts and the other to nothing.
  if (config.inputFullScalePeakMvLeft == null || config.inputFullScalePeakMvRight == null) {
    uncalibrated.push('input')
  } else if (hasDrifted(
    config.inputCalibratedHostApi, config.inputCalibratedDeviceName, config.inputCalibratedChannelOffset,
    config.inputHostApi, config.inputDeviceName, config.inputChannelOffset,
  )) {
    drifted.push({
      direction: 'input',
      measuredOn: describeDevice(
        config.inputCalibratedHostApi, config.inputCalibratedDeviceName, config.inputCalibratedChannelOffset),
      nowOn: describeDevice(config.inputHostApi, config.inputDeviceName, config.inputChannelOffset),
    })
  }

  return { uncalibrated, drifted }
}

function hasDrifted(
  calibratedHostApi: string,
  calibratedDeviceName: string,
  calibratedChannelOffset: number,
  hostApi: string,
  deviceName: string,
  channelOffset: number,
): boolean {
  // Not recorded — see the third rule above. The host API is deliberately not part of this test:
  // a blank device name is what an older document has, and a build that recorded one recorded both.
  if (calibratedDeviceName.trim() === '') return false

  return (
    calibratedDeviceName.trim() !== deviceName.trim() ||
    calibratedHostApi.trim() !== hostApi.trim() ||
    calibratedChannelOffset !== channelOffset
  )
}
