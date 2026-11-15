import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import type { SoundCardOption } from '../api/client'
import { buildCards, cardsOffering, keyOf } from './cardList'
import { asioSplitProblem, isAsioHostApi, withAsioAlternatives } from './asioSplit'

/**
 * Shaped on the bench that reported this, read from `GET /api/soundcard/devices` on 2026-11-15: a
 * MOTU M4 and a TOPPING D50 III, each with an ASIO driver, as the two halves of a split.
 *
 * <p>The two cards reach the picker differently and that asymmetry is the whole of the fault. The
 * MOTU's ASIO and WASAPI devices share an interface stem — `MOTU M Series` against `In 1-2 (MOTU M
 * Series)` — so `collapseToBestApi` folds them into one ASIO entry. The Topping's do not
 * (`Topping USB Audio Device` against `Luidsprekers (TOPPING USB DAC)`), so both of its entries are
 * already in the list. One card could be moved off ASIO and the other could not, which is why it
 * read as a card refusing to appear rather than as a rule.</p>
 */
function option(
  partial: Partial<SoundCardOption> & Pick<SoundCardOption, 'hostApi' | 'name'>,
): SoundCardOption {
  const kind =
    partial.hostApiKind ??
    {
      ASIO: 'Asio',
      'Windows WASAPI': 'Wasapi',
      'Windows WDM-KS': 'WdmKs',
      'Windows DirectSound': 'DirectSound',
      MME: 'Mme',
    }[partial.hostApi] ??
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
    trust: kind === 'DirectSound' || kind === 'Mme' ? 'Resamples' : 'Trusted',
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
  option({ hostApi: 'Windows WASAPI', name: 'In 3-4 (MOTU M Series)', interfaceName: 'MOTU M Series' }),
]

const outputs: SoundCardOption[] = [
  { ...motuAsio },
  option({
    hostApi: 'ASIO',
    name: 'Topping USB Audio Device',
    maxInputChannels: 0,
    maxOutputChannels: 2,
  }),
  option({
    hostApi: 'Windows WASAPI',
    name: 'Luidsprekers (TOPPING USB DAC)',
    interfaceName: 'TOPPING USB DAC',
    maxInputChannels: 0,
    maxOutputChannels: 2,
  }),
  option({
    hostApi: 'Windows WASAPI',
    name: 'Out 1-2 (MOTU M Series)',
    interfaceName: 'MOTU M Series',
    maxInputChannels: 0,
    maxOutputChannels: 2,
  }),
]

const defaultList = () => buildCards(inputs, outputs, false)
const labels = (cards: readonly { interfaceName: string; hostApi: string }[]) =>
  cards.map((c) => `${c.interfaceName} — ${c.hostApi}`)

describe('withAsioAlternatives', () => {
  it('offers the card that was collapsed away — which is the report', () => {
    // Before: the input dropdown's only entry was the MOTU under ASIO, and the output's only ASIO
    // entry was the Topping. Both cards on ASIO is the one pairing a split cannot open, and the
    // way out was Advanced and thirty further entries.
    expect(labels(cardsOffering(defaultList(), inputs))).toEqual(['MOTU M Series — ASIO'])

    const offered = withAsioAlternatives(defaultList(), inputs, outputs)
    expect(labels(cardsOffering(offered, inputs))).toEqual([
      'MOTU M Series — ASIO',
      'MOTU M Series — Windows WASAPI',
    ])
  })

  it('leaves the output side able to keep ASIO, which is what makes the pair legal', () => {
    const offered = labels(
      cardsOffering(withAsioAlternatives(defaultList(), inputs, outputs), outputs),
    )
    expect(offered).toContain('Topping USB Audio Device — ASIO')
    expect(offered).toContain('MOTU M Series — Windows WASAPI')
  })

  it('adds nothing for an ASIO card whose other entries are a different interface', () => {
    // The Topping's WASAPI endpoint names `TOPPING USB DAC`, which is its own card in this picker
    // and is in the list already. Nothing to add, and nothing to duplicate.
    const offered = withAsioAlternatives(defaultList(), inputs, outputs)
    expect(offered.filter((c) => c.interfaceName === 'Topping USB Audio Device')).toHaveLength(1)
    expect(offered.filter((c) => c.interfaceName === 'TOPPING USB DAC')).toHaveLength(1)
  })

  it('takes the best non-ASIO entry, not every one', () => {
    const withDirectSound = [
      ...inputs,
      option({
        hostApi: 'Windows DirectSound',
        name: 'In 1-2 (MOTU M Series)',
        interfaceName: 'MOTU M Series',
      }),
    ]
    const offered = withAsioAlternatives(
      buildCards(withDirectSound, outputs, false),
      withDirectSound,
      outputs,
    )
    expect(
      offered.filter((c) => c.interfaceName === 'MOTU M Series').map((c) => c.hostApi),
    ).toEqual(['ASIO', 'Windows WASAPI'])
  })

  it('does not offer an alternative the default list hides', () => {
    // A card whose only non-ASIO entry resamples has no honest alternative: offering it would put
    // a host API in the picker that the filter exists to keep out. Advanced is the way to one.
    const only = [
      option({ hostApi: 'ASIO', name: 'Some Interface', maxOutputChannels: 2, isFullDuplex: true }),
      option({
        hostApi: 'MME',
        name: 'Some Interface In (Some Interface)',
        interfaceName: 'Some Interface',
      }),
    ]
    const offered = withAsioAlternatives(buildCards(only, only, false), only, only)
    expect(offered.map((c) => c.hostApi)).toEqual(['ASIO'])
  })

  it('is idempotent, so a recompute cannot grow the list', () => {
    const once = withAsioAlternatives(defaultList(), inputs, outputs)
    const twice = withAsioAlternatives(once, inputs, outputs)
    expect(twice.map((c) => c.key)).toEqual(once.map((c) => c.key))
  })

  it('adds nothing under Advanced, where every entry is in the list already', () => {
    const everything = buildCards(inputs, outputs, true)
    expect(withAsioAlternatives(everything, inputs, outputs, true).map((c) => c.key)).toEqual(
      everything.map((c) => c.key),
    )
  })

  it('leaves a bench with no ASIO card exactly as it was', () => {
    const wasapiIn = inputs.filter((o) => o.hostApi !== 'ASIO')
    const wasapiOut = outputs.filter((o) => o.hostApi !== 'ASIO')
    const list = buildCards(wasapiIn, wasapiOut, false)
    expect(withAsioAlternatives(list, wasapiIn, wasapiOut).map((c) => c.key)).toEqual(
      list.map((c) => c.key),
    )
  })

  it('keeps the list in its own order — the drivers that reach the hardware first', () => {
    const preferences = withAsioAlternatives(defaultList(), inputs, outputs).map(
      (c) => c.preference,
    )
    expect([...preferences].sort((a, b) => a - b)).toEqual(preferences)
  })
})

describe('asioSplitProblem', () => {
  const asio = (name: string) => keyOf('ASIO', name)
  const wasapi = (name: string) => keyOf('Windows WASAPI', name)

  it('refuses two ASIO cards and says which one to move', () => {
    const problem = asioSplitProblem(asio('MOTU M Series'), asio('Topping USB Audio Device'))
    expect(problem).toContain('MOTU M Series')
    expect(problem).toContain('Topping USB Audio Device')
    expect(problem).toContain('Move one of them to WASAPI')
  })

  it('names one ASIO card used both ways as a sound card rather than a split', () => {
    const problem = asioSplitProblem(asio('MOTU M Series'), asio('MOTU M Series'))
    expect(problem).toContain('is one interface')
    expect(problem).toContain('choose Sound card')
  })

  it('allows one ASIO card against a card on another host API, which is the whole point', () => {
    expect(asioSplitProblem(wasapi('MOTU M Series'), asio('Topping USB Audio Device'))).toBeNull()
    expect(asioSplitProblem(asio('MOTU M Series'), wasapi('TOPPING USB DAC'))).toBeNull()
    expect(asioSplitProblem(wasapi('MOTU M Series'), wasapi('TOPPING USB DAC'))).toBeNull()
  })

  it('says nothing about a half that has not been chosen', () => {
    expect(asioSplitProblem('', asio('Topping USB Audio Device'))).toBeNull()
    expect(asioSplitProblem(asio('MOTU M Series'), '')).toBeNull()
    expect(asioSplitProblem('', '')).toBeNull()
  })

  it('reads ASIO out of the key however it is cased', () => {
    expect(isAsioHostApi('ASIO')).toBe(true)
    expect(isAsioHostApi('asio')).toBe(true)
    expect(isAsioHostApi('Windows WASAPI')).toBe(false)
  })
})

/**
 * The dialog has to ask for both halves of the rule, and neither is visible from the module.
 *
 * <p>Read as text because it lives in a `.vue`, which is not mountable under this suite's node
 * environment — the technique `hostApiLoosening.test.ts` and `settingsInEffect.test.ts` use.</p>
 */
describe('the card dialog', () => {
  const source = readFileSync(
    fileURLToPath(new URL('../components/SoundCardDialog.vue', import.meta.url)),
    'utf8',
  )

  it('offers the alternatives on a two-card bench and nowhere else', () => {
    // One card is one stream, where ASIO is simply the better driver and its twin is a worse
    // duplicate of a card already in the list.
    const at = source.indexOf('function offerAsioAlternatives')
    expect(at, 'the gate exists').toBeGreaterThan(-1)
    expect(source.slice(at, at + 400)).toContain('if (!twoCardBench.value) return')
  })

  it('puts them through the list both dropdowns are built from', () => {
    // Via cardsFor, so the input and the output side cannot disagree about what is offered.
    expect(source).toContain('cardsOffering(offerAsioAlternatives(list), devices)')
  })

  it('says why a pair of ASIO cards cannot be opened, above the rates that look fine', () => {
    const refusal = source.indexOf('{{ asioRefusal }}')
    const rates = source.indexOf('{{ rateRefusal }}')
    expect(refusal, 'the refusal is rendered').toBeGreaterThan(-1)
    expect(refusal).toBeLessThan(rates)
  })
})

/**
 * The server judges the same pair and refuses it at connect, so the two must not drift apart on the
 * advice they give — a dialog telling a user to move one card to WASAPI while the refusal they
 * eventually meet says something else is the fault this whole rule exists to prevent.
 *
 * <p>Read as text, the technique `UdevRulesTests` uses on the udev file and
 * `EveryPanelsGeneratorModeLabel_IsOneTheFloorMessageKnows` uses on the panels: nothing else
 * notices a message edited in one language and not the other.</p>
 */
describe('the server says the same thing', () => {
  const settings = readFileSync(
    fileURLToPath(
      new URL('../../../src/Tonebench.Device/SoundCard/SoundCardSettings.cs', import.meta.url),
    ),
    'utf8',
  )

  it('shares the refusal and the fix for two ASIO cards', () => {
    expect(settings).toContain(
      'A split cannot have two ASIO cards: ASIO allows one driver open per process',
    )
    expect(settings).toContain('Move one of them to WASAPI')
  })

  it('shares the one-interface answer', () => {
    expect(settings).toContain('is one interface, so it is a sound card rather than a split')
    expect(settings).toContain('opens both of its directions in one stream')
  })
})
