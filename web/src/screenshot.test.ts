import { describe, expect, it } from 'vitest'
import {
  SCREENSHOT_DATE_BAND_PX,
  SCREENSHOT_SIZES,
  SCREENSHOT_TITLE_BAND_PX,
  screenshotFrameLayout,
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
