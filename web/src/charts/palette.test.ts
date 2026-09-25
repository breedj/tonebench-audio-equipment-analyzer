import { describe, expect, it } from 'vitest'
import { EXPORT_GRID_WIDTH_SCALE, exportGridStroke, exportGridTheme, makeChartPalette } from './palette'

describe('the export grid', () => {
  it('darkens a grid grey and leaves the screen one alone', () => {
    const screen = makeChartPalette(false)
    const exported = makeChartPalette(false, true)
    expect(screen.grid).toBe('#e5e7eb')
    expect(screen.gridWidth).toBe(1)
    expect(exported.grid).not.toBe(screen.grid)
    expect(exported.gridWidth).toBe(EXPORT_GRID_WIDTH_SCALE)
  })

  it('darkens without changing anything else about the palette', () => {
    const screen = makeChartPalette(false)
    const exported = makeChartPalette(false, true)
    expect(exported.axis).toBe(screen.axis)
    expect(exported.labelBackground).toBe(screen.labelBackground)
    expect(exported.dark).toBe(false)
  })

  it('keeps several grid tiers in the same order', () => {
    // The sweep chart draws decade, major and minor greys; flattening them into one grey would
    // lose the decade lines, which is why the export multiplies rather than replaces.
    const tiers = ['#c8c8c8', '#e5e5e5', '#ededed'].map(exportGridStroke)
    const value = (hex: string) => Number.parseInt(hex.slice(1), 16)
    expect(value(tiers[0]!)).toBeLessThan(value(tiers[1]!))
    expect(value(tiers[1]!)).toBeLessThan(value(tiers[2]!))
  })

  it('lands a light grid where it still reads as a grid rather than as data', () => {
    // Dark enough to survive the viewer's downscale, light enough to stay behind the curves.
    const channel = Number.parseInt(exportGridStroke('#e5e5e5').slice(1, 3), 16)
    expect(channel).toBeGreaterThan(0x90)
    expect(channel).toBeLessThan(0xd0)
  })

  it('hands back anything that is not a plain hex colour', () => {
    expect(exportGridStroke('rgba(234, 179, 8, 0.45)')).toBe('rgba(234, 179, 8, 0.45)')
    expect(exportGridStroke('transparent')).toBe('transparent')
  })

  it('wraps a chart theme without disturbing its other colours', () => {
    const light = { axis: '#525252', grid: '#e5e5e5', gridWidth: 1, trace: '#1d6fd0' }
    const exported = exportGridTheme(light)
    expect(exported.axis).toBe(light.axis)
    expect(exported.trace).toBe(light.trace)
    expect(exported.grid).toBe(exportGridStroke(light.grid))
    expect(exported.gridWidth).toBe(EXPORT_GRID_WIDTH_SCALE)
  })
})
