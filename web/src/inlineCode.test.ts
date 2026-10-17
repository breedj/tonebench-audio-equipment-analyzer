import { describe, expect, it } from 'vitest'

import { inlineCodeSegments } from './inlineCode'

/** The text of a segment list, which must always be the input with the delimiters taken out. */
function rendered(text: string): string {
  return inlineCodeSegments(text)
    .map((s) => s.text)
    .join('')
}

describe('inlineCodeSegments', () => {
  it('leaves prose with no backticks in it as one segment', () => {
    expect(inlineCodeSegments('A sweep needs at least two points.')).toEqual([
      { text: 'A sweep needs at least two points.', code: false },
    ])
  })

  it('is empty for an empty message', () => {
    expect(inlineCodeSegments('')).toEqual([])
  })

  it('reads a pair as a code span and drops the backticks', () => {
    // The warning that found this, shortened.
    expect(inlineCodeSegments('This is a stepped (`.step`) run: 3 sweeps.')).toEqual([
      { text: 'This is a stepped (', code: false },
      { text: '.step', code: true },
      { text: ') run: 3 sweeps.', code: false },
    ])
  })

  it('keeps the spaces either side of a span, which carry the sentence', () => {
    const segments = inlineCodeSegments('use `.ac dec 100` rather than `.ac lin`.')
    expect(segments.map((s) => s.code)).toEqual([false, true, false, true, false])
    expect(segments[0]).toEqual({ text: 'use ', code: false })
    expect(segments[2]).toEqual({ text: ' rather than ', code: false })
    expect(rendered('use `.ac dec 100` rather than `.ac lin`.')).toBe(
      'use .ac dec 100 rather than .ac lin.',
    )
  })

  it('reads a span holding spaces and angle brackets, which the advice is written with', () => {
    expect(inlineCodeSegments('`.ac dec 100 <start> <stop>`')).toEqual([
      { text: '.ac dec 100 <start> <stop>', code: true },
    ])
  })

  it('starts and ends on a span without inventing empty segments', () => {
    expect(inlineCodeSegments('`a` and `b`')).toEqual([
      { text: 'a', code: true },
      { text: ' and ', code: false },
      { text: 'b', code: true },
    ])
    expect(inlineCodeSegments('`a``b`')).toEqual([
      { text: 'a', code: true },
      { text: 'b', code: true },
    ])
  })

  it('keeps an unpaired backtick as text rather than swallowing the rest of the message', () => {
    // A warning's whole value is the part after the thing that went wrong, so a stray tick must
    // cost one character and not a sentence.
    expect(inlineCodeSegments('add `.options plotwinsize=0 and run it again.')).toEqual([
      { text: 'add `.options plotwinsize=0 and run it again.', code: false },
    ])
    expect(inlineCodeSegments('a `b` c `d')).toEqual([
      { text: 'a ', code: false },
      { text: 'b', code: true },
      { text: ' c `d', code: false },
    ])
  })

  it('leaves an empty pair alone', () => {
    expect(inlineCodeSegments('nothing `` here')).toEqual([
      { text: 'nothing `` here', code: false },
    ])
  })

  it('never loses or adds a character but the delimiters it removes', () => {
    for (const text of [
      'plain',
      '`code`',
      'a `b` c',
      '`a` `b` `c`',
      'unpaired ` tick',
      '``',
      '`',
      'ends with a span `x`',
    ]) {
      const ticks = [...text].filter((c) => c === '`').length
      const paired = text.length - rendered(text).length
      expect(paired % 2).toBe(0)
      expect(paired).toBeLessThanOrEqual(ticks)
      expect(inlineCodeSegments(text).every((s) => s.text.length > 0)).toBe(true)
    }
  })
})
