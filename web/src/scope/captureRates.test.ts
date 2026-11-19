import { describe, expect, it } from 'vitest'
import {
  MIN_RATE_SPAN_MS,
  RATE_WINDOW_MS,
  captureRateLabel,
  captureRates,
  pushRateSample,
  type ScopeRateSample,
} from './captureRates'

/** A 120 ms cadence, which is what the status poll really runs at. */
function feed(
  perPoll: { committed: number; collected: number; drawn: number },
  polls: number,
  stepMs = 120,
): ScopeRateSample[] {
  let samples: ScopeRateSample[] = []
  let committed = 0
  let collected = 0
  let drawn = 0
  for (let i = 0; i <= polls; i++) {
    samples = pushRateSample(samples, { atMs: i * stepMs, committed, collected, drawn })
    committed += perPoll.committed
    collected += perPoll.collected
    drawn += perPoll.drawn
  }
  return samples
}

describe('captureRates', () => {
  it('reads the three rates off a realistic poll cadence', () => {
    // Three committed and one drawn per 120 ms poll is 25/s against 8.3/s -- the gap the whole
    // diagnostic exists to show.
    const rates = captureRates(feed({ committed: 3, collected: 3, drawn: 1 }, 17))
    expect(rates).not.toBeNull()
    expect(rates!.committedPerS).toBeCloseTo(25, 1)
    expect(rates!.collectedPerS).toBeCloseTo(25, 1)
    expect(rates!.drawnPerS).toBeCloseTo(8.33, 1)
  })

  it('carries the counts beside the rates', () => {
    // The tooltip states both. Deriving a count from a rate would disagree with the server's own
    // log line, which counts rather than divides.
    const rates = captureRates(feed({ committed: 3, collected: 2, drawn: 1 }, 10))!
    expect(rates.committed).toBe(30)
    expect(rates.collected).toBe(20)
    expect(rates.drawn).toBe(10)
    expect(rates.spanMs).toBeCloseTo(1200, 6)
  })

  it('shows the loss between committing and collecting', () => {
    // The sound-card case: the engine commits faster than the 10 ms poll collects, so records are
    // replaced before anything takes them -- and are therefore neither counted nor averaged.
    const rates = captureRates(feed({ committed: 20, collected: 12, drawn: 1 }, 17))!
    expect(rates.committedPerS).toBeGreaterThan(rates.collectedPerS)
    expect(rates.collectedPerS).toBeGreaterThan(rates.drawnPerS)
  })

  it('answers null below one sample', () => {
    expect(captureRates([])).toBeNull()
    expect(captureRates([{ atMs: 0, committed: 0, collected: 0, drawn: 0 }])).toBeNull()
  })

  it('answers null until the window is long enough to divide by', () => {
    // A rate from a 120 ms span is one poll's jitter wearing a per-second unit.
    const short = feed({ committed: 3, collected: 3, drawn: 1 }, 2)
    expect(short.at(-1)!.atMs - short[0]!.atMs).toBeLessThan(MIN_RATE_SPAN_MS)
    expect(captureRates(short)).toBeNull()
  })

  it('never reports a negative rate when a counter restarts', () => {
    // A new run, an adopted owner or a Clear zeroes all three. Subtracting the old large number
    // from the new small one would read as the instrument running backwards.
    const samples = feed({ committed: 3, collected: 3, drawn: 1 }, 17)
    const restarted = pushRateSample(samples, { atMs: 2040, committed: 0, collected: 0, drawn: 0 })

    expect(restarted).toHaveLength(1)
    expect(captureRates(restarted)).toBeNull()
  })

  it('restarts the window when only one of the three goes backwards', () => {
    const samples = feed({ committed: 3, collected: 3, drawn: 1 }, 17)
    const last = samples.at(-1)!
    const restarted = pushRateSample(samples, { ...last, atMs: last.atMs + 120, drawn: 0 })
    expect(restarted).toHaveLength(1)
  })

  it('drops samples older than the window', () => {
    const samples = feed({ committed: 3, collected: 3, drawn: 1 }, 60)
    expect(samples.at(-1)!.atMs - samples[0]!.atMs).toBeLessThanOrEqual(RATE_WINDOW_MS)
    // Still long enough to be a rate, which is what the window is sized for.
    expect(captureRates(samples)).not.toBeNull()
  })

  it('keeps two samples however old, so a slow poll still reads', () => {
    // A long record commits a few times a second; the window must not starve itself down to one
    // sample and report nothing at all.
    let samples: ScopeRateSample[] = []
    samples = pushRateSample(samples, { atMs: 0, committed: 0, collected: 0, drawn: 0 })
    samples = pushRateSample(samples, { atMs: 9000, committed: 20, collected: 20, drawn: 20 })
    expect(samples).toHaveLength(2)
    expect(captureRates(samples)!.drawnPerS).toBeCloseTo(20 / 9, 3)
  })
})

describe('captureRateLabel', () => {
  it('writes the three rates in the order they fall', () => {
    const rates = captureRates(feed({ committed: 3, collected: 3, drawn: 1 }, 17))!
    expect(captureRateLabel(rates)).toBe('25 → 25 → 8.3 /s')
  })

  it('keeps a decimal on a slow rate and drops it on a fast one', () => {
    // A 500 ms record commits two or three a second; rounding that to an integer would make a real
    // change invisible, while 47.3 on the fast end is false precision.
    const slow = captureRates(feed({ committed: 1, collected: 1, drawn: 1 }, 17, 400))!
    expect(captureRateLabel(slow)).toBe('2.5 → 2.5 → 2.5 /s')
  })
})
