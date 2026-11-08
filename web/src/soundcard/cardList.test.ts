import { describe, expect, it } from 'vitest'
import type { SoundCardHostApiOption, SoundCardOption } from '../api/client'
import {
  buildCards,
  clampChannelOffset,
  hostApiNotes,
  keyOf,
  reselectCardKey,
  savedCardNeedsAdvanced,
} from './cardList'

/**
 * Shaped on this bench's real inventory, read from `GET /api/soundcard/devices` on 2026-10-02:
 * 59 devices, 35 card entries, four pieces of hardware, one of which (the MOTU) appears under all
 * four Windows host APIs. The WDM-KS names are verbatim — they are the whole reason that host API
 * cannot be grouped.
 */
function option(partial: Partial<SoundCardOption> & Pick<SoundCardOption, 'hostApi' | 'name'>): SoundCardOption {
  const kind =
    partial.hostApiKind ??
    { ASIO: 'Asio', 'Windows WASAPI': 'Wasapi', 'Windows WDM-KS': 'WdmKs', 'Windows DirectSound': 'DirectSound', MME: 'Mme' }[
      partial.hostApi
    ] ??
    'Other'

  const preference =
    partial.preference ??
    ({ Asio: 0, CoreAudio: 0, Jack: 1, Alsa: 2, WdmKs: 3, Wasapi: 4, DirectSound: 5, Mme: 6 }[
      kind
    ] ?? 7)

  return {
    interfaceName: partial.name,
    hostApiKind: kind,
    maxInputChannels: 2,
    maxOutputChannels: 0,
    isFullDuplex: false,
    trust: kind === 'DirectSound' || kind === 'Mme' ? 'Resamples' : kind === 'Wasapi' ? 'RequiresExclusive' : 'Trusted',
    preference,
    hasControlPanel: kind === 'Asio',
    ...partial,
  }
}

const motuAsio = option({
  hostApi: 'ASIO',
  name: 'MOTU M Series',
  maxInputChannels: 8,
  maxOutputChannels: 4,
  isFullDuplex: true,
})

const inputs: SoundCardOption[] = [
  motuAsio,
  option({ hostApi: 'Windows WASAPI', name: 'In 1-2 (MOTU M Series)', interfaceName: 'MOTU M Series' }),
  option({ hostApi: 'Windows WASAPI', name: 'Microphone (Realtek(R) Audio)', interfaceName: 'Realtek(R) Audio' }),
  option({ hostApi: 'Windows WDM-KS', name: 'In 1-2 (In 1-2)' }),
  option({ hostApi: 'Windows WDM-KS', name: 'Realtek HD Audio Mic input' }),
  option({ hostApi: 'Windows DirectSound', name: 'In 1-2 (MOTU M Series)', interfaceName: 'MOTU M Series' }),
  option({ hostApi: 'MME', name: 'Microsoft-geluidstoewijzing - Input', interfaceName: 'Microsoft-geluidstoewijzing - Input' }),
]

const outputs: SoundCardOption[] = [
  { ...motuAsio },
  option({
    hostApi: 'Windows WASAPI',
    name: 'Out 1-2 (MOTU M Series)',
    interfaceName: 'MOTU M Series',
    maxInputChannels: 0,
    maxOutputChannels: 2,
  }),
  option({
    hostApi: 'Windows WASAPI',
    name: 'Speakers (Realtek(R) Audio)',
    interfaceName: 'Realtek(R) Audio',
    maxInputChannels: 0,
    maxOutputChannels: 2,
  }),
  option({ hostApi: 'Windows WDM-KS', name: 'Out 1-2 (Out 1-2)', maxInputChannels: 0, maxOutputChannels: 2 }),
  option({
    hostApi: 'Windows DirectSound',
    name: 'Out 1-2 (MOTU M Series)',
    interfaceName: 'MOTU M Series',
    maxInputChannels: 0,
    maxOutputChannels: 2,
  }),
]

describe('buildCards, advanced', () => {
  it('is the unfiltered list: every host API, one entry per interface within it', () => {
    const cards = buildCards(inputs, outputs, true)

    expect(cards.map((c) => `${c.interfaceName} — ${c.hostApi}`)).toEqual([
      'MOTU M Series — ASIO',
      'In 1-2 (In 1-2) — Windows WDM-KS',
      'Out 1-2 (Out 1-2) — Windows WDM-KS',
      'Realtek HD Audio Mic input — Windows WDM-KS',
      'MOTU M Series — Windows WASAPI',
      'Realtek(R) Audio — Windows WASAPI',
      'MOTU M Series — Windows DirectSound',
      'Microsoft-geluidstoewijzing - Input — MME',
    ])
  })

  it('merges a card’s two directions into one entry, keeping duplex and the control panel', () => {
    const motu = buildCards(inputs, outputs, true).find((c) => c.hostApi === 'ASIO')

    expect(motu).toMatchObject({ duplex: true, hasControlPanel: true })
  })
})

describe('buildCards, default', () => {
  const cards = buildCards(inputs, outputs, false)

  it('offers one entry per physical card, under its best host API', () => {
    expect(cards.map((c) => `${c.interfaceName} — ${c.hostApi}`)).toEqual([
      'MOTU M Series — ASIO',
      'Realtek(R) Audio — Windows WASAPI',
    ])
  })

  it('drops the host APIs that resample, mapper pseudo-devices and all', () => {
    expect(cards.some((c) => c.trust === 'Resamples')).toBe(false)
    expect(cards.some((c) => c.interfaceName.startsWith('Microsoft-geluidstoewijzing'))).toBe(false)
  })

  it('drops WDM-KS, whose names name no interface and so can never be paired', () => {
    // The point of the rule: these three cannot be grouped onto the hardware they belong to, so a
    // name-based "only where there is nothing better" rule would leave every one of them standing.
    expect(cards.some((c) => c.hostApiKind === 'WdmKs')).toBe(false)
  })

  it('does not let a one-directional host API win a card from one that can drive it', () => {
    // A card whose ASIO driver enumerates inputs only: collapsing on preference alone would hide
    // the WASAPI entry that has both, leaving the card in the list and unusable.
    const halfAsio = option({
      hostApi: 'ASIO',
      name: 'Half Duplex Thing',
      maxInputChannels: 2,
      maxOutputChannels: 0,
    })
    const wasapiIn = option({
      hostApi: 'Windows WASAPI',
      name: 'In (Half Duplex Thing)',
      interfaceName: 'Half Duplex Thing',
    })
    const wasapiOut = option({
      hostApi: 'Windows WASAPI',
      name: 'Out (Half Duplex Thing)',
      interfaceName: 'Half Duplex Thing',
      maxInputChannels: 0,
      maxOutputChannels: 2,
    })

    const built = buildCards([halfAsio, wasapiIn], [wasapiOut], false)

    expect(built.map((c) => c.hostApi)).toEqual(['Windows WASAPI'])
  })

  it('still offers a card no host API can fully drive, rather than hiding it', () => {
    const inputOnly = option({ hostApi: 'Windows WASAPI', name: 'Microphone (Webcam)', interfaceName: 'Webcam' })

    expect(buildCards([inputOnly], [], false).map((c) => c.interfaceName)).toEqual(['Webcam'])
  })
})

describe('savedCardNeedsAdvanced', () => {
  it('is true for a saved device whose host API the default list hides', () => {
    expect(savedCardNeedsAdvanced(inputs, outputs, 'Windows WDM-KS', 'In 1-2 (In 1-2)')).toBe(true)
  })

  it('is true for a card the default list collapsed away, so the saved API stays selectable', () => {
    // The MOTU is offered as ASIO; a config saved against its WASAPI devices points at a card key
    // that is no longer in the list.
    expect(savedCardNeedsAdvanced(inputs, outputs, 'Windows WASAPI', 'In 1-2 (MOTU M Series)')).toBe(true)
  })

  it('is false for a selection the default list already offers', () => {
    expect(savedCardNeedsAdvanced(inputs, outputs, 'ASIO', 'MOTU M Series')).toBe(false)
    expect(savedCardNeedsAdvanced(inputs, outputs, 'Windows WASAPI', 'Microphone (Realtek(R) Audio)')).toBe(false)
  })

  it('is false when nothing is saved, and for a card that is no longer plugged in', () => {
    expect(savedCardNeedsAdvanced(inputs, outputs, '', '')).toBe(false)
    expect(savedCardNeedsAdvanced(inputs, outputs, 'ASIO', 'Focusrite USB ASIO')).toBe(false)
  })
})

describe('reselectCardKey', () => {
  it('keeps a selection the new list still offers', () => {
    const motu = keyOf('ASIO', 'MOTU M Series')

    expect(reselectCardKey(motu, inputs, outputs, false)).toBe(motu)
  })

  it('moves a collapsed-away card onto the same hardware under its best host API', () => {
    // Advanced off with the MOTU's WASAPI entry selected: the card is still there, under ASIO.
    const wasapi = keyOf('Windows WASAPI', 'MOTU M Series')

    expect(reselectCardKey(wasapi, inputs, outputs, false)).toBe(keyOf('ASIO', 'MOTU M Series'))
  })

  it('selects nothing when the card is gone from the list entirely', () => {
    // WDM-KS names no interface, so there is no equivalent entry to move to.
    expect(reselectCardKey(keyOf('Windows WDM-KS', 'In 1-2 (In 1-2)'), inputs, outputs, false)).toBe('')
    expect(reselectCardKey('', inputs, outputs, false)).toBe('')
  })
})

describe('clampChannelOffset', () => {
  it('keeps a pair the device actually has', () => {
    // The MOTU under ASIO: 8 in, so 1/2 through 7/8.
    expect(clampChannelOffset(0, 8)).toBe(0)
    expect(clampChannelOffset(2, 8)).toBe(2)
    expect(clampChannelOffset(6, 8)).toBe(6)
  })

  it('brings a pair chosen on a wider card down onto a two-channel one', () => {
    // The reported dead end: offset 2 (channels 3/4) carried from the MOTU onto any WASAPI device
    // here, every one of which is two channels — and which shows no pair dropdown to correct it.
    expect(clampChannelOffset(2, 2)).toBe(0)
    expect(clampChannelOffset(6, 2)).toBe(0)
  })

  it('clamps to the last whole pair, never past it', () => {
    // 4 output channels is pairs 1/2 and 3/4; an offset of 4 would be 5/6.
    expect(clampChannelOffset(4, 4)).toBe(2)
    expect(clampChannelOffset(99, 6)).toBe(4)
  })

  it('refuses to straddle two pairs', () => {
    // No device lists an odd offset, and 2/3 is not a pair any dropdown offers.
    expect(clampChannelOffset(1, 8)).toBe(0)
    expect(clampChannelOffset(3, 8)).toBe(2)
  })

  it('is 0 for a device with no usable pair at all', () => {
    expect(clampChannelOffset(2, 1)).toBe(0)
    expect(clampChannelOffset(2, 0)).toBe(0)
    expect(clampChannelOffset(-2, 8)).toBe(0)
  })
})

describe('keyOf', () => {
  it('separates the same device name under two host APIs', () => {
    expect(keyOf('ASIO', 'MOTU M Series')).not.toBe(keyOf('Windows WASAPI', 'MOTU M Series'))
  })
})


describe('hostApiNotes', () => {
  function api(
    partial: Partial<SoundCardHostApiOption> & Pick<SoundCardHostApiOption, 'name' | 'kind'>,
  ): SoundCardHostApiOption {
    return {
      deviceCount: 0,
      inputCount: 0,
      outputCount: 0,
      trust: 'Trusted',
      preference: 0,
      ...partial,
    }
  }

  /**
   * The report this exists for (2026-11-07): an interface "not discovered and thus I could not
   * choose it", behind an ASIO4ALL driver. A driver that starts and reports no channels is dropped
   * from both device lists, so the dialog showed nothing and the only three explanations needed
   * three different answers.
   */
  it('names an ASIO driver that reported no channels, and says what to do', () => {
    const notes = hostApiNotes([api({ name: 'ASIO', kind: 'Asio', deviceCount: 1 })], 0)

    expect(notes).toHaveLength(1)
    // Verbatim: the first cut of this read "and it reports any input or output channels", which is
    // the opposite of what happened, and a toContain('channels') passed against it.
    expect(notes[0]!.text).toBe('ASIO listed 1 driver, and it reports no input or output channels.')
    expect(notes[0]!.hint).toContain('control panel')
    expect(notes[0]!.hint).toContain('ASIO4ALL')
  })

  /** Always, not only on an empty list: it is genuinely wrong, rare, and fixable. */
  it('names a channel-less driver even when other cards were found', () => {
    const notes = hostApiNotes([api({ name: 'ASIO', kind: 'Asio', deviceCount: 1 })], 3)

    expect(notes).toHaveLength(1)
  })

  /**
   * An empty host API is the ordinary case — most benches have no ASIO interface — so a standing
   * line about it on every QA403 bench's dialog would be noise. With an empty dropdown in front of
   * the reader, what was tried is exactly what they want.
   */
  it('reports an empty host API only when no card was found at all', () => {
    const apis = [api({ name: 'ASIO', kind: 'Asio' })]

    expect(hostApiNotes(apis, 1)).toEqual([])
    expect(hostApiNotes(apis, 0)).toHaveLength(1)
    expect(hostApiNotes(apis, 0)[0]!.text).toContain('found no devices')
    // The useful half: it proves ASIO is in this build, so the missing driver is not ours.
    expect(hostApiNotes(apis, 0)[0]!.hint).toContain('does carry ASIO')
  })

  /** Nothing to say about a host API this picker would not offer anyway. */
  it('says nothing about the host APIs the picker does not offer', () => {
    const apis = [
      api({ name: 'Windows DirectSound', kind: 'DirectSound', deviceCount: 4, preference: 5 }),
      api({ name: 'MME', kind: 'Mme', preference: 6 }),
      api({ name: 'Windows WDM-KS', kind: 'WdmKs', deviceCount: 2, preference: 3 }),
    ]

    expect(hostApiNotes(apis, 0)).toEqual([])
  })

  it('says nothing about a host API that found usable devices', () => {
    const apis = [
      api({ name: 'ASIO', kind: 'Asio', deviceCount: 1, inputCount: 1, outputCount: 1 }),
      api({ name: 'Windows WASAPI', kind: 'Wasapi', deviceCount: 3, inputCount: 2, outputCount: 1, preference: 4 }),
    ]

    expect(hostApiNotes(apis, 2)).toEqual([])
  })

  /** One direction is enough to be worth offering — the measured Windows topology has nothing else. */
  it('says nothing about a host API with inputs but no outputs', () => {
    const apis = [api({ name: 'Windows WASAPI', kind: 'Wasapi', deviceCount: 1, inputCount: 1, preference: 4 })]

    expect(hostApiNotes(apis, 1)).toEqual([])
  })

  /** Non-ASIO host APIs get the reading, not the advice: there is no driver panel to send anyone to. */
  it('offers no hint for a host API with nothing to try', () => {
    const notes = hostApiNotes(
      [api({ name: 'Windows WASAPI', kind: 'Wasapi', deviceCount: 2, preference: 4 })],
      0,
    )

    expect(notes).toHaveLength(1)
    expect(notes[0]!.text).toBe(
      'Windows WASAPI listed 2 devices, and none of them reports any input or output channels.',
    )
    expect(notes[0]!.hint).toBeNull()
  })
})
