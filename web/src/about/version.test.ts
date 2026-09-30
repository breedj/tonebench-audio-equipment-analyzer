import { describe, expect, it } from 'vitest'
import { splitVersion } from './version'

describe('splitVersion', () => {
  it('separates the release from the source revision the SDK appends', () => {
    // Exactly what /api/app answered on a dev build, 2026-09-30.
    expect(splitVersion('0.2.0+96350a9f134a426ebd5d1f87d26353c92b536174')).toEqual({
      release: '0.2.0',
      build: '96350a9',
    })
  })

  it('keeps a pre-release tag on the release, where it belongs', () => {
    expect(splitVersion('1.0.0-rc.2+abcdef1234567890')).toEqual({ release: '1.0.0-rc.2', build: 'abcdef1' })
  })

  it('reports no build for a plain version', () => {
    expect(splitVersion('1.4.2')).toEqual({ release: '1.4.2', build: null })
  })

  it('treats a trailing plus as no build rather than an empty one', () => {
    expect(splitVersion('1.0.0+')).toEqual({ release: '1.0.0', build: null })
  })

  it('splits on the first plus, so build metadata containing one stays whole', () => {
    expect(splitVersion('1.0.0+a+b').release).toBe('1.0.0')
  })
})
