import { describe, expect, it } from 'vitest'
import {
  MANUAL_EDITIONS,
  MANUAL_PAGES,
  manualCounterpart,
  manualPageById,
  manualPageForKind,
  manualPagesIn,
} from './pages'
import { FFT_SPECTRUM_KIND } from '../measurements/kinds'

// The manual is two editions over one page list, and nothing else in the app knows that — the dialog
// reads it from MANUAL_EDITIONS, the cross-edition banner from manualCounterpart. So the invariants
// that keep the two books separate, and the links between them alive, are only checked here.

describe('the two editions', () => {
  it('gives every page an edition its group belongs to', () => {
    for (const page of MANUAL_PAGES) {
      const edition = MANUAL_EDITIONS.find((e) => e.id === page.edition)
      expect(edition, page.id).toBeDefined()
      expect(edition!.groups, `${page.id} is filed under "${page.group}"`).toContain(page.group)
    }
  })

  it('leaves no group of either edition empty', () => {
    // An empty group is a heading with nothing under it in the sidebar and on the contents page.
    for (const edition of MANUAL_EDITIONS) {
      for (const group of edition.groups) {
        const pages = manualPagesIn(edition.id).filter((p) => p.group === group)
        expect(pages.length, `${edition.title} ▸ ${group}`).toBeGreaterThan(0)
      }
    }
  })

  it('has pages in both editions', () => {
    for (const edition of MANUAL_EDITIONS) {
      expect(manualPagesIn(edition.id).length, edition.title).toBeGreaterThan(0)
    }
  })

  it('gives each page a unique id', () => {
    const ids = MANUAL_PAGES.map((p) => p.id)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('writes every summary as a sentence, since the contents page prints it as one', () => {
    for (const page of MANUAL_PAGES) {
      expect(page.summary.length, page.id).toBeGreaterThan(20)
      expect(page.summary.trimEnd().endsWith('.'), page.id).toBe(true)
    }
  })
})

describe('the cross-edition links', () => {
  it('points every counterpart at a page in the other edition', () => {
    for (const page of MANUAL_PAGES) {
      if (!page.counterpart) continue
      const target = manualPageById(page.counterpart)
      expect(target, `${page.id} points at "${page.counterpart}"`).not.toBeNull()
      expect(target!.edition, `${page.id} → ${page.counterpart}`).not.toBe(page.edition)
    }
  })

  it('never lets two pages claim the same counterpart', () => {
    // The reverse direction is found by searching for whoever names this page, so a shared target
    // would give that page a banner pointing at only one of them — silently.
    const claimed = MANUAL_PAGES.map((p) => p.counterpart).filter((id): id is string => !!id)
    expect(new Set(claimed).size).toBe(claimed.length)
  })

  it('resolves a counterpart from either side', () => {
    const guide = manualPagesIn('guide').find((p) => p.counterpart)!
    const reference = manualCounterpart(guide)!
    expect(reference.edition).toBe('reference')
    expect(manualCounterpart(reference)?.id).toBe(guide.id)
  })

  it('gives most of the guide a way through to the full manual', () => {
    // Not all of it: a page like "Decibels and volts" has no chapter of its own to point at. But a
    // guide that mostly dead-ends has stopped being an entrance to the manual.
    const guide = manualPagesIn('guide')
    const linked = guide.filter((p) => manualCounterpart(p) !== null)
    expect(linked.length).toBeGreaterThan(guide.length / 2)
  })
})

describe("a measurement screen's own help button", () => {
  it('lands in the full manual, never in the guide', () => {
    for (const page of MANUAL_PAGES) {
      if (page.kind) expect(page.edition, page.id).toBe('reference')
    }
    expect(manualPageForKind(FFT_SPECTRUM_KIND)?.edition).toBe('reference')
  })
})
