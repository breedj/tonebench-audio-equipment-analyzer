import { describe, expect, it } from 'vitest'
import { overviewSpan, type Span } from './overviewSpan'

const cols = (lo: number, hi: number) => [{ freqHz: lo }, { freqHz: (lo + hi) / 2 }, { freqHz: hi }]
const NONE: Span = { lo: 0, hi: -1 }

describe('overviewSpan', () => {
  it('takes the span from an un-windowed source', () => {
    expect(overviewSpan(NONE, cols(10.53, 43011), null)).toEqual({ lo: 10.53, hi: 43011 })
  })

  it('keeps the known span when the source was fetched for a window', () => {
    const known = { lo: 10.53, hi: 43011 }
    expect(overviewSpan(known, cols(10, 20000), { loFreqHz: 10, hiFreqHz: 20000 })).toEqual(known)
  })

  it('re-fits when a later source arrives un-windowed', () => {
    const known = { lo: 10.53, hi: 43011 }
    expect(overviewSpan(known, cols(2.93, 96000), null)).toEqual({ lo: 2.93, hi: 96000 })
  })

  // First render: a windowed overlay is the only thing there is, and the chart needs something to
  // scale to. A slightly narrow span beats none.
  it('takes a windowed span when no span is known yet', () => {
    expect(overviewSpan(NONE, cols(10, 20000), { loFreqHz: 10, hiFreqHz: 20000 })).toEqual({ lo: 10, hi: 20000 })
  })

  it('keeps the known span when there are no columns to read', () => {
    const known = { lo: 10.53, hi: 43011 }
    expect(overviewSpan(known, [], null)).toEqual(known)
    expect(overviewSpan(known, null, null)).toEqual(known)
  })

  /**
   * The regression, stated as the loop it prevents rather than as a property.
   *
   * This replays the cycle that actually happened: the chart compares its axis against the span,
   * reports a zoom when the axis is narrower, the overlay is re-fetched for that window, and the
   * span is recomputed. With the rule, the span holds and the report stops changing after one
   * round. Without it the two states alternate for ever — which is what the `toEqual` on the
   * second iteration pins.
   */
  it('settles instead of alternating when the axis is narrower than the data', () => {
    const axis = { lo: 10, hi: 20000 }
    const fullColumns = cols(10.53, 43011)
    const windowColumns = cols(10, 20000)

    let span = overviewSpan(NONE, fullColumns, null)
    const reports: (string | null)[] = []
    let fetched: SpectrumWindowLike = null

    for (let i = 0; i < 6; i++) {
      // What reportViewport decides: zoomed unless the axis covers the span.
      const atFullSpan = axis.lo <= span.lo * 1.001 && axis.hi >= span.hi * 0.999
      reports.push(atFullSpan ? null : `${axis.lo}-${axis.hi}`)

      // What the re-fetch then delivers.
      fetched = atFullSpan ? null : { loFreqHz: axis.lo, hiFreqHz: axis.hi }
      span = overviewSpan(span, fetched ? windowColumns : fullColumns, fetched)
    }

    // One report, then silence: every later pass reaches the same decision.
    expect(new Set(reports).size).toBe(1)
    expect(reports[0]).toBe('10-20000')
  })
})

type SpectrumWindowLike = { loFreqHz: number; hiFreqHz: number } | null
