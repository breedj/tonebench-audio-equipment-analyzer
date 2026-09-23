import { describe, expect, it } from 'vitest'
import {
  DEFAULT_RESPONSE_DISPLAY,
  responseChartFlags,
  responseDisplayFromParams,
  responsePhaseKindFromParams,
  responseRightChannelIsReferenceFromParams,
} from './display'

describe('responseDisplayFromParams', () => {
  it('restores every chart field a type stores', () => {
    const json = JSON.stringify({
      chartView: 'Dbv',
      chartShowLeft: false,
      chartShowRight: true,
      chartShowPhase: true,
      chartShowCoherence: true,
      chartShowOneDb: false,
      chartShowThreeDb: false,
      chartThickLines: false,
      chartShowDataPoints: true,
      chartYTopDb: 5,
      chartYBottomDb: -25,
      chartYTopDbv: 12,
      chartYBottomDbv: -40,
      chartXFromHz: 20,
      chartXToHz: 20000,
      chartPhaseTopDeg: 90,
      chartPhaseBottomDeg: -270,
      chartDelayView: 'Group',
      chartDelayTopUs: 2500,
      chartDelayBottomUs: -100,
      chartNyquistRadius: 'Db',
      chartNyquistRadiusFloorDb: -60,
      chartInvertLoopSign: true,
      chartRestoreDelay: true,
      chartShowFittedModel: false,
      chartHideOutOfBandRoots: true,
    })

    expect(responseDisplayFromParams(json)).toEqual({
      view: 'Dbv',
      showLeft: false,
      showRight: true,
      showPhase: true,
      showCoherence: true,
      showOneDb: false,
      showThreeDb: false,
      thickLines: false,
      showDataPoints: true,
      yTopDb: 5,
      yBottomDb: -25,
      yTopDbv: 12,
      yBottomDbv: -40,
      xFromHz: 20,
      xToHz: 20000,
      phaseTopDeg: 90,
      phaseBottomDeg: -270,
      delayView: 'Group',
      delayTopUs: 2500,
      delayBottomUs: -100,
      nyquistRadius: 'Db',
      nyquistRadiusFloorDb: -60,
      invertLoopSign: true,
      restoreDelay: true,
      showFittedModel: false,
      hideOutOfBandRoots: true,
    })
  })

  it('ignores a delay view it does not know, so an older or hand-edited type opens with no delay curve', () => {
    expect(responseDisplayFromParams(JSON.stringify({ chartDelayView: 'group' })).delayView).toBe('None')
    expect(responseDisplayFromParams(JSON.stringify({ chartDelayView: 'Phase' })).delayView).toBe('Phase')
    expect(responseDisplayFromParams(JSON.stringify({ chartDelayTopUs: 'big' })).delayTopUs).toBe(1000)
  })

  it('ignores a non-negative radius floor', () => {
    expect(responseDisplayFromParams(JSON.stringify({ chartNyquistRadiusFloorDb: 5 })).nyquistRadiusFloorDb).toBe(-40)
  })

  it('keeps the base for fields a type saved before they existed', () => {
    const base = { ...DEFAULT_RESPONSE_DISPLAY, yTopDb: 3, showPhase: true }
    const display = responseDisplayFromParams(JSON.stringify({ chartView: 'Gain', chartXToHz: 50000 }), base)
    expect(display.yTopDb).toBe(3)
    expect(display.showPhase).toBe(true)
    expect(display.xToHz).toBe(50000)
  })

  it('ignores a non-positive frequency window, unparseable JSON and a result with no chart fields', () => {
    expect(responseDisplayFromParams(JSON.stringify({ chartXFromHz: 0, chartXToHz: -5 })).xFromHz).toBe(10)
    expect(responseDisplayFromParams('{not json')).toEqual(DEFAULT_RESPONSE_DISPLAY)
    expect(responseDisplayFromParams(JSON.stringify({ frequencySpanHz: 20000, levelValue: -18 }))).toEqual(DEFAULT_RESPONSE_DISPLAY)
    expect(responseDisplayFromParams(null)).toEqual(DEFAULT_RESPONSE_DISPLAY)
  })

  it('returns a copy, never the default object itself', () => {
    const display = responseDisplayFromParams(null)
    display.xFromHz = 1
    expect(DEFAULT_RESPONSE_DISPLAY.xFromHz).toBe(10)
  })
})

describe('responsePhaseKindFromParams', () => {
  it('is per-trace for a chirp whatever its settings', () => {
    expect(responsePhaseKindFromParams('FrequencyResponse', JSON.stringify({ rightChannelIsReference: true }))).toBe('PerTrace')
    expect(responsePhaseKindFromParams('FrequencyResponse', null)).toBe('PerTrace')
  })

  it('follows the stepped runner rule', () => {
    const stepped = (over: object) => responsePhaseKindFromParams('SteppedResponse', JSON.stringify(over))
    expect(stepped({ generateRight: false, rightChannelIsReference: true })).toBe('None')
    expect(stepped({ generateRight: true, rightChannelIsReference: true })).toBe('DutResponse')
    expect(stepped({ generateRight: true, rightChannelIsReference: false })).toBe('ChannelDifference')
  })

  it('reads the reference flag on its own', () => {
    expect(responseRightChannelIsReferenceFromParams(JSON.stringify({ rightChannelIsReference: true }))).toBe(true)
    expect(responseRightChannelIsReferenceFromParams(JSON.stringify({}))).toBe(false)
    expect(responseRightChannelIsReferenceFromParams('nope')).toBe(false)
  })
})

describe('responseChartFlags', () => {
  const display = { ...DEFAULT_RESPONSE_DISPLAY, showRight: true, showPhase: true, showCoherence: true }

  it('hides the right curve when that input is the reference', () => {
    const flags = responseChartFlags(display, { kind: 'FrequencyResponse', phaseKind: 'PerTrace', rightChannelIsReference: true, hasTraces: true })
    expect(flags.showRight).toBe(false)
    expect(flags.showPhase).toBe(true)
    expect(flags.showCoherence).toBe(true)
  })

  it('hides phase when the run measured none, but not on an empty chart', () => {
    expect(responseChartFlags(display, { kind: 'SteppedResponse', phaseKind: 'None', rightChannelIsReference: false, hasTraces: true }).showPhase).toBe(false)
    expect(responseChartFlags(display, { kind: 'SteppedResponse', phaseKind: 'None', rightChannelIsReference: false, hasTraces: false }).showPhase).toBe(true)
  })

  it('gates the delay curve on the same phase availability as the phase series', () => {
    const withDelay = { ...display, delayView: 'Group' as const }
    expect(responseChartFlags(withDelay, { kind: 'FrequencyResponse', phaseKind: 'PerTrace', rightChannelIsReference: false, hasTraces: true }).delayView).toBe('Group')
    expect(responseChartFlags(withDelay, { kind: 'SteppedResponse', phaseKind: 'None', rightChannelIsReference: false, hasTraces: true }).delayView).toBe('None')
  })

  it('never draws coherence for a stepped run', () => {
    expect(responseChartFlags(display, { kind: 'SteppedResponse', phaseKind: 'DutResponse', rightChannelIsReference: false, hasTraces: true }).showCoherence).toBe(false)
  })
})
