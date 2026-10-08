import { describe, expect, it } from 'vitest'
import { soleBandOverlay } from './spectrumOverlayColors'
import { FFT_SPECTRUM_KIND, STEPPED_RESPONSE_KIND, THD_VS_FREQUENCY_KIND } from './kinds'
import type { OverlayEntry } from '../composables/useMeasurements'

/** Only the three things this reads: kind, checked, and whether it carries a spectrum channel. */
function overlay(id: number, kind: string, visible: boolean, channels: number[]): OverlayEntry {
  return {
    id,
    visible,
    detail: { kind, channelsData: channels.map((channel) => ({ channel })) },
  } as unknown as OverlayEntry
}

const fft = (id: number, visible = true, channels = [0]) => overlay(id, FFT_SPECTRUM_KIND, visible, channels)

describe('soleBandOverlay', () => {
  it('is the overlay when exactly one is visible', () => {
    expect(soleBandOverlay([fft(1)])?.id).toBe(1)
    expect(soleBandOverlay([fft(1), fft(2, false), fft(3, false)])?.id).toBe(1)
  })

  /**
   * The whole of the rule. Three overlays plus the live trace is four translucent bands over each
   * other, which is why the band was live-only to begin with.
   */
  it('is nothing once a second overlay is shown', () => {
    expect(soleBandOverlay([fft(1), fft(2)])).toBeNull()
    expect(soleBandOverlay([fft(1), fft(2), fft(3)])).toBeNull()
  })

  it('is nothing when none is shown', () => {
    expect(soleBandOverlay([])).toBeNull()
    expect(soleBandOverlay([fft(1, false)])).toBeNull()
  })

  /**
   * Overlays are app-wide, so the list routinely holds items belonging to other screens. They are
   * not candidates, and — the part worth pinning — they do not count towards "only one" either:
   * a sweep checked on another screen must not take the band away from the one FFT overlay here.
   */
  it('ignores overlays from other screens entirely', () => {
    const withSweep = [overlay(1, THD_VS_FREQUENCY_KIND, true, [0, 1]), fft(2)]
    expect(soleBandOverlay(withSweep)?.id).toBe(2)

    const withResponse = [overlay(1, STEPPED_RESPONSE_KIND, true, []), fft(2)]
    expect(soleBandOverlay(withResponse)?.id).toBe(2)
  })

  /** An FFT overlay carrying no channel has no columns to draw a band from. */
  it('is nothing for an overlay with no spectrum channel', () => {
    expect(soleBandOverlay([fft(1, true, [])])).toBeNull()
  })

  it('takes either channel as enough', () => {
    expect(soleBandOverlay([fft(1, true, [1])])?.id).toBe(1)
    expect(soleBandOverlay([fft(1, true, [0, 1])])?.id).toBe(1)
  })
})
