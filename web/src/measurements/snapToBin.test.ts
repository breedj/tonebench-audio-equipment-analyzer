import { describe, expect, it } from 'vitest'
import { canSnapToBin, snapToBinOnLoad } from './snapToBin'

describe('canSnapToBin', () => {
  it('covers the two kinds that carry a tone list', () => {
    expect(canSnapToBin('Tones')).toBe(true)
    // The comb snaps tone by tone, and its TD+N reading depends on it — unsnapped, every gap
    // between two tones reads their own Hann sidelobes as distortion.
    expect(canSnapToBin('Multitone')).toBe(true)
  })

  it('covers nothing else, since snapping rewrites a tone list and these have none', () => {
    for (const kind of ['WhiteNoise', 'EchoChirp', 'Square', 'Triangle', 'Off'] as const)
      expect(canSnapToBin(kind)).toBe(false)
  })
})

describe('snapToBinOnLoad', () => {
  it('restores what the measurement was saved with', () => {
    expect(snapToBinOnLoad(true, 'Tones')).toBe(true)
    expect(snapToBinOnLoad(false, 'Tones')).toBe(false)
    expect(snapToBinOnLoad(true, 'Multitone')).toBe(true)
    expect(snapToBinOnLoad(false, 'Multitone')).toBe(false)
  })

  /**
   * A measurement written before the kinds were gated could carry `snapToBin` true alongside a
   * stimulus that has no tones to snap.
   */
  it('does not bring a stored true back for a stimulus that cannot snap', () => {
    expect(snapToBinOnLoad(true, 'WhiteNoise')).toBe(false)
    expect(snapToBinOnLoad(true, 'EchoChirp')).toBe(false)
    expect(snapToBinOnLoad(true, 'Square')).toBe(false)
  })

  /**
   * The bug this module was extracted for, reported 2026-10-08. A brand-new measurement clones a
   * live generator that Stop has silenced, so its stored kind is `Off` — not a stimulus, and
   * GeneratorChannelPanel answers it by leaving sine tones selected. Read as "not Tones" it
   * unticked a box the server had deliberately ticked: BuildDefaultParametersJsonForNewType forces
   * snapping on for every new type, so the measurement's own parameters said true while the panel
   * showed false.
   */
  it('reads Off as the sine tones the panel will actually show', () => {
    expect(snapToBinOnLoad(true, 'Off')).toBe(true)
    expect(snapToBinOnLoad(false, 'Off')).toBe(false)
  })
})
