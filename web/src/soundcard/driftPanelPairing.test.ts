import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * The split dialog's Clock drift panel describes the pairing its dropdowns are <em>staging</em>.
 *
 * It applies on Save, so a panel answered for the session's own bench describes the pairing being
 * replaced: reported 2026-11-13, where a figure measured with the card generating stayed on screen
 * under a "Measured" summary after Generates and Captures had been turned round. The two directions
 * of one pair are different pairings -- a drift figure's sign is a property of which clock is the
 * reference -- so refreshing was not enough; the panel has to ask about the staged pair.
 *
 * It lives in a `.vue`, which is not mountable under this suite's node environment, so this reads the
 * component as text the way `settingsInEffect.test.ts` and `splitNarrowing.test.ts` do.
 */
const source = readFileSync(
  fileURLToPath(new URL('../components/SplitDeviceDialog.vue', import.meta.url)),
  'utf8',
)
  .replace(/^\ufeff/, '')
  .replace(/\r\n/g, '\n')

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
    expect(computedBody('pairingPending')).toContain('storedPairing')

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

/** The body of a computed, up to the next top-level `const`. */
function computedBody(name: string): string {
  const start = source.indexOf(`const ${name} = computed`)
  expect(start, `${name} is declared`).toBeGreaterThan(-1)
  const rest = source.slice(start + 1)
  const end = rest.indexOf('\nconst ')
  return end === -1 ? rest : rest.slice(0, end)
}
