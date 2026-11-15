import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * The Clock drift panel describes the pairing the dialog <em>shows</em>, not the bench the session
 * has open.
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
 * The panel became its own component when the two device dialogs were merged, and the rule outlived
 * the move. What is asserted here is split the way the code is: the panel is handed a pairing and
 * must ask about *that*, and its host must say when the staged pairing is not yet the one in use.
 *
 * Both files are `.vue`, which are not mountable under this suite's node environment, so this reads
 * them as text the way `settingsInEffect.test.ts` and `splitNarrowing.test.ts` do.
 */
const read = (name: string) =>
  readFileSync(fileURLToPath(new URL(`../components/${name}`, import.meta.url)), 'utf8')
    .replace(/^﻿/, '')
    .replace(/\r\n/g, '\n')

const panel = read('BenchClockDriftPanel.vue')
const host = read('DeviceSettingsDialog.vue')

describe('the drift panel follows the pairing it is given', () => {
  it('asks about the pairing it was handed rather than the bench in use', () => {
    const call = panel.slice(panel.indexOf('api.getBenchClockDrift('))
    expect(call.slice(0, 200)).toContain('generates: props.generates')
    expect(call.slice(0, 200)).toContain('captures: props.captures')
  })

  it('re-reads it when either half changes', () => {
    // The whole fix: without this the panel is only ever read when the dialog opens.
    const at = panel.indexOf('() => [props.generates, props.captures')
    expect(at, 'a watcher on the two halves').toBeGreaterThan(-1)
    expect(panel.slice(at, at + 240)).toContain('loadBenchDrift()')
  })

  it('does not re-read it for a generator-rate change', () => {
    // A clock difference is a property of two crystals; the rate one of them runs at does not
    // change which two, and re-reading on it would spend a request per keystroke.
    const at = panel.indexOf('() => [props.generates, props.captures')
    expect(panel.slice(at, at + 240)).not.toContain('generatorSampleRateHz')
  })

  /**
   * And it re-reads on a *card* change too, which the panel could not do while the cards were
   * chosen in a different dialog. On a bench with a card on each half the pairing is keyed per
   * card, so turning the input card round is a different pairing with a different stored figure.
   */
  it('re-reads it when either card changes', () => {
    const at = panel.indexOf('() => [props.generates, props.captures')
    expect(panel.slice(at, at + 240)).toContain('props.inputDevice')
    expect(panel.slice(at, at + 240)).toContain('props.outputDevice')
  })

  it('names a card only for a half that is a card', () => {
    // `toQuery` sends an empty string as an empty string, and the endpoint reads "neither given"
    // as "use the stored document" -- so a blank name would key that half's clock to a card with
    // no name rather than falling back.
    const call = panel.slice(
      panel.indexOf('api.getBenchClockDrift('),
      panel.indexOf('} catch'),
    )
    expect(call).toContain("props.captures === 'SoundCard' && props.inputDevice")
    expect(call).toContain("props.generates === 'SoundCard' && props.outputDevice")
  })

  /**
   * And the run is held while the staged bench is not the one in use, because it cannot follow: the
   * drift test measures the bench the session has open. Letting it run against a staged pairing
   * would store a figure under one pairing while the panel showed it for another -- the fault
   * above, made durable.
   */
  it('holds its Measure button on the pending flag its host sets', () => {
    const at = panel.indexOf('clockDriftOpen = true')
    const button = panel.slice(panel.lastIndexOf('<button', at), panel.indexOf('>', at))
    expect(button).toContain(':disabled="props.pending"')

    // And says why, rather than presenting a dead button.
    expect(button).toContain('props.pendingReason')
  })
})

/**
 * The host's half of it, which the merge changed in kind rather than in meaning. The old split
 * dialog applied on Save, so the run was held until a press; this one applies as you pick, so what
 * holds the run is the quarter-second the two writes take to land. Both documents count: the
 * pairing decides which two clocks there are, and the cards decide which card each half is.
 */
describe('the host says when the staged bench is not the one in use', () => {
  it('compares both documents against what is saved, so pending is not a guess', () => {
    // The pairing's own half, against the split document the dialog loaded.
    expect(computedBody(host, 'splitInEffect')).toContain('storedSplit.value')

    // And the card document's, which is settingsInEffect's job -- so a card changed but not yet
    // applied holds the run too, which it must: the figure is keyed per card.
    const bench = computedBody(host, 'benchInEffect')
    expect(bench).toContain('settingsInEffect.value')
    expect(bench).toContain('splitInEffect.value')
  })

  it('reads the split document so there is something to compare against', () => {
    const load = host.slice(host.indexOf('async function loadSplit()'))
    expect(load.slice(0, 400)).toContain('storedSplit.value = await api.getSplitDeviceConfig()')
  })

  it('hands that to the panel with a reason a reader can act on', () => {
    const at = host.indexOf('<BenchClockDriftPanel')
    expect(at, 'the panel is rendered').toBeGreaterThan(-1)
    const tag = host.slice(at, host.indexOf('/>', at))
    expect(tag).toContain(':pending="!benchInEffect"')
    expect(tag).toContain('pending-reason=')
    expect(tag).toContain('still being applied')
  })

  it('hands it the cards as well as the kinds, which is the pairing it is keyed by', () => {
    const at = host.indexOf('<BenchClockDriftPanel')
    const tag = host.slice(at, host.indexOf('/>', at))
    expect(tag).toContain(':generates="form.outputKind"')
    expect(tag).toContain(':captures="form.inputKind"')
    expect(tag).toContain('input-host-api')
    expect(tag).toContain('output-host-api')
  })
})

// NOTE: the same rule was asserted here for the sound-card dialog's own Clock drift panel
// until 2026-11-15. That panel is gone: drift is a property of a bench with two clocks, and
// after that day's narrowing a sound card is one interface -- so every bench with a drift
// figure to measure is a split.

/** The body of a computed, up to the next top-level `const`. */
function computedBody(text: string, name: string): string {
  const start = text.indexOf(`const ${name} = computed`)
  expect(start, `${name} is declared`).toBeGreaterThan(-1)
  const rest = text.slice(start + 1)
  const end = rest.indexOf('\nconst ')
  return end === -1 ? rest : rest.slice(0, end)
}
