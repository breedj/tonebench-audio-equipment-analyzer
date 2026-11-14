import { readdirSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { RESPONSE_SPANS_HZ } from './api/client'
import { sampleRateForSpan, spanFallback, supportedSpans } from './supportedSpans'

const ALL = [24000, 48000, 96000] as const

/**
 * Every span must imply a rate in the **48 kHz family**, and that is the hard rule rather than a
 * preference.
 *
 * <p>A response measurement's transform length is `round(rate / BaseBinHz)` with `BaseBinHz` =
 * 48000/32768, which is exactly what makes 48, 96 and 192 kHz land on 32768, 65536 and 131072. The
 * 44.1 kHz family lands on 30106, 60211 and 120422, none of them a power of two, and the FFT
 * backend throws on those — so a 44.1 kHz span fails the run with "Input length must be a power of
 * two" and names nothing the user chose.</p>
 *
 * <p>Written after exactly that was tried and reverted on 2026-11-14. Adding 44.1 kHz spans looked
 * right (the family had just been added to the rate probe), typechecked, and passed every test
 * there was — the app answering `fftSize: 30106` to a 22.05 kHz span is what caught it. This test
 * is so the next person does not have to run it to find out.</p>
 */
describe('the span list', () => {
  it('only ever implies a power-of-two transform length', () => {
    const BASE_BIN_HZ = 48000 / 32768

    for (const span of RESPONSE_SPANS_HZ) {
      const rate = sampleRateForSpan(span)
      const fftSize = Math.round(rate / BASE_BIN_HZ)

      expect(
        fftSize & (fftSize - 1),
        `a ${span} Hz span implies a ${rate} Hz rate and a transform length of ${fftSize}, ` +
          'which is not a power of two — the FFT backend throws on it',
      ).toBe(0)
    }
  })

  it('is ascending, so the dropdown reads in order', () => {
    expect([...RESPONSE_SPANS_HZ]).toEqual([...RESPONSE_SPANS_HZ].sort((a, b) => a - b))
  })
})

/**
 * Every span dropdown in the app filters by the connected analyzer's rates.
 *
 * <p><b>This is the audit, and it is the point of the file.</b> `supportedSpans` existed and was
 * wired to exactly one of the four panels that need it: the frequency-response panel filtered,
 * while the impedance and impulse panels and the loopback-reference dialog iterated the raw list
 * and so offered spans the server then refused with a 409 — its own docstring said "three panels
 * ask the same question" and only one did. That was invisible while every analyzer had the QA403's
 * three rates, and became reachable the moment the 44.1 kHz family was probed on 2026-11-14.</p>
 *
 * <p>Nothing else notices: the dropdown is markup in a `.vue`, which is not mountable under this
 * suite's node environment, so this reads the templates as text the way `numberField.test.ts`
 * audits number inputs.</p>
 */
describe('every span dropdown filters by the analyzer', () => {
  const dir = fileURLToPath(new URL('./components', import.meta.url))

  it('iterates a filtered list rather than the raw constant', () => {
    const offenders: string[] = []

    for (const name of readdirSync(dir).filter((f) => f.endsWith('.vue'))) {
      const text = readFileSync(`${dir}/${name}`, 'utf8')
      if (/v-for="span in RESPONSE_SPANS_HZ"/.test(text)) offenders.push(name)
    }

    expect(offenders, 'these offer spans the analyzer may not have').toEqual([])
  })

  it('finds the dropdowns it is auditing, so a passing run means something', () => {
    const withDropdown = readdirSync(dir)
      .filter((f) => f.endsWith('.vue'))
      .filter((name) => /v-for="span in \w+"/.test(readFileSync(`${dir}/${name}`, 'utf8')))

    // Four: the frequency-response, impedance and impulse-response config panels, and the
    // loopback-reference dialog. A fifth is why the audit above exists.
    expect(withDropdown.length).toBeGreaterThanOrEqual(4)
  })
})

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

  // Refusing every span would be a worse answer than letting the server say why. The example is a
  // rate in neither family — 44.1 kHz used to stand here and stopped being a "matches nothing" case
  // on 2026-11-14, when its family was probed and the 22.05 kHz span added to meet it.
  it('supportedSpans_WhenNothingMatches_FallsBackToEverySpan', () => {
    expect(supportedSpans(ALL, [32000])).toEqual([...ALL])
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
