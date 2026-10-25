import { describe, expect, it } from 'vitest'
import { parseEngine, prefersGpuSurface } from './useDecaySurfaceEngine'

describe('parseEngine', () => {
  it('reads the one stored value that means anything', () => {
    expect(parseEngine('Classic')).toBe('Classic')
    expect(parseEngine('Auto')).toBe('Auto')
  })

  /**
   * An absent key is the ordinary case — nobody has opened Configuration yet — and a cleared or
   * blocked store answers the same way. Both must land on the GPU surface being allowed, or the
   * feature would be off for everyone until they went looking for it.
   */
  it('defaults to Auto for anything it does not recognise', () => {
    for (const stored of [null, '', 'auto', 'CLASSIC', '3d', 'true', '{}']) {
      expect(parseEngine(stored), JSON.stringify(stored)).toBe('Auto')
    }
  })
})

describe('prefersGpuSurface', () => {
  it('is the preference alone, and Classic is the one that says no', () => {
    expect(prefersGpuSurface('Auto')).toBe(true)
    expect(prefersGpuSurface('Classic')).toBe(false)
  })
})
