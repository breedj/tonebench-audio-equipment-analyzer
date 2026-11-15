import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import type { SoundCardConfig, SoundCardOption } from '../api/client'
import { asioSplitProblem } from './asioSplit'
import {
  benchHalfOptions,
  benchPairingProblem,
  cardConfigFor,
  documentsTouched,
  halfFromValue,
  halfValueOf,
  QA40X_HALF,
  SIMULATOR_HALF,
  splitConfigFor,
  type BenchHalf,
  type StagedBench,
} from './benchHalves'
import { buildCards, cardsOffering, keyOf } from './cardList'

/**
 * A bench shaped on the two cards this feature is used with: a MOTU M4 under ASIO, full duplex, and
 * a playback-only Topping DAC under WASAPI. The second one is the case the direction filter exists
 * for — it must never appear in the input dropdown.
 */
function option(
  partial: Partial<SoundCardOption> & Pick<SoundCardOption, 'hostApi' | 'name'>,
): SoundCardOption {
  const kind =
    partial.hostApiKind
    ?? { ASIO: 'Asio', 'Windows WASAPI': 'Wasapi', MME: 'Mme' }[partial.hostApi]
    ?? 'Other'

  const preference =
    partial.preference ?? ({ Asio: 0, Wasapi: 4, Mme: 6 }[kind] ?? 7)

  return {
    interfaceName: partial.name,
    hostApiKind: kind,
    maxInputChannels: 2,
    maxOutputChannels: 0,
    isFullDuplex: false,
    trust: 'Trusted',
    preference,
    hasControlPanel: kind === 'Asio',
    ...partial,
  }
}

const motu = option({
  hostApi: 'ASIO',
  name: 'MOTU M Series',
  maxInputChannels: 8,
  maxOutputChannels: 4,
  isFullDuplex: true,
})

const toppingOut = option({
  hostApi: 'Windows WASAPI',
  name: 'Luidsprekers (TOPPING USB DAC)',
  interfaceName: 'TOPPING USB DAC',
  maxInputChannels: 0,
  maxOutputChannels: 2,
})

const inputs: SoundCardOption[] = [motu]
const outputs: SoundCardOption[] = [motu, toppingOut]
const cards = buildCards(inputs, outputs, false)

const motuKey = keyOf('ASIO', 'MOTU M Series')
const toppingKey = keyOf('Windows WASAPI', 'TOPPING USB DAC')

const qa40x: BenchHalf = { kind: 'Hardware', cardKey: '' }
const simulator: BenchHalf = { kind: 'Simulated', cardKey: '' }
const card = (cardKey: string): BenchHalf => ({ kind: 'SoundCard', cardKey })

describe('benchHalfOptions', () => {
  it('offers the QA40x first and the simulator last, whatever the card list', () => {
    // The ask, and it reads as a scale rather than a list: the dedicated analyzer, whatever
    // hardware the machine has, and the thing that is not hardware at all.
    const values = benchHalfOptions(cards, outputs, true).map((o) => o.value)
    expect(values[0]).toBe('')
    expect(values[1]).toBe(QA40X_HALF)
    expect(values.at(-1)).toBe(SIMULATOR_HALF)

    const empty = benchHalfOptions([], [], true).map((o) => o.value)
    expect(empty).toEqual(['', QA40X_HALF, SIMULATOR_HALF])
  })

  it('leaves out a card that does not carry this direction', () => {
    // Delegated to cardsOffering rather than re-implemented -- the input dropdown offering a
    // playback-only DAC collapses that side into a blank device list with nothing saying why.
    const forInput = benchHalfOptions(cards, inputs, true).map((o) => o.cardKey)
    expect(forInput).toContain(motuKey)
    expect(forInput).not.toContain(toppingKey)

    const forOutput = benchHalfOptions(cards, outputs, true).map((o) => o.cardKey)
    expect(forOutput).toContain(toppingKey)
  })

  it('keeps the card list in its own preference order', () => {
    const offered = benchHalfOptions(cards, outputs, true)
      .filter((o) => o.cardKey)
      .map((o) => o.cardKey)
    expect(offered).toEqual(cardsOffering(cards, outputs).map((c) => c.key))
  })

  it('offers no kinds at all on a plain sound-card bench', () => {
    // Picking a QA40x there would have to re-point the whole session, which is the status bar's
    // Analyzer dropdown's job. The list is then what this picker offered before the merge.
    const options = benchHalfOptions(cards, outputs, false)
    expect(options.map((o) => o.value)).toEqual([
      '',
      ...cardsOffering(cards, outputs).map((c) => c.key),
    ])
    expect(options.every((o) => o.kind === 'SoundCard')).toBe(true)
  })

  it('names a card the way the dropdown reads it', () => {
    const motuOption = benchHalfOptions(cards, inputs, true).find((o) => o.cardKey === motuKey)
    expect(motuOption?.label).toBe('MOTU M Series — ASIO (full duplex)')
  })

  it('never offers Split as a half', () => {
    // A split cannot be half of a split, and the type says so -- asserted as an absence, since
    // there is no value to compare against.
    expect(
      benchHalfOptions(cards, outputs, true).some((o) => (o.kind as string) === 'Split'),
    ).toBe(false)
  })
})

describe('the option value round trip', () => {
  it("is a card's own card key, unchanged", () => {
    // The whole reason the merge is small: form.inputCardKey keeps its type, so cardsOffering,
    // matchHostApis, reselectCardKey and asioSplitProblem are all untouched.
    expect(halfValueOf(card(motuKey))).toBe(motuKey)
    expect(benchHalfOptions(cards, inputs, true).find((o) => o.cardKey === motuKey)?.value).toBe(
      motuKey,
    )
  })

  it('round trips all four shapes', () => {
    for (const half of [qa40x, simulator, card(motuKey), card('')]) {
      expect(halfFromValue(halfValueOf(half))).toEqual(half)
    }
  })

  it('survives a device name holding anything a driver might call it', () => {
    // A prefix scheme such as `card:` would not: PortAudio device names are whatever the driver
    // says, colons and spaces included. The NUL in keyOf's own separator is the one character no
    // DeviceKind name can contain.
    const awkward = keyOf('Host: API (1)', 'Line 1/2: In — "main"')
    expect(halfFromValue(awkward)).toEqual(card(awkward))
  })

  it('reads an unknown value as a card with no card chosen', () => {
    // So a value this build does not understand produces a sentence rather than silently selecting
    // hardware nobody asked for.
    expect(halfFromValue('Nonsense')).toEqual(card(''))
    expect(benchPairingProblem(halfFromValue('Nonsense'), qa40x)).not.toBeNull()
  })
})

describe('benchPairingProblem', () => {
  it('refuses a QA40x on both sides, naming one device opened twice', () => {
    const problem = benchPairingProblem(qa40x, qa40x)
    expect(problem).toContain('one device opened twice')
  })

  it('allows two simulators', () => {
    // The one pairing that exercises the whole composite at a desk with nothing plugged in.
    expect(benchPairingProblem(simulator, simulator)).toBeNull()
  })

  it('allows a card on both sides', () => {
    expect(benchPairingProblem(card(motuKey), card(toppingKey))).toBeNull()
  })

  it('allows a QA40x against a card, either way round', () => {
    expect(benchPairingProblem(qa40x, card(toppingKey))).toBeNull()
    expect(benchPairingProblem(card(motuKey), qa40x)).toBeNull()
  })

  it('delegates the ASIO rule rather than re-wording it', () => {
    // Compared against that function's own output, not a literal, so the two cannot drift on the
    // advice they give -- and the server refuses the same pair at connect.
    const other = keyOf('ASIO', 'Topping ASIO')
    expect(benchPairingProblem(card(motuKey), card(other))).toBe(
      asioSplitProblem(motuKey, other),
    )
    expect(benchPairingProblem(card(motuKey), card(other))).not.toBeNull()
  })

  it('asks for the card, per direction, when a card half has none', () => {
    // Reachable by opening the dialog on a bench whose stored card has left the list -- the one
    // state the dropdowns themselves cannot produce.
    expect(benchPairingProblem(card(''), qa40x)).toContain('captures')
    expect(benchPairingProblem(qa40x, card(''))).toContain('generates')
  })
})

const stored: SoundCardConfig = {
  inputHostApi: 'ASIO',
  inputDeviceName: 'MOTU M Series',
  outputHostApi: 'Windows WASAPI',
  outputDeviceName: 'Luidsprekers (TOPPING USB DAC)',
  inputChannelOffset: 2,
  outputChannelOffset: 0,
  streamStartSettleMs: 2000,
  outputFullScalePeakMv: 1414,
  inputFullScalePeakMvLeft: 2828,
  inputFullScalePeakMvRight: 2830,
  calibratedUtc: '2026-11-10T10:00:00Z',
  outputCalibratedHostApi: 'Windows WASAPI',
  outputCalibratedDeviceName: 'Luidsprekers (TOPPING USB DAC)',
  outputCalibratedChannelOffset: 0,
  inputCalibratedHostApi: 'ASIO',
  inputCalibratedDeviceName: 'MOTU M Series',
  inputCalibratedChannelOffset: 2,
  frontEndGainLeftDb: 0,
  frontEndGainRightDb: 0,
  gainDisplayUnit: 'Db',
  minStimulusFrequencyHz: 20,
  clockDriftPpm: null,
  clockDriftSpreadPpm: null,
  clockDriftSigmaPpm: null,
  clockDriftToneMarginDb: null,
  clockDriftMeasuredUtc: null,
  clockDriftInputDeviceName: '',
  clockDriftOutputDeviceName: '',
}

function staged(partial: Partial<StagedBench> = {}): StagedBench {
  return {
    input: card(motuKey),
    output: card(toppingKey),
    inputKey: keyOf('ASIO', 'MOTU M Series'),
    outputKey: keyOf('Windows WASAPI', 'Luidsprekers (TOPPING USB DAC)'),
    inputChannelOffset: 2,
    outputChannelOffset: 0,
    minStimulusFrequencyHz: 20,
    streamStartSettleMs: 2000,
    ...partial,
  }
}

describe('cardConfigFor', () => {
  it('leaves a direction alone when that half is not a card', () => {
    // The promise SplitDevicePage makes: switching the analyzer picker back to Sound card finds
    // that bench as it was. Choosing the QA40x as the capturing half must not erase the input card
    // underneath it -- the Sound card analyzer opens both directions of this same document.
    const next = cardConfigFor(stored, staged({ input: qa40x, inputKey: '', inputChannelOffset: 0 }))
    expect(next.inputHostApi).toBe('ASIO')
    expect(next.inputDeviceName).toBe('MOTU M Series')
    expect(next.inputChannelOffset).toBe(2)
  })

  it('leaves the other direction alone too', () => {
    const next = cardConfigFor(
      stored,
      staged({ output: simulator, outputKey: '', outputChannelOffset: 4 }),
    )
    expect(next.outputHostApi).toBe('Windows WASAPI')
    expect(next.outputDeviceName).toBe('Luidsprekers (TOPPING USB DAC)')
    expect(next.outputChannelOffset).toBe(0)
  })

  it('writes a direction whose half is a card', () => {
    const next = cardConfigFor(
      stored,
      staged({ inputKey: keyOf('Windows WASAPI', 'In 1-2 (MOTU M Series)'), inputChannelOffset: 4 }),
    )
    expect(next.inputHostApi).toBe('Windows WASAPI')
    expect(next.inputDeviceName).toBe('In 1-2 (MOTU M Series)')
    expect(next.inputChannelOffset).toBe(4)
  })

  it('keeps every calibration figure, which is not this dialog section to change', () => {
    const next = cardConfigFor(stored, staged({ input: qa40x, output: simulator }))
    expect(next.outputFullScalePeakMv).toBe(1414)
    expect(next.inputFullScalePeakMvLeft).toBe(2828)
    expect(next.calibratedUtc).toBe('2026-11-10T10:00:00Z')
  })

  it('always carries the two figures below the dropdowns', () => {
    // Their rows are rendered whenever either half is a card, so they are always staged.
    const next = cardConfigFor(
      stored,
      staged({ minStimulusFrequencyHz: 5, streamStartSettleMs: 3000 }),
    )
    expect(next.minStimulusFrequencyHz).toBe(5)
    expect(next.streamStartSettleMs).toBe(3000)
  })
})

describe('documentsTouched', () => {
  const storedSplit = { inputKind: 'SoundCard' as const, outputKind: 'SoundCard' as const, generatorSampleRateHz: 0 }
  const documents = (s: StagedBench, rate = 0) => ({
    card: cardConfigFor(stored, s),
    split: splitConfigFor(s, rate),
  })

  it('reports neither when nothing moved', () => {
    expect(documentsTouched(documents(staged()), { card: stored, split: storedSplit })).toEqual({
      card: false,
      split: false,
    })
  })

  it('reports the card alone for a channel offset', () => {
    expect(
      documentsTouched(documents(staged({ inputChannelOffset: 4 })), {
        card: stored,
        split: storedSplit,
      }),
    ).toEqual({ card: true, split: false })
  })

  it('reports the split alone for the generator rate', () => {
    expect(
      documentsTouched(documents(staged(), 48000), { card: stored, split: storedSplit }),
    ).toEqual({ card: false, split: true })
  })

  it('reports the split alone for a kind change that takes a card out', () => {
    // The card half is left where it stood -- cardConfigFor's own rule -- so only the pairing moved
    // and only one reopen is paid.
    const next = staged({ input: qa40x, inputKey: '' })
    expect(documentsTouched(documents(next), { card: stored, split: storedSplit })).toEqual({
      card: false,
      split: true,
    })
  })

  it('reports the card alone for a different card on the same half', () => {
    const next = staged({ output: card(motuKey), outputKey: keyOf('ASIO', 'MOTU M Series') })
    expect(documentsTouched(documents(next), { card: stored, split: storedSplit })).toEqual({
      card: true,
      split: false,
    })
  })

  it('reports both when a kind change also brings a card in', () => {
    // The one gesture that legitimately pays two reopens: the pairing moved and so did a card.
    const next = staged({
      input: qa40x,
      inputKey: '',
      output: card(motuKey),
      outputKey: keyOf('ASIO', 'MOTU M Series'),
    })
    expect(documentsTouched(documents(next), { card: stored, split: storedSplit })).toEqual({
      card: true,
      split: true,
    })
  })

  it('reports a document it has not read yet as unmoved', () => {
    // The existing `if (!config.value) return` guard, said in one place: a write built against a
    // document this dialog has never seen would be a write of defaults over whatever is stored.
    expect(documentsTouched(documents(staged()), { card: null, split: null })).toEqual({
      card: false,
      split: false,
    })
  })
})

/**
 * The server judges the same pairing and refuses it with a message, so the two must not drift —
 * a dialog saying one thing while the 400 a user eventually meets says another is the fault this
 * whole mirroring convention exists to prevent.
 *
 * <p>Read as text, the technique `asioSplit.test.ts` uses on `SoundCardSettings.cs` and
 * `UdevRulesTests` uses on the udev file: nothing else notices a message edited in one language
 * and not the other.</p>
 */
describe('the server says the same thing', () => {
  const source = readFileSync(
    fileURLToPath(new URL('../../../src/Tonebench.App/Services/SplitDeviceSetting.cs', import.meta.url)),
    'utf8',
  )

  it('shares the QA40x-on-both-sides refusal word for word', () => {
    // The C# literal is split across two concatenated, indented lines, so the source is flattened
    // the way the compiler would read it before the whole sentence is looked for. Anything less
    // than the whole sentence would let the two drift in the middle of it.
    const flattened = source.replace(/"\s*\+\s*"/g, '').replace(/\s+/g, ' ')
    expect(flattened).toContain(benchPairingProblem(qa40x, qa40x)!.replace(/\s+/g, ' '))
  })

  it('still refuses the pairing this module mirrors', () => {
    expect(source).toContain('input == DeviceKind.Hardware && output == DeviceKind.Hardware')
  })

  it('still leaves the card-level rule to the card document', () => {
    // benchPairingProblem delegates to asioSplitProblem for exactly this reason.
    expect(source).toContain('SoundCardSettings.SplitCombinationProblem')
  })
})
