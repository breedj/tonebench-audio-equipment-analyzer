import { describe, expect, it } from 'vitest'
import type { GeneratorChannelRequest, StoredGeneratorConfig } from '../api/client'
import { scopeGeneratorFromParams, scopeGeneratorToParams } from './generator'

const tone: GeneratorChannelRequest = {
  enabled: true,
  kind: 'Tones',
  tones: [{ freqHz: 1000, ampDbv: -6 }],
}

const silent: GeneratorChannelRequest = { enabled: false, kind: 'Tones', tones: [{ freqHz: 1000, ampDbv: -6 }] }

function paramsWith(generator: unknown): string {
  return JSON.stringify({ sampleRateHz: 192000, recordSeconds: 0.02, generator })
}

describe('scopeGeneratorFromParams', () => {
  it('reads the stimulus a measurement saved', () => {
    const stored = scopeGeneratorFromParams(paramsWith({ left: tone, right: silent, snapToBin: false }))
    expect(stored?.left.kind).toBe('Tones')
    expect(stored?.left.enabled).toBe(true)
    expect(stored?.right.enabled).toBe(false)
    expect(stored?.snapToBin).toBe(false)
  })

  it('answers nothing for a measurement that has none', () => {
    // A type seeded by DefaultOscilloscopeParametersJson, which states no generator at all: the
    // panel then keeps its own defaults rather than staging a half-restore.
    expect(scopeGeneratorFromParams(JSON.stringify({ sampleRateHz: 192000 }))).toBeNull()
    expect(scopeGeneratorFromParams(null)).toBeNull()
    expect(scopeGeneratorFromParams(undefined)).toBeNull()
    expect(scopeGeneratorFromParams('')).toBeNull()
  })

  it('answers nothing rather than throwing on a document it cannot read', () => {
    expect(scopeGeneratorFromParams('{ not json')).toBeNull()
    expect(scopeGeneratorFromParams(paramsWith('a string'))).toBeNull()
  })

  it('takes both channels or neither', () => {
    // GeneratorPanel reads the pair: half a restore would stage one channel's enable against the
    // other channel's default, which is a stimulus the measurement never had.
    expect(scopeGeneratorFromParams(paramsWith({ left: tone, snapToBin: true }))).toBeNull()
    expect(scopeGeneratorFromParams(paramsWith({ right: tone, snapToBin: true }))).toBeNull()
  })
})

describe('scopeGeneratorToParams', () => {
  it('round-trips what the panel composed', () => {
    const params = scopeGeneratorToParams({ left: tone, right: silent, snapToBin: true })
    const back = scopeGeneratorFromParams(JSON.stringify({ sampleRateHz: 192000, ...params }))
    expect(back?.left).toEqual(tone)
    expect(back?.right).toEqual(silent)
    expect(back?.snapToBin).toBe(true)
  })

  it('keeps the stimulus a measurement already had when nothing is composed', () => {
    // The panel is not mounted to ask -- a save from a screen this measurement is not open on.
    // Writing nothing there would drop the stimulus it had; the fallback is what it had.
    const stored: StoredGeneratorConfig = {
      left: tone,
      right: silent,
      snapToBin: false,
    } as unknown as StoredGeneratorConfig
    expect(scopeGeneratorToParams(null, stored).generator).toBe(stored)
    expect(scopeGeneratorToParams(undefined, stored).generator).toBe(stored)
  })

  it('writes nothing at all when there is neither', () => {
    // An empty object, so the spread into params_json adds no `generator` key rather than a null
    // one — a null would read back as "this measurement plays nothing", which is a different claim.
    expect(scopeGeneratorToParams(null)).toEqual({})
  })
})
