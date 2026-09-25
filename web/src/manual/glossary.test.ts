import { describe, expect, it } from 'vitest'
import { GLOSSARY, filterGlossary, glossaryMatches, groupGlossary, sortedGlossary } from './glossary'
import { MANUAL_PAGES } from './pages'

describe('the glossary entries', () => {
  it('names a real manual page in every cross-reference', () => {
    const ids = new Set(MANUAL_PAGES.map((p) => p.id))
    for (const entry of GLOSSARY) {
      for (const id of entry.see ?? []) {
        expect(ids, `${entry.term} points at "${id}"`).toContain(id)
      }
    }
  })

  it('defines each term once', () => {
    const seen = new Set<string>()
    for (const entry of GLOSSARY) {
      const key = entry.term.toLowerCase()
      expect(seen, `${entry.term} is defined twice`).not.toContain(key)
      seen.add(key)
    }
  })

  it('never files an alias under a term that has its own entry', () => {
    // An alias is only a search hint. One that duplicates a headword means the reader typing it
    // gets two hits for one idea, and the entry it was added to is probably the wrong home for it.
    const terms = new Set(GLOSSARY.map((e) => e.term.toLowerCase()))
    for (const entry of GLOSSARY) {
      for (const alias of entry.aliases ?? []) {
        expect(terms, `${entry.term} aliases the headword "${alias}"`).not.toContain(alias.toLowerCase())
      }
    }
  })

  it('writes every definition as a sentence', () => {
    for (const entry of GLOSSARY) {
      expect(entry.definition.length, entry.term).toBeGreaterThan(40)
      expect(entry.definition.trimEnd().endsWith('.'), entry.term).toBe(true)
    }
  })

  it('leaves no markup in a definition, which the page renders literally', () => {
    // The page interpolates these as text, so an emphasis marker carried over from prose habits
    // reaches the reader as an asterisk. Caught on screen once, on the group delay entry.
    for (const entry of GLOSSARY) {
      const text = `${entry.definition} ${entry.formula ?? ''}`
      expect(text, entry.term).not.toMatch(/[*_<>]|\[.+\]\(.+\)/)
    }
  })

  it('covers the terms the screens put on the reader without explanation', () => {
    // Not a wish list: each of these is a label, an axis or a column the user meets on screen or
    // in a CSV, so the glossary is where someone who does not recognise it will look.
    const expected = [
      'Bandwidth',
      'Coherence',
      'Crest factor',
      'Damping factor',
      'dBV',
      'ENBW',
      'FFT',
      'Group delay',
      'Impedance',
      'Intermodulation distortion',
      'Nyquist frequency',
      'Nyquist plot',
      'Phase delay',
      'THD',
      'THD+N',
      'Weighting',
    ]
    const terms = new Set(GLOSSARY.map((e) => e.term))
    for (const term of expected) expect(terms).toContain(term)
  })
})

describe('sorting and grouping', () => {
  it('sorts alphabetically without regard to case', () => {
    const terms = sortedGlossary().map((e) => e.term.toLowerCase())
    expect(terms).toEqual([...terms].sort())
  })

  it('files the dB units together under D rather than scattering them by case', () => {
    const letters = groupGlossary().map((g) => g.letter)
    expect(letters).toEqual([...new Set(letters)])
    const d = groupGlossary().find((g) => g.letter === 'D')
    expect(d?.entries.map((e) => e.term)).toContain('dBV')
  })

  it('groups every entry exactly once', () => {
    const grouped = groupGlossary().flatMap((g) => g.entries)
    expect(grouped).toHaveLength(GLOSSARY.length)
  })
})

describe('searching', () => {
  const entry = GLOSSARY.find((e) => e.term === 'Group delay')!

  it('matches a term however it is cased', () => {
    expect(glossaryMatches(entry, 'GROUP DELAY')).toBe(true)
  })

  it('matches on an alias nobody spells out', () => {
    const enbw = GLOSSARY.find((e) => e.term === 'ENBW')!
    expect(glossaryMatches(enbw, 'equivalent noise bandwidth')).toBe(true)
  })

  it('matches a word from the definition, not just the term', () => {
    expect(glossaryMatches(entry, 'smoothing window')).toBe(true)
  })

  it('requires every word, so a phrase narrows the list', () => {
    const all = filterGlossary('delay').length
    const both = filterGlossary('phase delay').length
    expect(both).toBeLessThan(all)
    expect(both).toBeGreaterThan(0)
  })

  it('returns everything for an empty query', () => {
    expect(filterGlossary('')).toHaveLength(GLOSSARY.length)
    expect(filterGlossary('   ')).toHaveLength(GLOSSARY.length)
  })
})
