import { describe, expect, it } from 'vitest'
import { curveOverlayScreen, type CurveOverlayScreen } from './overlayScope'
import {
  CROSSTALK_KIND,
  FFT_SPECTRUM_KIND,
  FREQUENCY_RESPONSE_KIND,
  IMD_VS_AMPLITUDE_KIND,
  IMPEDANCE_KIND,
  IMPULSE_RESPONSE_KIND,
  MEASUREMENT_KINDS,
  OUTPUT_IMPEDANCE_KIND,
  STEPPED_RESPONSE_KIND,
  THD_VS_AMPLITUDE_KIND,
  THD_VS_FREQUENCY_KIND,
} from './kinds'

describe('curveOverlayScreen', () => {
  /**
   * The report of 2026-10-23: run a chirp, check one of its results on, open a stepped measurement,
   * and the chirp's curve was on that chart — with no row in its History and no overlay list on
   * that screen, so nothing could take it off again. They measure the same curve and shared a
   * screen for it; the comparison is not worth an overlay that cannot be dismissed.
   */
  it('keeps a chirp off the stepped sine’s screen, though they measure the same curve', () => {
    expect(curveOverlayScreen(FREQUENCY_RESPONSE_KIND)).toBe('Response')
    expect(curveOverlayScreen(STEPPED_RESPONSE_KIND)).toBe('SteppedResponse')
    expect(curveOverlayScreen(STEPPED_RESPONSE_KIND)).not.toBe(curveOverlayScreen(FREQUENCY_RESPONSE_KIND))
  })

  /**
   * The reported bug: an impulse result's gated response is a `Left` and a `Gain` curve, so every
   * filter short of the kind matched it from the frequency-response screen and drew it there.
   */
  it('keeps an impulse response off the frequency-response screen, though its curves are the same kinds', () => {
    expect(curveOverlayScreen(IMPULSE_RESPONSE_KIND)).toBe('ImpulseResponse')
    expect(curveOverlayScreen(IMPULSE_RESPONSE_KIND)).not.toBe(curveOverlayScreen(FREQUENCY_RESPONSE_KIND))
  })

  it('keeps an amplifier’s output impedance off a loudspeaker’s impedance screen', () => {
    expect(curveOverlayScreen(IMPEDANCE_KIND)).toBe('Impedance')
    expect(curveOverlayScreen(OUTPUT_IMPEDANCE_KIND)).toBe('OutputImpedance')
  })

  it('gives crosstalk a screen of its own', () => {
    expect(curveOverlayScreen(CROSSTALK_KIND)).toBe('Crosstalk')
  })

  it('claims nothing for a kind that stores no curve — an FFT capture or a sweep', () => {
    expect(curveOverlayScreen(FFT_SPECTRUM_KIND)).toBeNull()
    expect(curveOverlayScreen(THD_VS_FREQUENCY_KIND)).toBeNull()
    expect(curveOverlayScreen(THD_VS_AMPLITUDE_KIND)).toBeNull()
    expect(curveOverlayScreen(IMD_VS_AMPLITUDE_KIND)).toBeNull()
    expect(curveOverlayScreen(null)).toBeNull()
    expect(curveOverlayScreen(undefined)).toBeNull()
  })

  /**
   * The property the whole function exists for: every kind the app can create lands on at most one
   * screen. A new kind that stores `response_trace` rows and is forgotten here draws nowhere, which
   * is visible; one that answers for two screens draws on someone else's chart, which is not.
   */
  it('partitions every kind the app offers — no kind belongs on two screens', () => {
    const byScreen = new Map<CurveOverlayScreen, string[]>()

    for (const { kind } of MEASUREMENT_KINDS) {
      const screen = curveOverlayScreen(kind)
      if (screen === null) continue
      byScreen.set(screen, [...(byScreen.get(screen) ?? []), kind])
    }

    expect(Object.fromEntries(byScreen)).toEqual({
      Response: [FREQUENCY_RESPONSE_KIND],
      SteppedResponse: [STEPPED_RESPONSE_KIND],
      ImpulseResponse: [IMPULSE_RESPONSE_KIND],
      Impedance: [IMPEDANCE_KIND],
      Crosstalk: [CROSSTALK_KIND],
      OutputImpedance: [OUTPUT_IMPEDANCE_KIND],
    })
  })
})
