import { describe, expect, it } from 'vitest'
import { parseEngine, prefers3dSurface } from './useDecaySurfaceEngine'

describe('parseEngine', () => {
  it('reads the one stored value that means anything', () => {
    expect(parseEngine('Classic')).toBe('Classic')
    expect(parseEngine('Auto')).toBe('Auto')
  })

  /**
   * An absent key is the ordinary case — nobody has opened Configuration yet — and a cleared or
   * blocked store answers the same way. Both must land on the 3D surface being allowed, or the
   * feature would be off for everyone until they went looking for it.
   */
  it('defaults to Auto for anything it does not recognise', () => {
    for (const stored of [null, '', 'auto', 'CLASSIC', '3d', 'true', '{}']) {
      expect(parseEngine(stored), JSON.stringify(stored)).toBe('Auto')
    }
  })

  /**
   * The spelling is storage compatibility and nothing else — see the type's own remarks. A bench
   * that chose the flat projection before the engine was replaced has `'Classic'` in its browser
   * storage, and renaming the value would hand it the 3D chart back without being asked.
   */
  it('still reads the spelling a browser already has stored', () => {
    expect(prefers3dSurface(parseEngine('Classic'))).toBe(false)
  })
})

describe('prefers3dSurface', () => {
  it('is the preference alone, and Classic is the one that says no', () => {
    expect(prefers3dSurface('Auto')).toBe(true)
    expect(prefers3dSurface('Classic')).toBe(false)
  })
})
