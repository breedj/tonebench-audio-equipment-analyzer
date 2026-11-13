import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * A device dialog's Clock drift panel describes the pairing <em>it</em> shows, not the bench the
 * session has open.
 *
 * Reported 2026-11-13 against the split dialog: a figure measured with the card generating stayed on
 * screen, under a "Measured" summary, after Generates and Captures had been turned round. Re-fetching
 * could not fix it — the endpoint resolved the pairing from the *saved* document, and that dialog
 * applies its dropdowns on Save — so the pairing is passed as an argument and resolved server-side,
 * which keeps `BenchClockKey` the one thing that decides what a pairing is keyed by.
 *
 * The two directions of one pair are genuinely two pairings: a drift figure's sign is a property of
 * which clock is the reference, so a reading taken one way round is correctly not offered the other.
 *
 * Both dialogs live in `.vue` files, which are not mountable under this suite's node environment, so
 * this reads them as text the way `settingsInEffect.test.ts` and `splitNarrowing.test.ts` do.
 */
const read = (name: string) =>
  readFileSync(fileURLToPath(new URL(`../components/${name}`, import.meta.url)), 'utf8')
    .replace(/^﻿/, '')
    .replace(/\r\n/g, '\n')

const source = read('SplitDeviceDialog.vue')
const cardSource = read('SoundCardDialog.vue')

describe('the drift panel follows the dropdowns', () => {
  it('asks about the staged pairing rather than the bench in use', () => {
    const call = source.slice(source.indexOf('api.getBenchClockDrift('))
    expect(call.slice(0, 160)).toContain('generates: form.outputKind')
    expect(call.slice(0, 160)).toContain('captures: form.inputKind')
  })

  it('re-reads it when either dropdown changes', () => {
    // The whole fix: without this the panel is only ever read when the dialog opens.
    const at = source.indexOf('() => [form.outputKind, form.inputKind]')
    expect(at, 'a watcher on the two halves').toBeGreaterThan(-1)
    expect(source.slice(at, at + 220)).toContain('loadBenchDrift()')
  })

  it('does not re-read it for a generator-rate change', () => {
    // A clock difference is a property of two crystals; the rate one of them runs at does not
    // change which two, and re-reading on it would spend a request per keystroke.
    const at = source.indexOf('() => [form.outputKind, form.inputKind]')
    expect(source.slice(at, at + 220)).not.toContain('generatorSampleRateHz')
  })

  /**
   * And the run is held until Save, because it cannot follow: the drift test measures the bench the
   * session has open. Letting it run against a staged pairing would store a figure under one pairing
   * while the panel showed it for another -- the fault above, made durable.
   */
  it('holds the Measure button while the staged pairing is unsaved', () => {
    expect(computedBody(source, 'pairingPending')).toContain('storedPairing')

    const at = source.indexOf('clockDriftOpen = true')
    const button = source.slice(source.lastIndexOf('<button', at), source.indexOf('>', at))
    expect(button).toContain(':disabled="pairingPending"')

    // And says why, rather than presenting a dead button.
    expect(source).toContain('press Save before measuring it')
  })

  it('remembers what is saved, so pending is a comparison and not a guess', () => {
    const load = source.slice(source.indexOf('const stored = await api.getSplitDeviceConfig()'))
    expect(load.slice(0, 400)).toContain('storedPairing.value = {')
  })
})

/**
 * The same rule in the sound-card dialog, where the pairing is the two <em>cards</em> selected.
 *
 * That dialog applies its selection 250 ms after it changes rather than behind a button, so the form
 * and the server normally agree — but not while the write is in flight, and not at all when it
 * cannot be applied (no common sample rate), where the form says one pairing and the server holds
 * another. Answered for the session's bench the panel then quotes a figure measured between two
 * other devices under a heading that reads as this one's.
 */
describe('the card dialog follows its selection', () => {
  const loadFn = cardSource.slice(
    cardSource.indexOf('async function loadBenchDrift'),
    cardSource.indexOf('async function loadBenchDrift') + 900,
  )

  it('asks about the two cards the form names', () => {
    expect(loadFn).toContain('api.getBenchClockDrift(')
    for (const field of ['inputHostApi', 'inputDevice', 'outputHostApi', 'outputDevice']) {
      expect(loadFn, field).toContain(field)
    }

    // Both host APIs, not the device names alone: one card appears under several, and a pairing is
    // keyed by host API as well as by the interface.
    expect(loadFn).toContain('next.inputHostApi')
    expect(loadFn).toContain('next.outputHostApi')
  })

  it('falls back to the bench in use before a config has loaded', () => {
    // toConfig() is null until the stored document arrives, and a query of four empty strings is a
    // pairing between nothing and nothing rather than "whatever the app has open".
    expect(loadFn).toMatch(/next === null\s*\?\s*undefined/)
  })

  it('re-reads it when the cards change', () => {
    const at = cardSource.indexOf("[form.cardKey, form.inputKey, form.outputKey].join('|')")
    expect(at, 'a watcher over the three card fields').toBeGreaterThan(-1)
    expect(cardSource.slice(at, at + 260)).toContain('loadBenchDrift()')
  })

  it('does not re-read it for a channel-pair change', () => {
    // BenchClockKey keys a card by its interface stem rather than its endpoint, precisely so a
    // figure survives a channel-pair change. Re-reading on one would ask the same question again,
    // and listing it here would assert the opposite.
    const at = cardSource.indexOf("[form.cardKey, form.inputKey, form.outputKey].join('|')")
    expect(cardSource.slice(at, at + 260)).not.toContain('ChannelOffset')
  })

  /** The run is held the same way, and this dialog already had that half. */
  it('holds its Measure button while the settings are not in effect', () => {
    const at = cardSource.indexOf('clockDriftOpen = true')
    const button = cardSource.slice(cardSource.lastIndexOf('<button', at), cardSource.indexOf('>', at))
    expect(button).toContain(':disabled="!settingsInEffect"')
  })
})

/** The body of a computed, up to the next top-level `const`. */
function computedBody(text: string, name: string): string {
  const start = text.indexOf(`const ${name} = computed`)
  expect(start, `${name} is declared`).toBeGreaterThan(-1)
  const rest = text.slice(start + 1)
  const end = rest.indexOf('\nconst ')
  return end === -1 ? rest : rest.slice(0, end)
}
