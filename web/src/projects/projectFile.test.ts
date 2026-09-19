import { describe, expect, it } from 'vitest'
import {
  ensureExtension,
  fileNameOf,
  isRooted,
  joinPath,
  parentPath,
  sanitizeFileName,
  suggestLocation,
} from './projectFile'

describe('sanitizeFileName', () => {
  it('matches the server rule: forbidden set, control chars, whitespace runs, edge dots', () => {
    expect(sanitizeFileName('GM70 amp')).toBe('GM70 amp')
    expect(sanitizeFileName('  spaced   out  ')).toBe('spaced out')
    expect(sanitizeFileName('a/b\\c:d*e?f"g<h>i|j')).toBe('abcdefghij')
    expect(sanitizeFileName('...dots...')).toBe('dots')
    expect(sanitizeFileName('tab\tand\nnewline')).toBe('tab and newline')
    expect(sanitizeFileName('')).toBe('Project')
    expect(sanitizeFileName('???')).toBe('Project')
    expect(sanitizeFileName('Ω 6L6 (push-pull)')).toBe('Ω 6L6 (push-pull)')
  })
})

describe('ensureExtension', () => {
  it('appends once, ignoring case', () => {
    expect(ensureExtension('amp')).toBe('amp.qa403')
    expect(ensureExtension('amp.qa403')).toBe('amp.qa403')
    expect(ensureExtension('amp.QA403')).toBe('amp.QA403')
  })
})

describe('joinPath', () => {
  it('uses the given separator and never doubles it', () => {
    expect(joinPath('C:\\Users\\j\\Documents', 'a.qa403', '\\')).toBe('C:\\Users\\j\\Documents\\a.qa403')
    expect(joinPath('C:\\', 'a.qa403', '\\')).toBe('C:\\a.qa403')
    expect(joinPath('/home/j', 'a.qa403', '/')).toBe('/home/j/a.qa403')
    expect(joinPath('/', 'a.qa403', '/')).toBe('/a.qa403')
    expect(joinPath('', 'a.qa403', '/')).toBe('a.qa403')
  })
})

describe('fileNameOf / parentPath', () => {
  it('split on either separator', () => {
    expect(fileNameOf('C:\\bench\\amp.qa403')).toBe('amp.qa403')
    expect(fileNameOf('/home/j/amp.qa403')).toBe('amp.qa403')
    expect(fileNameOf('amp.qa403')).toBe('amp.qa403')
    expect(parentPath('C:\\bench\\amp.qa403')).toBe('C:\\bench')
    expect(parentPath('/home/j/amp.qa403')).toBe('/home/j')
    expect(parentPath('amp.qa403')).toBe('')
  })

  it('keeps a root as a root', () => {
    expect(parentPath('C:\\amp.qa403')).toBe('C:\\')
    expect(parentPath('/amp.qa403')).toBe('/')
  })
})

describe('isRooted', () => {
  it('accepts drive, UNC, POSIX and home paths and rejects relative ones', () => {
    expect(isRooted('C:\\x')).toBe(true)
    expect(isRooted('c:/x')).toBe(true)
    expect(isRooted('\\\\server\\share')).toBe(true)
    expect(isRooted('/home')).toBe(true)
    expect(isRooted('~')).toBe(true)
    expect(isRooted('~/x')).toBe(true)
    expect(isRooted('x.qa403')).toBe(false)
    expect(isRooted('sub\\x.qa403')).toBe(false)
    expect(isRooted('C:x')).toBe(false)
  })
})

describe('suggestLocation', () => {
  it('sanitizes the name and adds the extension under the folder', () => {
    expect(suggestLocation('GM70: amp', 'C:\\Users\\j\\Documents\\QA403 Projects', '\\'))
      .toBe('C:\\Users\\j\\Documents\\QA403 Projects\\GM70 amp.qa403')
    expect(suggestLocation('', '/home/j/QA403 Projects', '/')).toBe('/home/j/QA403 Projects/Project.qa403')
  })
})
