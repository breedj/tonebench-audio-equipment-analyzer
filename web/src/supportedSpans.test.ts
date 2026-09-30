import { describe, expect, it } from 'vitest'
import { sampleRateForSpan, spanFallback, supportedSpans } from './supportedSpans'

const ALL = [24000, 48000, 96000] as const

describe('supportedSpans', () => {
  it('sampleRateForSpan_IsTwiceTheSpan', () => {
    expect(sampleRateForSpan(24000)).toBe(48000)
    expect(sampleRateForSpan(96000)).toBe(192000)
  })

  // The QA403 has all three, which is why this was a constant for so long.
  it('supportedSpans_OnAnAnalyzerWithEveryRate_OffersEverySpan', () => {
    expect(supportedSpans(ALL, [48000, 96000, 192000])).toEqual([24000, 48000, 96000])
  })

  // A card that tops out at 48 kHz can only be analysed to 24 kHz.
  it('supportedSpans_OnACardWithOneRate_OffersOnlyItsSpan', () => {
    expect(supportedSpans(ALL, [48000])).toEqual([24000])
  })

  it('supportedSpans_OnACardMissingTheTopRate_DropsOnlyThatSpan', () => {
    expect(supportedSpans(ALL, [48000, 96000])).toEqual([24000, 48000])
  })

  // The ranges are fetched asynchronously; an empty dropdown on load reads as a broken panel.
  it('supportedSpans_BeforeTheRatesAreKnown_OffersEverySpan', () => {
    expect(supportedSpans(ALL, null)).toEqual([...ALL])
    expect(supportedSpans(ALL, undefined)).toEqual([...ALL])
    expect(supportedSpans(ALL, [])).toEqual([...ALL])
  })

  // Refusing every span would be a worse answer than letting the server say why.
  it('supportedSpans_WhenNothingMatches_FallsBackToEverySpan', () => {
    expect(supportedSpans(ALL, [44100])).toEqual([...ALL])
  })
})

describe('spanFallback', () => {
  it('spanFallback_WhenTheCurrentSpanIsAvailable_ChangesNothing', () => {
    expect(spanFallback(48000, [24000, 48000])).toBeNull()
  })

  // The largest available, so the band on screen shrinks rather than the panel keeping a value the
  // server will refuse.
  it('spanFallback_WhenTheCurrentSpanIsGone_PicksTheLargestAvailable', () => {
    expect(spanFallback(96000, [24000, 48000])).toBe(48000)
    expect(spanFallback(96000, [24000])).toBe(24000)
  })

  it('spanFallback_WithNothingAvailable_ChangesNothing', () => {
    expect(spanFallback(96000, [])).toBeNull()
  })
})
