import { describe, expect, it } from 'vitest'
import { layoutLegend } from './exportChart'

describe('layoutLegend', () => {
  const entries = [
    { label: 'Gain', color: '#000' },
    { label: 'Phase', color: '#7c3aed' },
    { label: 'A stored measurement with a long name', color: '#111' },
  ]

  it('right-aligns the entries in their original order', () => {
    const placements = layoutLegend(entries, [30, 40, 220], 800, 12)
    expect(placements.map((p) => p.entry.label)).toEqual(entries.map((e) => e.label))
    // Last entry ends at the margin: x + swatch(22) + gap(6) + label = 800 - 12.
    expect(placements[2].x + 22 + 6 + 220).toBe(788)
    for (let i = 1; i < placements.length; i++) expect(placements[i].x).toBeGreaterThan(placements[i - 1].x)
  })

  it('drops entries from the end when the width runs out, never the first', () => {
    const placements = layoutLegend(entries, [30, 40, 220], 200, 12)
    expect(placements.map((p) => p.entry.label)).toEqual(['Gain', 'Phase'])
    expect(layoutLegend(entries, [30, 40, 220], 90, 12).map((p) => p.entry.label)).toEqual(['Gain'])
  })

  it('handles an empty legend', () => {
    expect(layoutLegend([], [], 800, 12)).toEqual([])
  })
})
