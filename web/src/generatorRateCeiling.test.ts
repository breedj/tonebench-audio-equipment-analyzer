import { describe, expect, it } from 'vitest'
import { generatorRateCeilingNotice } from './generatorRateCeiling'

const QA40X = { maxGeneratorRateHz: 192_000, generatorRatesHz: [48_000, 96_000, 192_000] }

describe('generatorRateCeilingNotice', () => {
  // A sound card plays at every rate it captures at, and the server sends null for it. Infinity is
  // the same answer arriving from a server that does not send the field — comparing against it
  // would make every rate "above the ceiling".
  it('on an analyzer with no ceiling and no ladder, says nothing', () => {
    expect(generatorRateCeilingNotice({ stagedSampleRateHz: 384_000 })).toBeNull()
    expect(
      generatorRateCeilingNotice({ stagedSampleRateHz: 384_000, maxGeneratorRateHz: null }),
    ).toBeNull()
    expect(
      generatorRateCeilingNotice({
        stagedSampleRateHz: 384_000,
        maxGeneratorRateHz: Number.POSITIVE_INFINITY,
      }),
    ).toBeNull()
  })

  it('at or below the ceiling, says nothing', () => {
    for (const rate of [48_000, 96_000, 192_000]) {
      expect(generatorRateCeilingNotice({ stagedSampleRateHz: rate, ...QA40X })).toBeNull()
    }
  })

  it('with a nonsense rate, says nothing', () => {
    expect(generatorRateCeilingNotice({ stagedSampleRateHz: Number.NaN, ...QA40X })).toBeNull()
  })

  /**
   * The advice, which is the half that was wrong until 2026-11-14.
   *
   * It read "Use an external generator" — written when a second device was something to go and
   * find. A split analyzer makes it a dropdown, so the notice names the lever this bench has.
   */
  it('above the ceiling on a single analyzer, points at the split analyzer', () => {
    const notice = generatorRateCeilingNotice({ stagedSampleRateHz: 384_000, ...QA40X })

    expect(notice).toContain('384 kHz')
    expect(notice).toContain('input-only rate')
    expect(notice).toContain('Split (two devices)')
    expect(notice).not.toContain('external generator')
  })

  it('above the ceiling on a split, points at its own Generator rate', () => {
    const notice = generatorRateCeilingNotice({
      stagedSampleRateHz: 384_000,
      ...QA40X,
      isSplit: true,
    })

    expect(notice).toContain('Generator rate')
    expect(notice).not.toContain('Split (two devices)')
  })

  /**
   * And the half that was wrong until 2026-11-22, reported from the bench: it offered to turn the
   * generator off, which is a switch inside the panel this very notice has replaced -- so it named
   * a control that is not on screen, to do a thing already done. What the reader needs instead is
   * the fact that was missing, which is that the capture still runs.
   */
  it('never offers to turn the generator off, and says the capture runs without it', () => {
    for (const bench of [
      { stagedSampleRateHz: 384_000, ...QA40X },
      { stagedSampleRateHz: 384_000, ...QA40X, isSplit: true },
      {
        stagedSampleRateHz: 384_000,
        generatorRatesHz: [48_000, 96_000, 192_000],
        maxGeneratorRateHz: null,
        isSplit: true,
      },
    ]) {
      const notice = generatorRateCeilingNotice(bench) as string
      expect(notice, JSON.stringify(bench)).not.toContain('turn the generator off')
      expect(notice, JSON.stringify(bench)).toContain('without it')
    }
  })

  /**
   * The second refusal, and the one actually met on a bench: a split whose generating half is a
   * sound card that stops at 192 kHz, with the capture staged at 384. Nothing said this before Run
   * — the server's own 409 was the first the user heard of it.
   */
  it('at a rate the generating half does not have, names what it does have', () => {
    const notice = generatorRateCeilingNotice({
      stagedSampleRateHz: 384_000,
      generatorRatesHz: [48_000, 96_000, 192_000],
      maxGeneratorRateHz: null,
      isSplit: true,
    })

    expect(notice).toContain('cannot run at 384 kHz')
    expect(notice).toContain('48, 96, 192 kHz')
    expect(notice).toContain('Generator rate')
  })

  /**
   * And a pinned generator plays its own rate whatever the capture is staged at, which is the whole
   * point of the split's Generator rate: a QA403 capturing at 384 kHz while a DAC plays at 192 is
   * the measurement, not a fault.
   */
  it('with the generator pinned, says nothing however the capture is staged', () => {
    expect(
      generatorRateCeilingNotice({
        stagedSampleRateHz: 384_000,
        generatorRateOverrideHz: 192_000,
        generatorRatesHz: [48_000, 96_000, 192_000],
        isSplit: true,
      }),
    ).toBeNull()
  })

  it('with an empty ladder, warns nothing — that is "not known", not "nothing works"', () => {
    expect(
      generatorRateCeilingNotice({ stagedSampleRateHz: 384_000, generatorRatesHz: [] }),
    ).toBeNull()
  })
})
