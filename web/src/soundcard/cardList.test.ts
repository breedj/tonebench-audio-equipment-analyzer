import { describe, expect, it } from 'vitest'
import type { SoundCardHostApiOption, SoundCardOption } from '../api/client'
import {
  buildCards,
  cardsOffering,
  clampChannelOffset,
  devicePairing,
  hostApiNotes,
  keyOf,
  reselectCardKey,
  matchHostApis,
  savedCardNeedsAdvanced,
  splitKey,
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

  /**
   * **The bug this pair of cases exists for**, reported 2026-11-12: a two-card bench came up with
   * Advanced ticked on every open and no way to untick it.
   *
   * The MOTU under WASAPI really is collapsed away from the *plain* default list — the case two
   * tests above — but a separate-devices bench never sees that list. Each dropdown is built
   * collapsed towards the other side's host API, so with a WASAPI output chosen the input dropdown
   * offers the MOTU under WASAPI and the saved card is reachable without Advanced at all. Asking
   * the plain list answered a question about a list that is not on screen.
   */
  it('is false for a two-card WASAPI pairing, where each dropdown collapses towards the other side', () => {
    expect(
      savedCardNeedsAdvanced(
        inputs,
        outputs,
        'Windows WASAPI',
        'In 1-2 (MOTU M Series)',
        'Windows WASAPI',
        'Speakers (Realtek(R) Audio)',
      ),
    ).toBe(false)
  })

  /** And the output is checked too, which the single-direction version never did. */
  it('is true when only the output needs the full list', () => {
    expect(
      savedCardNeedsAdvanced(
        inputs,
        outputs,
        'ASIO',
        'MOTU M Series',
        'Windows WDM-KS',
        'Out 1-2 (Out 1-2)',
      ),
    ).toBe(true)
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

/**
 * Two devices that are not one interface — the case the separate-devices picker exists for, and
 * the one the single-card picker deliberately cannot express.
 */
describe('devicePairing', () => {
  it('calls one card both ways duplex, which is the guarantee the single picker makes', () => {
    expect(devicePairing('asio\u0000MOTU', 'asio\u0000MOTU')).toBe('duplex')
  })

  it('calls two different cards separate, because that is two clocks', () => {
    expect(devicePairing('wasapi\u0000Cosmos', 'wasapi\u0000D50 III')).toBe('separate')
  })

  it('names which half is missing rather than calling a one-direction bench unusable', () => {
    expect(devicePairing('wasapi\u0000Cosmos', '')).toBe('inputOnly')
    expect(devicePairing('', 'wasapi\u0000D50 III')).toBe('outputOnly')
  })

  /** The one state that cannot be opened at all — SoundCardSettings.IsUsable being false. */
  it('calls nothing chosen none', () => {
    expect(devicePairing('', '')).toBe('none')
  })
})

describe('cardsOffering', () => {
  /**
   * A capture-only ADC: the shape of an E1DA Cosmos, whose only digital port is a USB-C out. It
   * has to reach the Input card dropdown and stay out of the Output one — offered in both, it
   * would collapse the output side into a blank device list with nothing saying why.
   */
  const cosmos = option({
    hostApi: 'Windows WASAPI',
    name: 'Cosmos ADC',
    maxInputChannels: 2,
    maxOutputChannels: 0,
  })

  const withCosmos = [...inputs, cosmos]

  it('offers a capture-only card to the input side only', () => {
    const cards = buildCards(withCosmos, outputs, false)

    expect(cardsOffering(cards, withCosmos).map((c) => c.interfaceName)).toContain('Cosmos ADC')
    expect(cardsOffering(cards, outputs).map((c) => c.interfaceName)).not.toContain('Cosmos ADC')
  })

  it('offers a full-duplex card to both sides', () => {
    const cards = buildCards(withCosmos, outputs, false)

    expect(cardsOffering(cards, withCosmos).map((c) => c.interfaceName)).toContain('MOTU M Series')
    expect(cardsOffering(cards, outputs).map((c) => c.interfaceName)).toContain('MOTU M Series')
  })

  it('offers nothing when the direction has no devices at all', () => {
    expect(cardsOffering(buildCards(inputs, outputs, false), [])).toEqual([])
  })
})

/**
 * The picker must not offer a pair PortAudio cannot open.
 *
 * Measured on this bench 2026-11-11: a MOTU under ASIO against a Sound BlasterX G6 under WASAPI is
 * refused at every rate, while the same two under WASAPI probe 48/96/192 kHz and stream with no
 * drops. The fixture above is that bench's shape — the MOTU exists under both APIs and the Realtek
 * only under WASAPI, exactly as the G6 does — so these cases are the report itself.
 */
describe('two cards are offered under one host API', () => {
  const wasapi = 'Windows WASAPI'
  const motuWasapi = keyOf(wasapi, 'MOTU M Series')
  const motuAsioKey = keyOf('ASIO', 'MOTU M Series')
  const realtek = keyOf(wasapi, 'Realtek(R) Audio')

  it('still collapses each card to its own best API when nothing is paired with it', () => {
    const cards = buildCards(inputs, outputs, false)
    expect(cards.find((c) => c.interfaceName === 'MOTU M Series')?.hostApi).toBe('ASIO')
  })

  it('offers the MOTU under WASAPI once the other side is a WASAPI-only card', () => {
    const cards = buildCards(inputs, outputs, false, wasapi)

    // The same physical interface, still one entry -- the preference order is overridden, not the
    // one-entry-per-interface rule, so this does not reintroduce the duplication Advanced is for.
    expect(cards.filter((c) => c.interfaceName === 'MOTU M Series')).toHaveLength(1)
    expect(cards.find((c) => c.interfaceName === 'MOTU M Series')?.hostApi).toBe(wasapi)
  })

  it('leaves a card alone where the preferred API cannot carry it', () => {
    const cards = buildCards(inputs, outputs, false, 'ASIO')
    expect(cards.find((c) => c.interfaceName === 'Realtek(R) Audio')?.hostApi).toBe(wasapi)
  })

  describe('matchHostApis', () => {
    it('brings a cross-API pair onto the API they share', () => {
      expect(matchHostApis(realtek, motuAsioKey, inputs, outputs)).toEqual({
        inputKey: realtek,
        outputKey: motuWasapi,
      })
    })

    it('moves both sides rather than making one win', () => {
      // The MOTU in, the Realtek out: it is the MOTU that has to move again, because the Realtek
      // is the side with only one API. Neither side is privileged; the shared API decides.
      expect(matchHostApis(motuAsioKey, realtek, inputs, outputs)).toEqual({
        inputKey: motuWasapi,
        outputKey: realtek,
      })
    })

    it('is idempotent, so the watcher that writes what it watches settles', () => {
      const once = matchHostApis(realtek, motuAsioKey, inputs, outputs)
      expect(matchHostApis(once.inputKey, once.outputKey, inputs, outputs)).toEqual(once)
    })

    it('leaves a matching pair untouched', () => {
      expect(matchHostApis(realtek, motuWasapi, inputs, outputs)).toEqual({
        inputKey: realtek,
        outputKey: motuWasapi,
      })
    })

    it('changes nothing while only one side is chosen', () => {
      expect(matchHostApis('', motuAsioKey, inputs, outputs)).toEqual({
        inputKey: '',
        outputKey: motuAsioKey,
      })
    })

    it('will not move a side onto a host API the picker does not offer', () => {
      // The MOTU and the Realtek share DirectSound in the fixture for inputs only, and
      // DirectSound resamples -- SoundCardEnumerator.Trust calls it so. Repairing a pairing by
      // moving it somewhere whose readings cannot be believed is not a repair.
      const result = matchHostApis(realtek, motuAsioKey, inputs, outputs)
      expect(splitKey(result.outputKey).hostApi).not.toBe('Windows DirectSound')
    })

    it('leaves both alone where they share nothing, so the probe can refuse it in words', () => {
      const lonely = keyOf('ASIO', 'Some other ASIO card')
      const extra = [...outputs, option({ hostApi: 'ASIO', name: 'Some other ASIO card', maxInputChannels: 0, maxOutputChannels: 2 })]
      expect(matchHostApis(realtek, lonely, inputs, extra)).toEqual({
        inputKey: realtek,
        outputKey: lonely,
      })
    })
  })
})
