import { describe, expect, it } from 'vitest'
import type { SoundCardConfig } from '../api/client'
import { calibrationState, describeDevice } from './calibrationState'

/** A fully calibrated MOTU on channels 1/2, with its provenance recorded — the state after both steps. */
function config(partial: Partial<SoundCardConfig> = {}): SoundCardConfig {
  return {
    inputHostApi: 'ASIO',
    inputDeviceName: 'MOTU M Series',
    outputHostApi: 'ASIO',
    outputDeviceName: 'MOTU M Series',
    inputChannelOffset: 0,
    outputChannelOffset: 0,
    streamStartSettleMs: 2000,
    outputFullScalePeakMv: 3367.7,
    inputFullScalePeakMvLeft: 4000,
    inputFullScalePeakMvRight: 4000,
    calibratedUtc: '2026-11-11T10:00:00Z',
    outputCalibratedHostApi: 'ASIO',
    outputCalibratedDeviceName: 'MOTU M Series',
    outputCalibratedChannelOffset: 0,
    inputCalibratedHostApi: 'ASIO',
    inputCalibratedDeviceName: 'MOTU M Series',
    inputCalibratedChannelOffset: 0,
    frontEndGainLeftDb: 0,
    frontEndGainRightDb: 0,
    gainDisplayUnit: 'Db',
    minStimulusFrequencyHz: 20,
    ...partial,
  } as SoundCardConfig
}

describe('calibrationState', () => {
  it('says nothing about a card calibrated on the hardware it still names', () => {
    expect(calibrationState(config())).toEqual({ uncalibrated: [], drifted: [] })
  })

  it('reports each direction that has never been calibrated', () => {
    const state = calibrationState(
      config({ outputFullScalePeakMv: null, inputFullScalePeakMvLeft: null, inputFullScalePeakMvRight: null }))

    expect(state.uncalibrated).toEqual(['output', 'input'])
    expect(state.drifted).toEqual([])
  })

  /** One channel is not half-calibrated: it converts the left to volts and the right to nothing. */
  it('counts the input as uncalibrated when only one channel has a full scale', () => {
    expect(calibrationState(config({ inputFullScalePeakMvRight: null })).uncalibrated).toEqual(['input'])
  })

  it('reports a card whose output has been changed since it was calibrated', () => {
    const state = calibrationState(
      config({ outputHostApi: 'Windows WASAPI', outputDeviceName: 'Realtek(R) Audio' }))

    expect(state.uncalibrated).toEqual([])
    expect(state.drifted).toHaveLength(1)
    expect(state.drifted[0]!.direction).toBe('output')
    expect(state.drifted[0]!.measuredOn).toBe('MOTU M Series (ASIO), channels 1/2')
    expect(state.drifted[0]!.nowOn).toBe('Realtek(R) Audio (Windows WASAPI), channels 1/2')
  })

  /**
   * The easier of the two to do by accident, and exactly as wrong: a full scale measured on inputs
   * 3/4 and applied to 1/2 is a different analog path with its own gain.
   */
  it('reports a changed channel pair as drift, not just a changed device', () => {
    const state = calibrationState(config({ inputChannelOffset: 2 }))

    expect(state.drifted).toHaveLength(1)
    expect(state.drifted[0]!.direction).toBe('input')
    expect(state.drifted[0]!.measuredOn).toBe('MOTU M Series (ASIO), channels 1/2')
    expect(state.drifted[0]!.nowOn).toBe('MOTU M Series (ASIO), channels 3/4')
  })

  it('reports the same device under a different host API as drift', () => {
    expect(calibrationState(config({ inputHostApi: 'Windows WASAPI' })).drifted).toHaveLength(1)
  })

  it('reports both directions when both have moved', () => {
    const state = calibrationState(
      config({ inputDeviceName: 'Cosmos ADC', outputDeviceName: 'D50 III' }))

    expect(state.drifted.map((d) => d.direction)).toEqual(['output', 'input'])
  })

  /**
   * The rule that would be wrong by default. Every document written before the provenance fields
   * existed carries empty strings; reading those as "differs" would tell every calibrated bench
   * that its calibration was stale the first time it opened this dialog — false, and unfixable
   * except by recalibrating.
   */
  it('treats provenance that was never recorded as no complaint', () => {
    const state = calibrationState(config({
      outputCalibratedHostApi: '',
      outputCalibratedDeviceName: '',
      outputCalibratedChannelOffset: 0,
      inputCalibratedHostApi: '',
      inputCalibratedDeviceName: '',
      inputCalibratedChannelOffset: 0,
      // ...and the bench has since moved to another card entirely, which still must not warn.
      inputDeviceName: 'Cosmos ADC',
      outputDeviceName: 'D50 III',
      inputChannelOffset: 4,
    }))

    expect(state).toEqual({ uncalibrated: [], drifted: [] })
  })

  /** Drift is only meaningful where there is a figure to be wrong; an uncalibrated side is reported once. */
  it('does not report a direction as both uncalibrated and drifted', () => {
    const state = calibrationState(
      config({ outputFullScalePeakMv: null, outputDeviceName: 'D50 III' }))

    expect(state.uncalibrated).toEqual(['output'])
    expect(state.drifted).toEqual([])
  })

  it('answers for no configuration at all', () => {
    expect(calibrationState(null)).toEqual({ uncalibrated: [], drifted: [] })
  })
})

describe('describeDevice', () => {
  it('names the device, its host API and the channel pair', () => {
    expect(describeDevice('ASIO', 'MOTU M Series', 2)).toBe('MOTU M Series (ASIO), channels 3/4')
  })

  /** A one-direction bench has a side with no device, and "(), channels 1/2" reads as a fault. */
  it('says nothing rather than an empty name', () => {
    expect(describeDevice('', '', 0)).toBe('nothing, channels 1/2')
  })
})
