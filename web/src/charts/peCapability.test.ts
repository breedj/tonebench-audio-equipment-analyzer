import { afterEach, describe, expect, it, vi } from 'vitest'
import { decaySurfaceGpuAvailable, hasWebGpuObject, resetGpuProbe } from './peCapability'

afterEach(() => {
  resetGpuProbe()
  vi.unstubAllGlobals()
})

/**
 * This is a correctness gate rather than an optimisation, so its failure mode matters: without
 * WebGPU the engine draws a plausible surface that is not the measurement, silently. Every case
 * here is therefore about answering <c>false</c> rather than about answering quickly.
 */
describe('decaySurfaceGpuAvailable', () => {
  it('is false where the browser has no navigator.gpu at all', async () => {
    vi.stubGlobal('navigator', {})
    expect(await decaySurfaceGpuAvailable()).toBe(false)
  })

  /**
   * The case a `'gpu' in navigator` test alone would get wrong, and the ordinary one on Linux:
   * the object exists, the driver or the GPU is not on the supported list, and the adapter
   * request resolves null.
   */
  it('is false where navigator.gpu exists but no adapter can be had', async () => {
    vi.stubGlobal('navigator', { gpu: { requestAdapter: async () => null } })
    expect(await decaySurfaceGpuAvailable()).toBe(false)
  })

  it('is false where requestAdapter throws rather than resolving', async () => {
    vi.stubGlobal('navigator', { gpu: { requestAdapter: async () => { throw new Error('no') } } })
    expect(await decaySurfaceGpuAvailable()).toBe(false)
  })

  it('is true only when an adapter actually comes back', async () => {
    vi.stubGlobal('navigator', { gpu: { requestAdapter: async () => ({ name: 'adapter' }) } })
    expect(await decaySurfaceGpuAvailable()).toBe(true)
  })

  /** One probe per page: every chart and every export asks, and an adapter request is not free. */
  it('asks the browser once however many times it is called', async () => {
    const requestAdapter = vi.fn(async () => ({}))
    vi.stubGlobal('navigator', { gpu: { requestAdapter } })
    await Promise.all([decaySurfaceGpuAvailable(), decaySurfaceGpuAvailable(), decaySurfaceGpuAvailable()])
    expect(requestAdapter).toHaveBeenCalledTimes(1)
  })
})

describe('hasWebGpuObject', () => {
  it('reads the object off whatever it is handed, and false means no', () => {
    expect(hasWebGpuObject({ gpu: {} })).toBe(true)
    expect(hasWebGpuObject({})).toBe(false)
    expect(hasWebGpuObject(undefined)).toBe(false)
  })
})
