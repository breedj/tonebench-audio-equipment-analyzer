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
    expect(ensureExtension('amp')).toBe('amp.tbench')
    expect(ensureExtension('amp.tbench')).toBe('amp.tbench')
    expect(ensureExtension('amp.TBench')).toBe('amp.TBench')
  })

  // A project written before the rename keeps its own extension, or saving amp.qa403 would write
  // amp.qa403.tbench and leave the original beside it. Mirrors ProjectPaths.EnsureExtension.
  it('leaves the legacy extension alone', () => {
    expect(ensureExtension('amp.qa403')).toBe('amp.qa403')
    expect(ensureExtension('amp.QA403')).toBe('amp.QA403')
  })
})

describe('joinPath', () => {
  it('uses the given separator and never doubles it', () => {
    expect(joinPath('C:\\Users\\j\\Documents', 'a.tbench', '\\')).toBe('C:\\Users\\j\\Documents\\a.tbench')
    expect(joinPath('C:\\', 'a.tbench', '\\')).toBe('C:\\a.tbench')
    expect(joinPath('/home/j', 'a.tbench', '/')).toBe('/home/j/a.tbench')
    expect(joinPath('/', 'a.tbench', '/')).toBe('/a.tbench')
    expect(joinPath('', 'a.tbench', '/')).toBe('a.tbench')
  })
})

describe('fileNameOf / parentPath', () => {
  it('split on either separator', () => {
    expect(fileNameOf('C:\\bench\\amp.tbench')).toBe('amp.tbench')
    expect(fileNameOf('/home/j/amp.tbench')).toBe('amp.tbench')
    expect(fileNameOf('amp.tbench')).toBe('amp.tbench')
    expect(parentPath('C:\\bench\\amp.tbench')).toBe('C:\\bench')
    expect(parentPath('/home/j/amp.tbench')).toBe('/home/j')
    expect(parentPath('amp.tbench')).toBe('')
  })

  it('keeps a root as a root', () => {
    expect(parentPath('C:\\amp.tbench')).toBe('C:\\')
    expect(parentPath('/amp.tbench')).toBe('/')
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
    expect(isRooted('x.tbench')).toBe(false)
    expect(isRooted('sub\\x.tbench')).toBe(false)
    expect(isRooted('C:x')).toBe(false)
  })
})

describe('suggestLocation', () => {
  it('sanitizes the name and adds the extension under the folder', () => {
    expect(suggestLocation('GM70: amp', 'C:\\Users\\j\\Documents\\Tonebench Projects', '\\'))
      .toBe('C:\\Users\\j\\Documents\\Tonebench Projects\\GM70 amp.tbench')
    expect(suggestLocation('', '/home/j/Tonebench Projects', '/')).toBe('/home/j/Tonebench Projects/Project.tbench')
  })
})
