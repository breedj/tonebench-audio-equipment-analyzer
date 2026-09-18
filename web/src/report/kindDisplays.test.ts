import { describe, expect, it } from 'vitest'
import { DEFAULT_IMPEDANCE_DISPLAY, impedanceDisplayFromParams } from '../impedance/display'
import { DEFAULT_IMPULSE_DISPLAY, defaultImpulseDisplay, impulseDisplayFromParams } from '../measurements/impulseDisplay'
import { defaultSweepDisplay, sweepChartWindow, sweepDisplayFromParams } from '../sweep/display'

describe('sweepDisplayFromParams', () => {
  it('restores a frequency sweep and leaves the amplitude windows alone', () => {
    const display = sweepDisplayFromParams(
      JSON.stringify({ chartUnit: 'db', chartShowD4: true, chartShowLeft: false, chartXFromHz: 20, chartXToHz: 40000, chartYTopDb: 0 }),
    )
    expect(display.unit).toBe('db')
    expect(display.showD4).toBe(true)
    expect(display.showLeft).toBe(false)
    expect(sweepChartWindow(display, 'ThdVsFrequency')).toEqual({ xAxis: 'frequency', xFrom: 20, xTo: 40000 })
    expect(display.amplitudeBounds.outputVolt).toEqual({ from: 0.001, to: 10 })
  })

  it('lands an amplitude sweep window on the axis it was stored for', () => {
    const display = sweepDisplayFromParams(JSON.stringify({ chartXAxis: 'outputWatt', chartXFrom: 0.01, chartXTo: 50 }))
    expect(sweepChartWindow(display, 'ThdVsAmplitude')).toEqual({ xAxis: 'outputWatt', xFrom: 0.01, xTo: 50 })
    expect(display.amplitudeBounds.outputVolt).toEqual({ from: 0.001, to: 10 })
  })

  it('never shares the bounds object with its base, and survives bad JSON', () => {
    const base = defaultSweepDisplay()
    const display = sweepDisplayFromParams('{', base)
    display.amplitudeBounds.outputVolt.from = 5
    expect(base.amplitudeBounds.outputVolt.from).toBe(0.001)
    expect(sweepDisplayFromParams(null)).toEqual(defaultSweepDisplay())
  })
})

describe('impedanceDisplayFromParams', () => {
  it('restores the chart fields and guards the ohm bounds against the scale', () => {
    const display = impedanceDisplayFromParams(
      JSON.stringify({ chartView: 'Nyquist', chartZScale: 'Log', chartZTopOhms: 1000, chartZBottomOhms: 0, chartShowPhase: false, chartXFromHz: 5 }),
    )
    expect(display.view).toBe('Nyquist')
    expect(display.zScale).toBe('Log')
    expect(display.zTopOhms).toBe(1000)
    // Zero is not a log-axis bound; the default stays.
    expect(display.zBottomOhms).toBe(1)
    expect(display.showPhase).toBe(false)
    expect(display.xFromHz).toBe(5)
  })

  it('reads an unknown scale as linear and accepts a zero bottom there', () => {
    const display = impedanceDisplayFromParams(JSON.stringify({ chartZScale: 'Weird', chartZBottomOhms: 0 }))
    expect(display.zScale).toBe('Linear')
    expect(display.zBottomOhms).toBe(0)
    expect(impedanceDisplayFromParams(null)).toEqual(DEFAULT_IMPEDANCE_DISPLAY)
  })
})

describe('impulseDisplayFromParams', () => {
  it("restores every view's fields over the base", () => {
    const display = impulseDisplayFromParams(
      JSON.stringify({
        chartNormalisedImpulse: true,
        chartTimeFromMs: -2,
        chartTimeToMs: 50,
        chartYBottomDb: -80,
        chartResponseYTopDb: 12,
        chartDecayMode: 'Sonogram',
        chartCsdFftLength: 1024,
        chartBurstResolution: 'PreferTime',
      }),
    )
    expect(display).toEqual({
      ...DEFAULT_IMPULSE_DISPLAY,
      normalised: true,
      timeFromMs: -2,
      timeToMs: 50,
      yBottomDb: -80,
      responseYTopDb: 12,
      decayMode: 'Sonogram',
      csd: { ...DEFAULT_IMPULSE_DISPLAY.csd, fftLength: 1024 },
      burstResolution: 'PreferTime',
    })
    expect(impulseDisplayFromParams('nope')).toEqual(DEFAULT_IMPULSE_DISPLAY)
  })

  it('takes the CSD defaults from the sample rate it is given', () => {
    const at192k = defaultImpulseDisplay(192000)
    expect(at192k.csd.fftLength).toBeGreaterThan(DEFAULT_IMPULSE_DISPLAY.csd.fftLength)
    // Never shares the nested analysis object with its base.
    const display = impulseDisplayFromParams(null, at192k)
    display.csd.fftLength = 1
    expect(at192k.csd.fftLength).not.toBe(1)
  })
})
