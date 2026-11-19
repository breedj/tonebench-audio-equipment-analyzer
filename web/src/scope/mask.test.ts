import { describe, expect, it } from 'vitest'
import { addMaskVerdict, buildScopeMask, maskVerdict, newMaskTally } from './mask'

describe('buildScopeMask', () => {
  it('is the reference plus and minus the level tolerance where it is flat', () => {
    const mask = buildScopeMask([1, 1, 1, 1], 1e-6, 0.1, 0)

    expect(mask.upper).toEqual([1.1, 1.1, 1.1, 1.1])
    expect(mask.lower).toEqual([0.9, 0.9, 0.9, 0.9])
    expect(mask.columns).toBe(4)
  })

  /**
   * The whole reason the band is a dilation rather than two offset copies: near an edge a couple of
   * microseconds is most of the amplitude, so a time tolerance has to open the band to the height
   * of the edge.
   */
  it('opens to the height of an edge where the time tolerance reaches across it', () => {
    //                      0  1  2  3
    const reference = [0, 0, 1, 1]

    const mask = buildScopeMask(reference, 1, 0, 1)

    // Column 1 sees the step at column 2, so its band spans the whole edge.
    expect(mask.upper[1]).toBe(1)
    expect(mask.lower[1]).toBe(0)
    // Column 0 does not reach it.
    expect(mask.upper[0]).toBe(0)
    expect(mask.lower[0]).toBe(0)
  })

  it('adds the two tolerances rather than choosing between them', () => {
    const mask = buildScopeMask([0, 0, 1, 1], 1, 0.25, 1)

    expect(mask.upper[1]).toBe(1.25)
    expect(mask.lower[1]).toBe(-0.25)
  })

  it('reads a time tolerance in seconds through the column spacing', () => {
    // 5 µs either side of a 1 µs column is five columns.
    const reference = [0, 0, 0, 0, 0, 0, 1]
    const mask = buildScopeMask(reference, 1e-6, 0, 5e-6)

    expect(mask.upper[1]).toBe(1)
    expect(mask.upper[0]).toBe(0)
  })

  /** A tolerance finer than a column is a tolerance this picture cannot express. */
  it('rounds a sub-column time tolerance away rather than pretending to honour it', () => {
    const mask = buildScopeMask([0, 1], 1e-6, 0, 1e-7)

    expect(mask.upper[0]).toBe(0)
  })

  it('refuses to be widened by a negative or absent tolerance', () => {
    expect(buildScopeMask([1], 1e-6, -5, -5).upper).toEqual([1])
    expect(buildScopeMask([1], 1e-6, Number.NaN, Number.NaN).upper).toEqual([1])
  })

  /**
   * Absent is null, never a number: a band filled in from the neighbours would be a tolerance
   * around a stretch the reference was never measured over.
   */
  it('has no band where the reference has no value', () => {
    const mask = buildScopeMask([1, null, 1], 1, 0.1, 0)

    expect(mask.upper[1]).toBeNull()
    expect(mask.lower[1]).toBeNull()
    expect(mask.columns).toBe(2)
  })

  it('skips a gap inside the dilation window rather than being stopped by it', () => {
    const mask = buildScopeMask([0, 1, null, 3], 1, 0, 1)

    // Column 3 looks at 2 (absent) and 3, so its band is its own value.
    expect(mask.upper[3]).toBe(3)
    // Column 1 looks at 0, 1 and 2 (absent).
    expect(mask.upper[1]).toBe(1)
    expect(mask.lower[1]).toBe(0)
  })
})

describe('maskVerdict', () => {
  const mask = buildScopeMask([0, 0, 0, 0], 1, 1, 0)

  it('passes a trace inside the band', () => {
    const verdict = maskVerdict(mask, [0.5, -0.5, 0, 0.9])

    expect(verdict.checked).toBe(4)
    expect(verdict.failed).toBe(0)
    expect(verdict.firstFailIndex).toBeNull()
    expect(verdict.worstExcess).toBe(0)
  })

  it('reports where it first left the band and how far outside it went', () => {
    const verdict = maskVerdict(mask, [0, 1.25, 0, -3])

    expect(verdict.failed).toBe(2)
    expect(verdict.firstFailIndex).toBe(1)
    expect(verdict.worstExcess).toBeCloseTo(2, 12)
  })

  it('treats the edge of the band as inside it', () => {
    expect(maskVerdict(mask, [1, -1, 1, -1]).failed).toBe(0)
  })

  /**
   * The rule a decimated trace rests on: a spike inside one column is exactly what a mask is
   * pointed at, and judging the maxima alone passes a trace that dips through the floor between
   * two pixels.
   */
  it('judges both outlines of a decimated trace, not just the drawn line', () => {
    const verdict = maskVerdict(mask, [0, 0, 0, 0], [0, -4, 0, 0])

    expect(verdict.failed).toBe(1)
    expect(verdict.firstFailIndex).toBe(1)
    expect(verdict.worstExcess).toBeCloseTo(3, 12)
  })

  it('checks neither a column with no band nor one with no sample', () => {
    const gappy = buildScopeMask([0, null, 0], 1, 1, 0)

    const verdict = maskVerdict(gappy, [5, 5, null])

    expect(verdict.checked).toBe(1)
    expect(verdict.failed).toBe(1)
  })

  it('answers nothing for a band that covers none of the window', () => {
    const elsewhere = buildScopeMask([null, null], 1, 1, 0)

    expect(maskVerdict(elsewhere, [9, 9]).checked).toBe(0)
    expect(maskVerdict(elsewhere, [9, 9]).failed).toBe(0)
  })
})

describe('the run tally', () => {
  it('counts a record once, whichever way it went', () => {
    const tally = newMaskTally()

    addMaskVerdict(tally, { checked: 100, failed: 0, firstFailIndex: null, worstExcess: 0 })
    addMaskVerdict(tally, { checked: 100, failed: 3, firstFailIndex: 7, worstExcess: 0.2 })

    expect(tally).toEqual({ passed: 1, failed: 1 })
  })

  /** Absent is not a pass, at the level of a whole record: a band over nothing would read as all clear. */
  it('counts a record nothing could be judged on as neither', () => {
    const tally = newMaskTally()

    addMaskVerdict(tally, { checked: 0, failed: 0, firstFailIndex: null, worstExcess: 0 })

    expect(tally).toEqual({ passed: 0, failed: 0 })
  })
})
