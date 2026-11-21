import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  groupIntoSections,
  sectionBadgeStyle,
  sectionMarkColor,
  sectionWellStyle,
  type PickerSection,
} from './pickerSections'
import { SCOPE_MEASUREMENT_SECTIONS } from '../scope/measurementCatalogue'
import { SCOPE_MATH_SECTIONS } from '../scope/math'
import { STIMULUS_SECTIONS } from '../generator/stimulusKinds'

const SECTION: PickerSection = { id: 'a', label: 'A', note: '', tone: 'blue', mark: 'orange' }
const OTHER: PickerSection = { id: 'b', label: 'B', note: '', tone: 'teal', mark: 'pink' }

describe('grouping into sections', () => {
  it('keeps every item, exactly once, in the order it was given', () => {
    const items = [
      { id: 1, s: 'b' },
      { id: 2, s: 'a' },
      { id: 3, s: 'b' },
    ]
    const grouped = groupIntoSections([SECTION, OTHER], items, (i) => i.s)
    expect(grouped.map((g) => g.section.id)).toEqual(['a', 'b'])
    expect(grouped.flatMap((g) => g.items.map((i) => i.id)).sort()).toEqual([1, 2, 3])
    // Within a section the catalogue's own order survives, which is what keeps a tab reading the
    // way its table is written.
    expect(grouped[1].items.map((i) => i.id)).toEqual([1, 3])
  })

  it('drops a section nothing is in, rather than drawing an empty heading', () => {
    const grouped = groupIntoSections([SECTION, OTHER], [{ s: 'a' }], (i) => i.s)
    expect(grouped.map((g) => g.section.id)).toEqual(['a'])
  })

  it('drops an item whose section nothing defines — which is what the catalogues test against', () => {
    // Not a feature: the point of asserting it is that the loss is silent, so every catalogue
    // carries its own test that nothing is lost this way.
    expect(groupIntoSections([SECTION], [{ s: 'nonesuch' }], (i) => i.s)).toEqual([])
  })
})

describe('a section’s colours', () => {
  it('names a hue rather than spelling one, in both halves of a well', () => {
    expect(sectionWellStyle('violet')).toEqual({
      backgroundColor: 'var(--kind-violet-chip-bg)',
      color: 'var(--kind-violet-chip-fg)',
    })
    expect(sectionMarkColor('amber')).toBe('var(--kind-amber-chip-fg)')
    expect(sectionBadgeStyle('green')).toEqual({
      color: 'var(--kind-green-badge-fg)',
      backgroundColor: 'var(--kind-green-badge-bg)',
      borderColor: 'var(--kind-green-badge-border)',
    })
  })

  /**
   * The audit that matters, and the only one that can catch this: a tone that style.css does not
   * define renders as <em>nothing</em> — a transparent badge and an untinted well — which looks
   * like a section that was never styled rather than a name that was never defined. Both themes,
   * because a hue defined in one and not the other is a badge that vanishes when the theme is
   * switched, which nobody would be looking at when they switched it.
   */
  it('is defined in style.css, in both themes, for every section the three pickers offer', () => {
    const css = readFileSync(resolve(import.meta.dirname, '..', 'style.css'), 'utf8')
    const dark = css.slice(css.indexOf('.dark {'))
    const sections = [...SCOPE_MEASUREMENT_SECTIONS, ...SCOPE_MATH_SECTIONS, ...STIMULUS_SECTIONS]
    expect(sections.length).toBeGreaterThan(10)

    for (const section of sections) {
      for (const name of [
        `--kind-${section.tone}-badge-bg:`,
        `--kind-${section.tone}-badge-fg:`,
        `--kind-${section.tone}-badge-border:`,
        `--kind-${section.tone}-chip-bg:`,
        `--kind-${section.tone}-chip-fg:`,
        `--kind-${section.mark}-chip-fg:`,
      ]) {
        expect(css, `${section.id} wants ${name}`).toContain(name)
        expect(dark, `${section.id} wants ${name} in the dark theme`).toContain(name)
      }
    }
  })

  it('never marks a figure in the hue it is drawn in, which would draw nothing', () => {
    // A trace and its annotation in one colour is a figure with no annotation: the whole point of
    // the pair is that a level line reads as a level line rather than as a second trace.
    for (const section of [...SCOPE_MEASUREMENT_SECTIONS, ...SCOPE_MATH_SECTIONS, ...STIMULUS_SECTIONS]) {
      expect(section.mark, section.id).not.toBe(section.tone)
    }
  })
})
