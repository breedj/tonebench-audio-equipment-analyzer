import { describe, expect, it } from 'vitest'
import {
  ANNOTATION_COLORS,
  MAX_ANNOTATIONS,
  MAX_ANNOTATION_TEXT,
  addAnnotation,
  annotationColor,
  annotationReading,
  numberedAnnotations,
  parseAnnotations,
  removeAnnotation,
  updateAnnotation,
  type SpectrumAnnotation,
} from './spectrumAnnotations'

/** A draft, i.e. an annotation without the id the list hands out. */
function draft(freqHz: number, text = 'note'): Omit<SpectrumAnnotation, 'id'> {
  const { id: _id, ...rest } = point(1, freqHz, text)
  return rest
}

function point(id: number, freqHz: number, text = 'note'): SpectrumAnnotation {
  return {
    id,
    style: 'point',
    text,
    freqHz,
    levelDbv: -80,
    channel: 0,
    labelOffsetOctaves: 0.4,
    labelOffsetDb: 12,
  }
}

describe('parseAnnotations', () => {
  it('reads back a list it wrote', () => {
    const list = [point(1, 50), { ...point(2, 3000), style: 'frequency' as const, levelDbv: null, channel: null }]
    expect(parseAnnotations(JSON.parse(JSON.stringify(list)))).toEqual(list)
  })

  it('tolerates a stored value that is not a list at all', () => {
    for (const value of [null, undefined, 0, 'two', {}, { length: 2 }]) {
      expect(parseAnnotations(value)).toEqual([])
    }
  })

  it('drops an entry with no usable frequency', () => {
    // A log axis has no position for zero or a negative, and valToPos answers -Infinity for one.
    const parsed = parseAnnotations([
      { ...point(1, 0) },
      { ...point(2, -50) },
      { ...point(3, Number.NaN) },
      { ...point(4, 1000) },
    ])
    expect(parsed.map((a) => a.freqHz)).toEqual([1000])
  })

  it('reads an unknown style as a point when it has a level and as a frequency rule when it does not', () => {
    const parsed = parseAnnotations([
      { id: 1, style: 'banner', text: 'a', freqHz: 100, levelDbv: -60 },
      { id: 2, style: 'banner', text: 'b', freqHz: 200 },
    ])
    expect(parsed.map((a) => a.style)).toEqual(['point', 'frequency'])
    expect(parsed[1].levelDbv).toBeNull()
  })

  it('truncates a note longer than a chart label can carry', () => {
    const parsed = parseAnnotations([{ ...point(1, 100, 'x'.repeat(500)) }])
    expect(parsed[0].text).toHaveLength(MAX_ANNOTATION_TEXT)
  })

  it('keeps the first twenty-four annotations and drops the rest', () => {
    const many = Array.from({ length: MAX_ANNOTATIONS + 10 }, (_, i) => point(i + 1, 100 + i))
    expect(parseAnnotations(many)).toHaveLength(MAX_ANNOTATIONS)
  })

  it('gives two entries that stored the same id ids of their own', () => {
    const parsed = parseAnnotations([point(7, 100), point(7, 200), point(7, 300)])
    expect(new Set(parsed.map((a) => a.id)).size).toBe(3)
  })

  it('defaults a non-finite offset and an out-of-range channel rather than keeping them', () => {
    const parsed = parseAnnotations([
      { id: 1, style: 'point', text: 'a', freqHz: 100, levelDbv: -60, channel: 5, labelOffsetOctaves: 'x', labelOffsetDb: null },
    ])
    expect(parsed[0]).toMatchObject({ channel: null, labelOffsetOctaves: 0, labelOffsetDb: 0 })
  })
})

describe('numberedAnnotations', () => {
  it('numbers the annotations left to right, whatever order they were added in', () => {
    const numbered = numberedAnnotations([point(1, 19000), point(2, 50), point(3, 1000)])
    expect(numbered.map((a) => [a.number, a.freqHz])).toEqual([
      [1, 50],
      [2, 1000],
      [3, 19000],
    ])
  })

  it('renumbers without a gap after one is deleted', () => {
    const list = [point(1, 50), point(2, 1000), point(3, 19000)]
    const numbered = numberedAnnotations(removeAnnotation(list, 2))
    expect(numbered.map((a) => a.number)).toEqual([1, 2])
  })

  it('breaks a tie on the id, so two at one frequency keep a stable order', () => {
    expect(numberedAnnotations([point(9, 1000), point(4, 1000)]).map((a) => a.id)).toEqual([4, 9])
  })
})

describe('the mutators', () => {
  it('leaves the list they were given untouched', () => {
    // The dirty watcher in useChartDisplay fires on replacement, never on a push: an in-place edit
    // would leave the measurement looking saved while carrying an unsaved note.
    const list = [point(1, 50)]
    const frozen = JSON.parse(JSON.stringify(list))
    addAnnotation(list, draft(100))
    updateAnnotation(list, 1, { text: 'changed' })
    removeAnnotation(list, 1)
    expect(list).toEqual(frozen)
  })

  it('gives a new annotation an id nothing else has', () => {
    const list = addAnnotation([point(3, 50)], draft(100))
    expect(list[1].id).toBe(4)
  })

  it('adds nothing once the chart is full', () => {
    const full = Array.from({ length: MAX_ANNOTATIONS }, (_, i) => point(i + 1, 100 + i))
    expect(addAnnotation(full, draft(5000))).toHaveLength(MAX_ANNOTATIONS)
  })

  it('changes nothing for an id no entry has', () => {
    const list = [point(1, 50)]
    expect(updateAnnotation(list, 99, { text: 'nope' })).toEqual(list)
    expect(removeAnnotation(list, 99)).toEqual(list)
  })

  it('truncates a note typed past the label length', () => {
    expect(updateAnnotation([point(1, 50)], 1, { text: 'y'.repeat(200) })[0].text).toHaveLength(MAX_ANNOTATION_TEXT)
  })
})

describe('annotationReading', () => {
  it('reads a point as its frequency and its stored level in dBV', () => {
    expect(annotationReading(point(1, 1000))).toBe('1.000 kHz · -80.0 dBV')
  })

  it('leaves the level out of a frequency rule, which has none', () => {
    expect(annotationReading({ ...point(1, 1000), style: 'frequency', levelDbv: null })).toBe('1.000 kHz')
  })
})

describe('annotationColor', () => {
  it('gives the first six annotations a colour each', () => {
    const used = [1, 2, 3, 4, 5, 6].map(annotationColor)
    expect(new Set(used).size).toBe(6)
  })

  it('cycles past the end of the palette rather than running out', () => {
    expect(annotationColor(ANNOTATION_COLORS.length + 1)).toBe(annotationColor(1))
  })

  it('follows the ordinal, so a colour moves with the number a reader sees', () => {
    // Delete the first of three and the rest renumber (numberedAnnotations); their badges take the
    // colours of their new numbers rather than leaving a gap in the sequence.
    expect(annotationColor(2)).not.toBe(annotationColor(1))
    expect(annotationColor(1)).toBe(ANNOTATION_COLORS[0])
  })

  it('answers something drawable for a number nothing should ever pass', () => {
    for (const ordinal of [0, -4, Number.NaN]) {
      expect(ANNOTATION_COLORS).toContain(annotationColor(ordinal))
    }
  })
})
