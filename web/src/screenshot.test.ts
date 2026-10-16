import { describe, expect, it } from 'vitest'
import {
  SCREENSHOT_DATE_BAND_PX,
  SCREENSHOT_SIZES,
  SCREENSHOT_TITLE_BAND_PX,
  screenshotFrameLayout,
  screenshotTitleFor,
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
  const kept = { title: 'Elliptic - Nyquist', titleFor: 'Bode sweep', saveText: true }

  it('opens on the measurement name when nothing is kept', () => {
    expect(screenshotTitleFor({ title: '', titleFor: '', saveText: false }, 'FFT analysis')).toBe('FFT analysis')
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
    expect(screenshotTitleFor({ title: '   ', titleFor: 'Bode sweep', saveText: true }, 'Bode sweep')).toBe('Bode sweep')
  })

  it('compares names trimmed, since that is how both are stored', () => {
    expect(screenshotTitleFor(kept, '  Bode sweep  ')).toBe('Elliptic - Nyquist')
  })

  // Nothing open: there is no name to fall back to, and an empty box means no title band.
  it('is empty with no measurement open', () => {
    expect(screenshotTitleFor(kept, '')).toBe('')
  })
})
