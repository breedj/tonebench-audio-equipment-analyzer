import { describe, expect, it } from 'vitest'
import {
  DEFAULT_RESPONSE_DISPLAY,
  responseChartFlags,
  responseDisplayFromParams,
  responsePhaseKindFromParams,
  responseRightChannelIsReferenceFromParams,
} from './display'

describe('responseDisplayFromParams', () => {
  /**
   * A fresh measurement opens on FR (dBV) and the Bode projection. The view is pinned here because
   * it is a deliberate choice rather than an accident of which literal was typed first: the
   * absolute level is what a response measurement *is* until a reference channel makes a gain
   * meaningful.
   */
  it('opens a type that stores nothing on FR (dBV)', () => {
    expect(DEFAULT_RESPONSE_DISPLAY.view).toBe('Dbv')
    expect(responseDisplayFromParams('{}').view).toBe('Dbv')
    expect(responseDisplayFromParams(null).view).toBe('Dbv')
  })

  it('still restores a type that chose Gain (dB)', () => {
    expect(responseDisplayFromParams(JSON.stringify({ chartView: 'Gain' })).view).toBe('Gain')
  })

  it('restores the notes drawn on the chart, in this kind’s own view and quantities', () => {
    const json = JSON.stringify({
      chartAnnotations: [{ id: 1, style: 'point', text: 'Resonance', x: 38, y: 12.2 }],
    })
    expect(responseDisplayFromParams(json).annotations).toEqual([
      {
        id: 1,
        style: 'point',
        text: 'Resonance',
        view: 'Bode',
        x: 38,
        y: 12.2,
        x2: null,
        y2: null,
        xQuantity: 'hz',
        yQuantity: 'dbv',
        channel: null,
        labelOffsetX: 0,
        labelOffsetY: 0,
      },
    ])
  })

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
      chartNicholsAutoGain: false,
      chartNicholsTopDb: 6,
      chartNicholsBottomDb: -6,
      chartPoleZeroAutoWindow: false,
      chartPoleZeroXMinHz: -46000,
      chartPoleZeroXMaxHz: 120,
      chartPoleZeroYMinHz: -38000,
      chartPoleZeroYMaxHz: 38000,
      chartInvertLoopSign: true,
      chartRestoreDelay: true,
      chartShowFittedModel: false,
      chartHideOutOfBandRoots: true,
      chartSurfaceScale: 'LogModulus',
      chartSurfaceMode: 'Relief',
      chartSurfaceTopDb: 20,
      chartSurfaceBottomDb: -80,
      chartSurfacePalette: 'Grey',
      chartSurfaceContours: false,
    })

    expect(responseDisplayFromParams(json)).toEqual({
      // A type that stores no notes restores none -- the annotations are read from this same
      // document, so the field is part of a display's shape like every other one here.
      annotations: [],
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
      nicholsAutoGain: false,
      nicholsTopDb: 6,
      nicholsBottomDb: -6,
      poleZeroAutoWindow: false,
      poleZeroXMinHz: -46000,
      poleZeroXMaxHz: 120,
      poleZeroYMinHz: -38000,
      poleZeroYMaxHz: 38000,
      invertLoopSign: true,
      restoreDelay: true,
      showFittedModel: false,
      hideOutOfBandRoots: true,
      surfaceScale: 'LogModulus',
      surfaceMode: 'Relief',
      surfaceTopDb: 20,
      surfaceBottomDb: -80,
      surfacePalette: 'Grey',
      surfaceContours: false,
    })
  })

  it('keeps the linear s-plane for a type saved before the scale existed', () => {
    // Linear is the default precisely so that nothing already stored changes appearance.
    expect(responseDisplayFromParams(JSON.stringify({ chartView: 'Gain' })).surfaceScale).toBe('Linear')
  })

  it('keeps the heat map for a type saved before the relief existed', () => {
    expect(responseDisplayFromParams(JSON.stringify({ chartView: 'Gain' })).surfaceMode).toBe('Heatmap')
    expect(responseDisplayFromParams(JSON.stringify({ chartSurfaceMode: 'relief' })).surfaceMode).toBe('Heatmap')
  })

  it('ignores an s-plane scale or a palette it does not know', () => {
    expect(responseDisplayFromParams(JSON.stringify({ chartSurfaceScale: 'logmodulus' })).surfaceScale).toBe('Linear')
    // A palette that was removed, or a hand-edited name, must not reach `paletteLut`.
    expect(responseDisplayFromParams(JSON.stringify({ chartSurfacePalette: 'Viridis' })).surfacePalette).toBe('Jet')
    expect(responseDisplayFromParams(JSON.stringify({ chartSurfacePalette: 'Hot' })).surfacePalette).toBe('Hot')
  })

  it('ignores a non-finite surface window', () => {
    // JSON has no NaN, so this arrives as a string or null from a hand-edited type.
    expect(responseDisplayFromParams(JSON.stringify({ chartSurfaceTopDb: 'loud' })).surfaceTopDb).toBe(40)
    expect(responseDisplayFromParams(JSON.stringify({ chartSurfaceBottomDb: null })).surfaceBottomDb).toBe(-60)
  })

  it('ignores a delay view it does not know, so an older or hand-edited type opens with no delay curve', () => {
    expect(responseDisplayFromParams(JSON.stringify({ chartDelayView: 'group' })).delayView).toBe('None')
    expect(responseDisplayFromParams(JSON.stringify({ chartDelayView: 'Phase' })).delayView).toBe('Phase')
    expect(responseDisplayFromParams(JSON.stringify({ chartDelayTopUs: 'big' })).delayTopUs).toBe(1000)
  })

  /**
   * The two axis windows are the one pair of fields whose *absence* has to mean something specific:
   * every build before them fitted both axes, so a type stored then must keep doing that rather
   * than opening on a ±100 kHz plane with its roots in the origin marker.
   */
  it('keeps both complex-plane windows fitted for a type saved before the controls existed', () => {
    const display = responseDisplayFromParams(JSON.stringify({ chartView: 'Gain', chartProjection: 'Nichols' }))

    expect(display.nicholsAutoGain).toBe(true)
    expect(display.poleZeroAutoWindow).toBe(true)
  })

  /**
   * The default change of 2026-10-06: a type stored before the box existed picks the new default up,
   * while one that was explicitly saved with it off keeps it off. That is the whole contract of a
   * guarded restore, and this is the first setting whose default has moved since.
   */
  it('hides out-of-band roots by default, and still honours a stored false', () => {
    expect(responseDisplayFromParams(JSON.stringify({ chartView: 'Gain' })).hideOutOfBandRoots).toBe(true)
    expect(responseDisplayFromParams(JSON.stringify({ chartHideOutOfBandRoots: false })).hideOutOfBandRoots).toBe(false)
  })

  it('ignores a non-finite axis window', () => {
    expect(responseDisplayFromParams(JSON.stringify({ chartNicholsTopDb: 'loud' })).nicholsTopDb).toBe(20)
    expect(responseDisplayFromParams(JSON.stringify({ chartPoleZeroXMinHz: null })).poleZeroXMinHz).toBe(-100000)
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
