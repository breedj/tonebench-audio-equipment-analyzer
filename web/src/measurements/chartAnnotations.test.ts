import { describe, expect, it } from 'vitest'
import {
  ANNOTATION_COLORS,
  DEFAULT_ANNOTATION_STYLES,
  LEGACY_SPECTRUM_FRAME,
  MAX_ANNOTATIONS,
  MAX_ANNOTATION_TEXT,
  addAnnotation,
  annotationColor,
  annotationReading,
  annotationStyleOptions,
  annotationViewsPresent,
  annotationsElsewhere,
  normalizeAnnotationBox,
  numberedAnnotations,
  parseAnnotations,
  readStoredAnnotations,
  removeAnnotation,
  removeAnnotationsOfView,
  updateAnnotation,
  type AnnotationFrame,
  type ChartAnnotation,
} from './chartAnnotations'

const FRAME = LEGACY_SPECTRUM_FRAME
const SPECTRUM = FRAME.view

/** A draft, i.e. an annotation without the id the list hands out. */
function draft(x: number, text = 'note'): Omit<ChartAnnotation, 'id'> {
  const { id: _id, ...rest } = point(1, x, text)
  return rest
}

function point(id: number, x: number, text = 'note'): ChartAnnotation {
  return {
    id,
    style: 'point',
    text,
    view: SPECTRUM,
    x,
    y: -80,
    x2: null,
    y2: null,
    xQuantity: 'hz',
    yQuantity: 'dbv',
    channel: 0,
    labelOffsetX: 0.4,
    labelOffsetY: 12,
  }
}

/** A region from 1 to 3 kHz between -20 and -60 dBV, stored top-left first. */
function box(id: number, lo = 1000, hi = 3000, text = 'region'): ChartAnnotation {
  return {
    id,
    style: 'box',
    text,
    view: SPECTRUM,
    x: lo,
    y: -20,
    x2: hi,
    y2: -60,
    xQuantity: 'hz',
    yQuantity: 'dbv',
    channel: null,
    labelOffsetX: 0.3,
    labelOffsetY: 10,
  }
}

describe('parseAnnotations', () => {
  it('reads back a list it wrote', () => {
    const list = [point(1, 50), { ...point(2, 3000), style: 'frequency' as const, y: null, channel: null }]
    expect(parseAnnotations(JSON.parse(JSON.stringify(list)), FRAME)).toEqual(list)
  })

  it('tolerates a stored value that is not a list at all', () => {
    for (const value of [null, undefined, 0, 'two', {}, { length: 2 }]) {
      expect(parseAnnotations(value, FRAME)).toEqual([])
    }
  })

  it('reads a row written before the fields were renamed, in the frame it was given', () => {
    // Everything stored while annotations were an FFT-only feature used these names, and on a log
    // frequency axis against a dB axis the old semantics *are* the new ones: x logarithmic, so the
    // offset was in octaves; y linear, so it was a difference.
    const legacy = {
      id: 3,
      style: 'point',
      text: 'Mains hum',
      freqHz: 50,
      levelDbv: -72.5,
      channel: 1,
      labelOffsetOctaves: 0.45,
      labelOffsetDb: 14,
    }
    expect(parseAnnotations([legacy], FRAME)).toEqual([
      {
        id: 3,
        style: 'point',
        text: 'Mains hum',
        view: 'Spectrum',
        x: 50,
        y: -72.5,
        x2: null,
        y2: null,
        xQuantity: 'hz',
        yQuantity: 'dbv',
        channel: 1,
        labelOffsetX: 0.45,
        labelOffsetY: 14,
      },
    ])
  })

  it('reads a legacy box back with both of the corners it wrote', () => {
    const legacy = {
      id: 1,
      style: 'box',
      text: 'Hum band',
      freqHz: 900,
      levelDbv: -40,
      freqHz2: 1200,
      levelDbv2: -95,
      labelOffsetOctaves: 0.3,
      labelOffsetDb: 10,
    }
    expect(parseAnnotations([legacy], FRAME)[0]).toMatchObject({
      style: 'box',
      x: 900,
      x2: 1200,
      y: -40,
      y2: -95,
    })
  })

  it('prefers the new names over the old ones when a row carries both', () => {
    // A document written by a newer build and then edited by an older one still reads as the newer
    // one meant it.
    const row = { id: 1, style: 'point', text: 'a', x: 2000, freqHz: 50, y: -10, levelDbv: -80 }
    expect(parseAnnotations([row], FRAME)[0]).toMatchObject({ x: 2000, y: -10 })
  })

  it('fills the view and the quantities a row does not name from the frame', () => {
    const frame: AnnotationFrame = { view: 'Sweep', xQuantity: 'outputWatt', yQuantity: 'pct' }
    expect(parseAnnotations([{ id: 1, style: 'point', text: 'a', x: 2.4, y: 0.01 }], frame)[0]).toMatchObject({
      view: 'Sweep',
      xQuantity: 'outputWatt',
      yQuantity: 'pct',
    })
  })

  it('keeps the view and the quantities a row does name', () => {
    const row = { id: 1, style: 'point', text: 'a', x: 1, y: 2, view: 'Nyquist', xQuantity: 're', yQuantity: 'im' }
    expect(parseAnnotations([row], FRAME)[0]).toMatchObject({ view: 'Nyquist', xQuantity: 're', yQuantity: 'im' })
  })

  it('keeps an annotation whose x is zero or negative, which a time axis has a place for', () => {
    // The impulse response's window crosses t = 0, the s-plane's sigma is negative over most of its
    // area, and a Nyquist real part is routinely negative. Whether a value has a *position* is the
    // axis's judgement, not the parser's.
    const parsed = parseAnnotations(
      [
        { id: 1, style: 'point', text: 'a', x: 0, y: 1 },
        { id: 2, style: 'point', text: 'b', x: -0.5, y: 1 },
      ],
      { view: 'Impulse', xQuantity: 'ms', yQuantity: 'volt' },
    )
    expect(parsed.map((a) => a.x)).toEqual([0, -0.5])
  })

  it('drops an entry with no usable x at all', () => {
    const parsed = parseAnnotations(
      [{ ...point(1, Number.NaN) }, { id: 2, style: 'point', text: 'b' }, { ...point(3, 1000) }],
      FRAME,
    )
    expect(parsed.map((a) => a.x)).toEqual([1000])
  })

  it('reads an unknown style as a point when it has a level and as a rule when it does not', () => {
    const parsed = parseAnnotations(
      [
        { id: 1, style: 'banner', text: 'a', x: 100, y: -60 },
        { id: 2, style: 'banner', text: 'b', x: 200 },
      ],
      FRAME,
    )
    expect(parsed.map((a) => a.style)).toEqual(['point', 'frequency'])
    expect(parsed[1].y).toBeNull()
  })

  it('truncates a note longer than a chart label can carry', () => {
    const parsed = parseAnnotations([{ ...point(1, 100, 'x'.repeat(500)) }], FRAME)
    expect(parsed[0].text).toHaveLength(MAX_ANNOTATION_TEXT)
  })

  it('keeps the first twenty-four annotations and drops the rest', () => {
    const many = Array.from({ length: MAX_ANNOTATIONS + 10 }, (_, i) => point(i + 1, 100 + i))
    expect(parseAnnotations(many, FRAME)).toHaveLength(MAX_ANNOTATIONS)
  })

  it('gives two entries that stored the same id ids of their own', () => {
    const parsed = parseAnnotations([point(7, 100), point(7, 200), point(7, 300)], FRAME)
    expect(new Set(parsed.map((a) => a.id)).size).toBe(3)
  })

  it('defaults a non-finite offset and an out-of-range channel rather than keeping them', () => {
    const parsed = parseAnnotations(
      [{ id: 1, style: 'point', text: 'a', x: 100, y: -60, channel: 5, labelOffsetX: 'x', labelOffsetY: null }],
      FRAME,
    )
    expect(parsed[0]).toMatchObject({ channel: null, labelOffsetX: 0, labelOffsetY: 0 })
  })
})

describe('readStoredAnnotations', () => {
  it('reads a value that is not an array as "none sent" rather than as an empty list', () => {
    // The distinction the report's per-result rule needs: a result saved before it could hold
    // annotations falls back to its measurement's, while one somebody cleared keeps its empty list.
    expect(readStoredAnnotations(undefined, FRAME)).toBeNull()
    expect(readStoredAnnotations(null, FRAME)).toBeNull()
    expect(readStoredAnnotations([], FRAME)).toEqual([])
  })
})

describe('numberedAnnotations', () => {
  it('numbers the annotations left to right, whatever order they were added in', () => {
    const numbered = numberedAnnotations([point(1, 19000), point(2, 50), point(3, 1000)], SPECTRUM)
    expect(numbered.map((a) => [a.number, a.x])).toEqual([
      [1, 50],
      [2, 1000],
      [3, 19000],
    ])
  })

  it('numbers each view from one, so two charts of one measurement both start at 1', () => {
    const list = [
      { ...point(1, 1000), view: 'Bode' },
      { ...point(2, -1.2), view: 'Nyquist', xQuantity: 're', yQuantity: 'im' },
      { ...point(3, 20), view: 'Bode' },
      { ...point(4, 0.5), view: 'Nyquist', xQuantity: 're', yQuantity: 'im' },
    ]
    expect(numberedAnnotations(list, 'Bode').map((a) => [a.number, a.id])).toEqual([
      [1, 3],
      [2, 1],
    ])
    expect(numberedAnnotations(list, 'Nyquist').map((a) => [a.number, a.id])).toEqual([
      [1, 2],
      [2, 4],
    ])
  })

  it('orders a view left to right whatever quantity x is', () => {
    const watts = [
      { ...point(1, 12.5), view: 'Sweep', xQuantity: 'outputWatt', yQuantity: 'pct' },
      { ...point(2, 0.05), view: 'Sweep', xQuantity: 'outputWatt', yQuantity: 'pct' },
    ]
    expect(numberedAnnotations(watts, 'Sweep').map((a) => a.id)).toEqual([2, 1])
  })

  it('leaves the list it was given untouched, although it sorts', () => {
    const list = [point(1, 19000), point(2, 50)]
    numberedAnnotations(list, SPECTRUM)
    expect(list.map((a) => a.id)).toEqual([1, 2])
  })

  it('renumbers without a gap after one is deleted', () => {
    const list = [point(1, 50), point(2, 1000), point(3, 19000)]
    const numbered = numberedAnnotations(removeAnnotation(list, 2), SPECTRUM)
    expect(numbered.map((a) => a.number)).toEqual([1, 2])
  })

  it('breaks a tie on the id, so two at one x keep a stable order', () => {
    expect(numberedAnnotations([point(9, 1000), point(4, 1000)], SPECTRUM).map((a) => a.id)).toEqual([4, 9])
  })
})

describe('the view helpers', () => {
  const mixed = [
    { ...point(1, 1000), view: 'Bode' },
    { ...point(2, 2000), view: 'Nyquist' },
    { ...point(3, 3000), view: 'Bode' },
  ]

  it('lists the views that have annotations, in the order they first appear', () => {
    expect(annotationViewsPresent(mixed)).toEqual(['Bode', 'Nyquist'])
  })

  it('counts how many belong to some other chart, which is what the panel says', () => {
    expect(annotationsElsewhere(mixed, 'Bode')).toBe(1)
    expect(annotationsElsewhere(mixed, 'Harmonics')).toBe(3)
  })

  it('removes one view’s annotations and leaves every other view alone', () => {
    expect(removeAnnotationsOfView(mixed, 'Bode').map((a) => a.id)).toEqual([2])
  })
})

describe('the mutators', () => {
  it('leaves the list they were given untouched', () => {
    // The dirty watcher fires on replacement, never on a push: an in-place edit would leave the
    // measurement looking saved while carrying an unsaved note.
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

  it('adds nothing once the measurement is full', () => {
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
  it('reads a point as its x and its stored y, in the quantities they were stored in', () => {
    expect(annotationReading(point(1, 1000))).toBe('1.000 kHz · -80.0 dBV')
  })

  it('reads a point stored in some other quantity in that one', () => {
    const watts = { ...point(1, 2.4), xQuantity: 'outputWatt', yQuantity: 'pct', y: 0.0123 }
    expect(annotationReading(watts)).toBe('2.40 W · 0.0123 %')
  })

  it('leaves the level out of a rule, which has none', () => {
    expect(annotationReading({ ...point(1, 1000), style: 'frequency', y: null })).toBe('1.000 kHz')
  })

  it('reads a box as its two ranges', () => {
    expect(annotationReading(box(1))).toBe('1.00 – 3.00 kHz · -60.0 – -20.0 dBV')
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
    expect(annotationColor(2)).not.toBe(annotationColor(1))
    expect(annotationColor(1)).toBe(ANNOTATION_COLORS[0])
  })

  it('answers something drawable for a number nothing should ever pass', () => {
    for (const ordinal of [0, -4, Number.NaN]) {
      expect(ANNOTATION_COLORS).toContain(annotationColor(ordinal))
    }
  })
})

describe('a box annotation', () => {
  it('reads a box back with both of the corners it wrote', () => {
    // The trap: written `style === 'point' ? y : null`, every box loses its top edge here.
    const parsed = parseAnnotations(JSON.parse(JSON.stringify([box(1)])), FRAME)
    expect(parsed).toEqual([box(1)])
    expect(parsed[0].y).toBe(-20)
    expect(parsed[0].y2).toBe(-60)
  })

  it('normalises a box stored with its corners the wrong way round', () => {
    const inverted = { ...box(1), x: 3000, x2: 1000, y: -60, y2: -20 }
    expect(parseAnnotations([inverted], FRAME)[0]).toMatchObject({ x: 1000, x2: 3000, y: -20, y2: -60 })
  })

  it('reads a box whose second corner is missing as the point it still describes, keeping the note', () => {
    const parsed = parseAnnotations([{ ...box(1, 1000, 3000, 'kept'), x2: null, y2: null }], FRAME)
    expect(parsed[0]).toMatchObject({ style: 'point', x: 1000, y: -20, text: 'kept' })
  })

  it('reads a box that has lost its levels as a rule rather than dropping it', () => {
    const parsed = parseAnnotations([{ id: 1, style: 'box', text: 'kept', x: 1000, x2: 3000 }], FRAME)
    expect(parsed[0]).toMatchObject({ style: 'frequency', x: 1000, text: 'kept' })
  })

  it('reads an unknown style carrying two corners as a box', () => {
    const parsed = parseAnnotations([{ id: 1, style: 'region', text: 'a', x: 1000, y: -20, x2: 3000, y2: -60 }], FRAME)
    expect(parsed[0].style).toBe('box')
  })

  it('keeps a stored rule exactly as it always read it, although most pickers no longer offer one', () => {
    // Dropping a style from a picker must not touch what is already stored: an annotation the app
    // quietly stopped drawing is the user's own words lost.
    const rule: ChartAnnotation = {
      id: 4,
      style: 'frequency',
      text: 'Bandwidth corner',
      view: SPECTRUM,
      x: 21500,
      y: null,
      x2: null,
      y2: null,
      xQuantity: 'hz',
      yQuantity: 'dbv',
      channel: null,
      labelOffsetX: 0,
      labelOffsetY: 0,
    }
    expect(parseAnnotations([rule], FRAME)).toEqual([rule])
  })

  it('nulls a box channel, because a region names no trace', () => {
    expect(parseAnnotations([{ ...box(1), channel: 1 }], FRAME)[0].channel).toBeNull()
  })

  it('numbers a box by its low edge, which is where a reader enters it', () => {
    const numbered = numberedAnnotations([box(1, 5000, 9000), point(2, 2000)], SPECTRUM)
    expect(numbered.map((a) => [a.number, a.id])).toEqual([
      [1, 2],
      [2, 1],
    ])
  })

  it('normalises a box whose corners a patch inverted', () => {
    const patched = updateAnnotation([box(1)], 1, { x2: 400 })
    expect(patched[0]).toMatchObject({ x: 400, x2: 1000 })
  })

  it('leaves every other style alone', () => {
    const list = [point(1, 50)]
    expect(normalizeAnnotationBox(list[0])).toBe(list[0])
  })
})

describe('annotationStyleOptions', () => {
  it('offers Point and Box by default, and not the rule, which only stored annotations still use', () => {
    expect(annotationStyleOptions(DEFAULT_ANNOTATION_STYLES).map((o) => o.value)).toEqual(['point', 'box'])
  })

  it('offers the rule to a chart that asks for it, named Rule rather than Frequency', () => {
    // The spectrogram's y axis is a rolling age and the multi-scale charts have three y scales, so a
    // rule at one x value is the only honest style there.
    const options = annotationStyleOptions(['frequency'])
    expect(options.map((o) => o.value)).toEqual(['frequency'])
    expect(options[0].label).toBe('Rule')
  })

  it('keeps the picker in one order whatever order a chart lists its styles in', () => {
    expect(annotationStyleOptions(['frequency', 'box', 'point']).map((o) => o.value)).toEqual([
      'point',
      'box',
      'frequency',
    ])
  })
})
