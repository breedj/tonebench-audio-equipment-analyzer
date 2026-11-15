import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import {
  openedSides,
  rateProblem,
  rateRows,
  sharedRateNote,
  type ProbedBench,
} from './probeSides'

/**
 * The probe has to ask about the streams the bench opens.
 *
 * Both faults reported on 2026-11-15 were one defect seen from opposite ends, and both were
 * measured on that bench before anything was changed:
 *
 * - a **playback-only DAC** (Topping D50 III) was never probed at all, because the client gave up
 *   whenever one device name was blank — so no rates came back, nothing could be saved, and every
 *   calibration step was disabled on a bench `SoundCardDevice` opens perfectly well;
 * - a **MOTU under ASIO with that DAC under WASAPI**, chosen as a two-card split, was probed as a
 *   duplex pairing. Asked one at a time the two accept 44.1–192 kHz and 44.1–768 kHz; asked
 *   together they accept **nothing**, which is exactly the one-stream restriction two streams
 *   remove.
 */
const KHZ = '44.1, 48 and 192 kHz'

const side = (name: string, rates: number[], problem: string | null = null) => ({
  name,
  rates,
  problem,
})

describe('openedSides', () => {
  it('pairs the two directions only when one stream carries both', () => {
    expect(openedSides('both', false)).toEqual({ input: true, output: true, oneStream: true })
  })

  it('asks each card on its own when the bench opens one stream per card', () => {
    // The whole point of a card+card split: two streams, two clocks, no shared host API and no
    // shared rate required.
    expect(openedSides('both', true)).toEqual({ input: true, output: true, oneStream: false })
  })

  it('opens one side on a split that uses the card for one direction', () => {
    expect(openedSides('input', false)).toEqual({ input: true, output: false, oneStream: false })
    expect(openedSides('output', false)).toEqual({ input: false, output: true, oneStream: false })
  })

  it('opens nothing where no card is in the path', () => {
    expect(openedSides(null, false)).toEqual({ input: false, output: false, oneStream: false })
  })

  /**
   * The half a split does not use is still *stored* — a bench that plays through the card keeps
   * whatever input was chosen while it was a plain sound card. Probing that as a pairing asks
   * about a stream nothing will ever open, and a refusal there blocks a bench that is fine.
   */
  it('ignores the half a split does not use, however it is set', () => {
    expect(openedSides('output', false).input).toBe(false)
    expect(openedSides('input', true).output).toBe(false)
  })
})

describe('rateRows', () => {
  const pairing = (rates: number[]): ProbedBench => ({
    input: side('', rates),
    output: null,
    oneStream: true,
  })

  it('prints one row for a pairing', () => {
    expect(rateRows(pairing([48000, 96000]))).toEqual([
      { label: 'Sample rates', rates: [48000, 96000] },
    ])
  })

  it('prints a row per card on a two-card bench, because the two are not intersected', () => {
    // SplitAnalyzerDevice.Capabilities says so in its own remarks: the analysis rate is the
    // capture card's and the generator keeps a declared list of its own. One row would have to
    // invent a shared list the bench does not require.
    expect(
      rateRows({
        input: side('MOTU M Series', [48000, 96000, 192000]),
        output: side('Luidsprekers (TOPPING USB DAC)', [48000, 768000]),
        oneStream: false,
      }),
    ).toEqual([
      { label: 'Input rates', rates: [48000, 96000, 192000] },
      { label: 'Output rates', rates: [48000, 768000] },
    ])
  })

  it('calls a lone side the sample rates, there being nothing to tell it from', () => {
    expect(
      rateRows({ input: null, output: side('Topping', [48000]), oneStream: false }),
    ).toEqual([{ label: 'Sample rates', rates: [48000] }])
  })

  it('leaves a refused side to the refusal rather than printing an empty row', () => {
    expect(
      rateRows({ input: side('A', []), output: side('B', [48000]), oneStream: false }),
    ).toEqual([{ label: 'Output rates', rates: [48000] }])
  })

  it('prints nothing at all where the pairing was refused', () => {
    expect(rateRows(pairing([]))).toEqual([])
  })
})

describe('rateProblem', () => {
  it('says nothing while every side this bench opens accepted a rate', () => {
    expect(
      rateProblem(
        { input: side('A', [48000]), output: side('B', [96000]), oneStream: false },
        KHZ,
      ),
    ).toBeNull()
  })

  it('blames the pairing only where a pairing was asked about', () => {
    expect(
      rateProblem({ input: side('', []), output: null, oneStream: true }, KHZ),
    ).toContain('This pairing accepts none of')
  })

  /**
   * The message a two-card split used to get, and it blamed a pairing the bench never opens —
   * which sent a reader looking for a combination to change when the fault is one card.
   */
  it('names the card when the cards were asked one at a time', () => {
    const message = rateProblem(
      { input: side('MOTU M Series', []), output: side('Topping', [48000]), oneStream: false },
      KHZ,
    )
    expect(message).toContain('MOTU M Series')
    expect(message).not.toContain('pairing')
    expect(message).toContain('accepts none of')
  })

  it('names both where neither opened, and agrees with itself about number', () => {
    const message = rateProblem(
      { input: side('A', []), output: side('B', []), oneStream: false },
      KHZ,
    )
    expect(message).toContain('‘A’ and ‘B’')
    expect(message).toContain('accept none of')
    expect(message).not.toContain('accepts none of')
  })

  it('prefers what the server said over a list of rates', () => {
    // The server distinguishes "refused outright" from "does not do these rates", and its own
    // message is the one that can say which -- see SoundCardCatalog.Probe.
    expect(
      rateProblem(
        { input: side('A', [], "'A' is not there."), output: null, oneStream: false },
        KHZ,
      ),
    ).toBe("'A' is not there.")
  })

  it('says nothing about a side this bench does not open', () => {
    expect(
      rateProblem({ input: null, output: side('B', [48000]), oneStream: false }, KHZ),
    ).toBeNull()
  })
})

describe('sharedRateNote', () => {
  it('warns where two cards have no rate between them', () => {
    const note = sharedRateNote({
      input: side('A', [44100]),
      output: side('B', [48000]),
      oneStream: false,
    })
    expect(note).toContain('no rate in common')
    expect(note).toContain('split settings')
  })

  it('says nothing where they share one', () => {
    expect(
      sharedRateNote({
        input: side('A', [44100, 48000]),
        output: side('B', [48000, 96000]),
        oneStream: false,
      }),
    ).toBeNull()
  })

  it('says nothing about a pairing, which cannot reach this state', () => {
    expect(sharedRateNote({ input: side('', [48000]), output: null, oneStream: true })).toBeNull()
  })

  it('leaves a refused side to the refusal', () => {
    expect(
      sharedRateNote({ input: side('A', []), output: side('B', [48000]), oneStream: false }),
    ).toBeNull()
  })
})

/**
 * The client half, read as text because it lives in a `.vue` and in a composable whose refs this
 * suite cannot drive — the technique `splitNarrowing.test.ts` and `settingsInEffect.test.ts` use.
 */
describe('the dialog asks about what it will open', () => {
  const composable = readFileSync(
    fileURLToPath(new URL('../composables/useSoundCard.ts', import.meta.url)),
    'utf8',
  ).replace(/\r\n/g, '\n')

  const dialog = readFileSync(
    fileURLToPath(new URL('../components/DeviceSettingsDialog.vue', import.meta.url)),
    'utf8',
  )
    .replace(/^﻿/, '')
    .replace(/\r\n/g, '\n')

  it('no longer gives up when one direction is blank', () => {
    // The line this replaced -- `if (!next.inputDeviceName || !next.outputDeviceName) return` --
    // is why a DAC could not be saved at all.
    expect(composable).not.toContain('if (!next.inputDeviceName || !next.outputDeviceName)')
    expect(composable).toContain('const wantsInput = opens.input && !!next.inputDeviceName')
    expect(composable).toContain('const wantsOutput = opens.output && !!next.outputDeviceName')
  })

  it('asks for a pairing only when one stream carries both', () => {
    expect(composable).toContain('if (wantsInput && wantsOutput && opens.oneStream)')
  })

  it('passes the bench it is describing, rather than the default pairing', () => {
    expect(dialog).toContain('await probePairing(draft, opens.value)')
  })

  it('refuses per side, so one direction is enough', () => {
    const body = dialog.slice(dialog.indexOf('const canSave = computed'))
    expect(body).toContain('if (usesInput && inputRates.value.length === 0) return false')
    expect(body).toContain('if (usesOutput && outputRates.value.length === 0) return false')
  })
})
