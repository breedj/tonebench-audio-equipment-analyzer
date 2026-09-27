import { describe, expect, it } from 'vitest'
import { spectrumAxisOverlay } from './spectrumOverlayColors'
import { FFT_SPECTRUM_KIND, STEPPED_RESPONSE_KIND, THD_VS_FREQUENCY_KIND } from './kinds'
import type { OverlayEntry } from '../composables/useMeasurements'

/**
 * An overlay as far as `spectrumAxisOverlay` reads it: its kind, whether it is checked, and whether
 * it carries a spectrum channel. Everything else on a MeasurementDetail is irrelevant here, hence
 * the cast rather than a fixture of forty fields.
 */
function overlay(id: number, kind: string, visible: boolean, channels: number[]): OverlayEntry {
  return {
    id,
    visible,
    detail: { kind, channelsData: channels.map((channel) => ({ channel })) },
  } as unknown as OverlayEntry
}

const fft = (id: number, visible = true, channels = [0]) => overlay(id, FFT_SPECTRUM_KIND, visible, channels)

describe('spectrumAxisOverlay', () => {
  it('skips an overlay belonging to another screen', () => {
    // The reported bug: a stepped-response history item checked first, so it sits at index 0 of the
    // app-wide list, and the FFT overlay the user then checks is the second entry.
    const overlays = [overlay(1, STEPPED_RESPONSE_KIND, true, []), fft(2)]
    expect(spectrumAxisOverlay(overlays)?.id).toBe(2)
  })

  it('skips a sweep overlay too, whatever it claims to carry', () => {
    const overlays = [overlay(1, THD_VS_FREQUENCY_KIND, true, [0, 1]), fft(2)]
    expect(spectrumAxisOverlay(overlays)?.id).toBe(2)
  })

  it('skips an FFT overlay that is not checked', () => {
    // An unchecked overlay draws nothing (its series are null-filled), so an axis built from it
    // would be an empty plot with a frequency scale — worse than the "Waiting for data…" the chart
    // shows instead.
    expect(spectrumAxisOverlay([fft(1, false), fft(2, true)])?.id).toBe(2)
  })

  it('skips an FFT overlay with no stored channel', () => {
    expect(spectrumAxisOverlay([fft(1, true, []), fft(2)])?.id).toBe(2)
  })

  it('takes a right-only result, since its axis is as good as a left-only one', () => {
    expect(spectrumAxisOverlay([fft(1, true, [1])])?.id).toBe(1)
  })

  it('takes the first eligible one, so the axis does not move as later overlays are checked', () => {
    expect(spectrumAxisOverlay([fft(1), fft(2), fft(3)])?.id).toBe(1)
  })

  it('is null when nothing eligible is loaded', () => {
    expect(spectrumAxisOverlay([])).toBeNull()
    expect(spectrumAxisOverlay([overlay(1, STEPPED_RESPONSE_KIND, true, [])])).toBeNull()
  })
})
