import { describe, expect, it } from 'vitest'
import { RESPONSE_SPANS_HZ } from '../api/client'
import {
  IMPEDANCE_STEPPED_ALTERNATIVE,
  STEPPED_SINE_ALTERNATIVE,
  isPeriodicRatePair,
  mixedRatePairNotice,
} from './ratePairing'

/** Every rate a card or a QA403 can be driven at, both families. */
const FORTY_EIGHT = [48000, 96000, 192000, 384000, 768000]
const FORTY_FOUR_ONE = [44100, 88200, 176400]

describe('isPeriodicRatePair', () => {
  it('holds within a family, at every pairing the app can offer', () => {
    for (const family of [FORTY_EIGHT, FORTY_FOUR_ONE]) {
      for (const a of family) {
        for (const b of family) {
          expect(isPeriodicRatePair(a, b), `${a} against ${b}`).toBe(true)
        }
      }
    }
  })

  // The property the whole feature turns on: no pairing across the families is an integer ratio,
  // which is why a mixed bench is permanently tone-only rather than awaiting a fix.
  it('never holds across them', () => {
    for (const a of FORTY_EIGHT) {
      for (const b of FORTY_FOUR_ONE) {
        expect(isPeriodicRatePair(a, b), `${a} against ${b}`).toBe(false)
        expect(isPeriodicRatePair(b, a), `${b} against ${a}`).toBe(false)
      }
    }
  })

  it('refuses a rate that is not a positive number', () => {
    expect(isPeriodicRatePair(0, 48000)).toBe(false)
    expect(isPeriodicRatePair(48000, -1)).toBe(false)
    expect(isPeriodicRatePair(Number.NaN, 48000)).toBe(false)
    expect(isPeriodicRatePair(48000, Number.POSITIVE_INFINITY)).toBe(false)
  })
})

describe('mixedRatePairNotice', () => {
  const across = { generatorRateHz: 44100, captureRateHz: 192000, alternative: STEPPED_SINE_ALTERNATIVE }

  it('says so across the families, naming both rates and the way out', () => {
    const notice = mixedRatePairNotice(across)

    expect(notice).toContain('44.1 kHz')
    expect(notice).toContain('192 kHz')
    expect(notice).toContain('not an integer ratio')
    expect(notice).toContain('stepped sine')
  })

  it('says nothing within one family', () => {
    expect(mixedRatePairNotice({ ...across, generatorRateHz: 48000 })).toBeNull()
    expect(mixedRatePairNotice({ ...across, generatorRateHz: 192000 })).toBeNull()
    expect(mixedRatePairNotice({ ...across, generatorRateHz: 384000 })).toBeNull()
  })

  /**
   * The case that matters most, because it is every bench that is not a split: a generator with no
   * rate of its own follows the capture rate, so there is only one clock's rate in play and nothing
   * to warn about. A notice here would fire on every measurement in the app.
   */
  it('says nothing when the generator follows the capture rate', () => {
    for (const generatorRateHz of [0, null, undefined]) {
      expect(mixedRatePairNotice({ ...across, generatorRateHz })).toBeNull()
    }
  })

  it('says nothing when the capture rate is not known yet', () => {
    // The ranges are fetched asynchronously, and a panel that red-flags itself on load reads as
    // broken rather than as not-yet-loaded.
    expect(mixedRatePairNotice({ ...across, captureRateHz: 0 })).toBeNull()
    expect(mixedRatePairNotice({ ...across, captureRateHz: Number.NaN })).toBeNull()
  })

  /**
   * Every span this app offers is in the 48 kHz family, so a 44.1 kHz family generator is refused
   * at every one of them — there is no span a user can retreat to, which is exactly why the
   * alternative names a different *measurement* rather than a different span.
   */
  it('fires at every offered span when the generator is in the other family', () => {
    for (const span of RESPONSE_SPANS_HZ) {
      for (const generatorRateHz of FORTY_FOUR_ONE) {
        expect(
          mixedRatePairNotice({ generatorRateHz, captureRateHz: span * 2, alternative: STEPPED_SINE_ALTERNATIVE }),
          `${generatorRateHz} Hz against a ${span} Hz span`,
        ).not.toBeNull()
      }
    }
  })

  it('carries the impedance screen its own way out', () => {
    expect(mixedRatePairNotice({ ...across, alternative: IMPEDANCE_STEPPED_ALTERNATIVE }))
      .toContain('Set Stimulus to Stepped sine')
  })
})
