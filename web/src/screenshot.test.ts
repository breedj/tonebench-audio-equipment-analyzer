import { describe, expect, it } from 'vitest'
import {
  MAX_SCREENSHOT_CAPTIONS,
  SCREENSHOT_DATE_BAND_PX,
  SCREENSHOT_SIZES,
  SCREENSHOT_TITLE_BAND_PX,
  rememberScreenshotCaption,
  screenshotFrameLayout,
  screenshotTitleFor,
  toScreenshotCaptions,
  toScreenshotAspectRatioId,
  toScreenshotDestinationId,
  toScreenshotWidthPx,
} from './screenshot'

describe('screenshotFrameLayout', () => {
  it('sizes the whole picture from the width and the ratio', () => {
    expect(screenshotFrameLayout(1500, '3:2', '', false)).toMatchObject({ width: 1500, height: 1000, chartWidth: 1500, chartHeight: 1000 })
    expect(screenshotFrameLayout(1000, '2:1', '', false)).toMatchObject({ width: 1000, height: 500, chartHeight: 500 })
    // 750 / 1.5 = 500 exactly; 1250 / 1.5 rounds.
    expect(screenshotFrameLayout(1250, '3:2', '', false).height).toBe(833)
  })

  it('takes the bands out of the chart, not out of the picture', () => {
    const framed = screenshotFrameLayout(1500, '3:2', 'Output stage', true)
    expect(framed.width).toBe(1500)
    expect(framed.height).toBe(1000)
    expect(framed.titleBand).toBe(SCREENSHOT_TITLE_BAND_PX)
    expect(framed.dateBand).toBe(SCREENSHOT_DATE_BAND_PX)
    expect(framed.chartHeight).toBe(1000 - SCREENSHOT_TITLE_BAND_PX - SCREENSHOT_DATE_BAND_PX)
  })

  it('gives a blank title no band', () => {
    expect(screenshotFrameLayout(1500, '3:2', '   ', false).titleBand).toBe(0)
  })

  it('offers the sizes the dialog promises, 500 to 2000 px', () => {
    expect(SCREENSHOT_SIZES[0].widthPx).toBe(500)
    expect(SCREENSHOT_SIZES[SCREENSHOT_SIZES.length - 1].widthPx).toBe(2000)
  })
})

describe('stored answers this build may not know', () => {
  it('snaps a width to the nearest offered size', () => {
    expect(toScreenshotWidthPx(1600)).toBe(1500)
    expect(toScreenshotWidthPx(1)).toBe(500)
    expect(toScreenshotWidthPx(99999)).toBe(2000)
    expect(toScreenshotWidthPx('wide')).toBe(1500)
  })

  it('falls back to the defaults for an unknown ratio or destination', () => {
    expect(toScreenshotAspectRatioId('2:1')).toBe('2:1')
    expect(toScreenshotAspectRatioId('16:9')).toBe('3:2')
    expect(toScreenshotDestinationId('both')).toBe('both')
    expect(toScreenshotDestinationId('printer')).toBe('file')
  })
})

describe('screenshotTitleFor', () => {
  const kept = { captions: [{ measurement: 'Bode sweep', title: 'Elliptic - Nyquist' }], saveText: true }

  it('opens on the measurement name when nothing is kept', () => {
    expect(screenshotTitleFor({ captions: [], saveText: false }, 'FFT analysis')).toBe('FFT analysis')
  })

  // The report, twice over: a bench with Save text ticked saw every screenshot of every
  // measurement open holding one caption typed for another, session after session.
  it('does not offer a kept title on a different measurement', () => {
    expect(screenshotTitleFor(kept, 'FFT analysis')).toBe('FFT analysis')
  })

  it('offers it again on the measurement it was typed for', () => {
    expect(screenshotTitleFor(kept, 'Bode sweep')).toBe('Elliptic - Nyquist')
  })

  it('ignores a kept title once Save text is off, even on its own measurement', () => {
    expect(screenshotTitleFor({ ...kept, saveText: false }, 'Bode sweep')).toBe('Bode sweep')
  })

  it('falls through a blank kept title rather than opening empty', () => {
    expect(
      screenshotTitleFor({ captions: [{ measurement: 'Bode sweep', title: '   ' }], saveText: true }, 'Bode sweep'),
    ).toBe('Bode sweep')
  })

  // The point of keeping one per measurement: each is offered on its own, and taking a screenshot
  // of one does not cost another its caption.
  it('keeps a caption for every measurement at once', () => {
    const many = {
      saveText: true,
      captions: [
        { measurement: 'Bode sweep', title: 'Elliptic - Nyquist' },
        { measurement: 'FFT analysis', title: 'Hum at 100 Hz' },
        { measurement: 'Amp 1 kHz', title: 'Before the recap' },
      ],
    }
    expect(screenshotTitleFor(many, 'Bode sweep')).toBe('Elliptic - Nyquist')
    expect(screenshotTitleFor(many, 'FFT analysis')).toBe('Hum at 100 Hz')
    expect(screenshotTitleFor(many, 'Amp 1 kHz')).toBe('Before the recap')
    expect(screenshotTitleFor(many, 'Crosstalk')).toBe('Crosstalk')
  })

  it('compares names trimmed, since that is how both are stored', () => {
    expect(screenshotTitleFor(kept, '  Bode sweep  ')).toBe('Elliptic - Nyquist')
  })

  // Nothing open: there is no name to fall back to, and an empty box means no title band.
  it('is empty with no measurement open', () => {
    expect(screenshotTitleFor(kept, '')).toBe('')
  })
})

describe('rememberScreenshotCaption', () => {
  const existing = [
    { measurement: 'Bode sweep', title: 'Elliptic - Nyquist' },
    { measurement: 'FFT analysis', title: 'Hum at 100 Hz' },
  ]

  it('files a new caption under its measurement, most recent first', () => {
    const next = rememberScreenshotCaption(existing, 'Amp 1 kHz', 'Before the recap', true)
    expect(next[0]).toEqual({ measurement: 'Amp 1 kHz', title: 'Before the recap' })
    expect(next).toHaveLength(3)
  })

  // The fault this replaced: one stored pair, so a screenshot of a second measurement silently
  // forgot the first measurement\'s caption.
  it('leaves another measurement\'s caption alone', () => {
    const next = rememberScreenshotCaption(existing, 'Amp 1 kHz', 'Before the recap', true)
    expect(screenshotTitleFor({ captions: next, saveText: true }, 'Bode sweep')).toBe('Elliptic - Nyquist')
    expect(screenshotTitleFor({ captions: next, saveText: true }, 'FFT analysis')).toBe('Hum at 100 Hz')
  })

  it('replaces a measurement\'s own caption rather than adding a second', () => {
    const next = rememberScreenshotCaption(existing, 'Bode sweep', 'After the recap', true)
    expect(next.filter((c) => c.measurement === 'Bode sweep')).toHaveLength(1)
    expect(next[0]).toEqual({ measurement: 'Bode sweep', title: 'After the recap' })
    expect(next).toHaveLength(2)
  })

  it('removes this measurement\'s caption when Save text is off, and leaves the others', () => {
    const next = rememberScreenshotCaption(existing, 'Bode sweep', 'Elliptic - Nyquist', false)
    expect(next.map((c) => c.measurement)).toEqual(['FFT analysis'])
  })

  it('files nothing it could not look up again', () => {
    expect(rememberScreenshotCaption([], '', 'a caption', true)).toEqual([])
    expect(rememberScreenshotCaption([], 'Amp 1 kHz', '   ', true)).toEqual([])
  })

  it('caps the list, dropping the least recently used', () => {
    let captions: { measurement: string; title: string }[] = []
    for (let i = 0; i < MAX_SCREENSHOT_CAPTIONS + 10; i++) {
      captions = rememberScreenshotCaption(captions, `Measurement ${i}`, `caption ${i}`, true)
    }
    expect(captions).toHaveLength(MAX_SCREENSHOT_CAPTIONS)
    expect(captions[0].measurement).toBe(`Measurement ${MAX_SCREENSHOT_CAPTIONS + 9}`)
    expect(captions.some((c) => c.measurement === 'Measurement 0')).toBe(false)
  })
})

describe('toScreenshotCaptions', () => {
  it('reads a stored list', () => {
    expect(toScreenshotCaptions([{ measurement: 'Amp', title: 'note' }])).toEqual([
      { measurement: 'Amp', title: 'note' },
    ])
  })

  it('is empty for anything that is not a list of usable pairs', () => {
    expect(toScreenshotCaptions(undefined)).toEqual([])
    expect(toScreenshotCaptions(null)).toEqual([])
    expect(toScreenshotCaptions('nonsense')).toEqual([])
    expect(toScreenshotCaptions([{ measurement: 'Amp' }, { title: 'note' }, 42, null])).toEqual([])
    expect(toScreenshotCaptions([{ measurement: '  ', title: 'note' }])).toEqual([])
  })

  it('caps what it reads, so a hand-edited document cannot grow unbounded', () => {
    const many = Array.from({ length: MAX_SCREENSHOT_CAPTIONS + 5 }, (_, i) => ({
      measurement: `m${i}`,
      title: `t${i}`,
    }))
    expect(toScreenshotCaptions(many)).toHaveLength(MAX_SCREENSHOT_CAPTIONS)
  })
})
