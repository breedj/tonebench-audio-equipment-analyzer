import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

/**
 * The Clock drift panel shows the *stored* figure, and the drift test writes that figure itself.
 *
 * `ClockDriftRunner.RememberAsync` stores the result the moment a run ends — there is no Keep
 * button, deliberately — so the server holds a newer configuration than the one the Sound card
 * dialog loaded when it opened. Unless closing the drift dialog re-reads it, the panel behind goes
 * on showing the previous run: the old ppm, the old timestamp, and dashes for the two figures the
 * new run actually measured. Reported from the bench 2026-11-12, against a run reading 83.18 ppm
 * over a panel still saying 84.59.
 *
 * It is asserted by reading the component as text because the fault lives in a `.vue` template and
 * this suite runs on `environment: 'node'` with no DOM — the same technique as
 * `annotationNumbersDefault.test.ts`. What it pins is narrow and exact: the handler exists, it
 * reloads, and the template routes `@close` through it rather than closing inline. Checked by
 * putting the bare assignment back, which fails the third case.
 */
const source = readFileSync(
  new URL('../components/SoundCardDialog.vue', import.meta.url),
  'utf8',
)

describe('the Clock drift panel is refreshed after a run', () => {
  it('has a close handler for the drift dialog', () => {
    expect(source).toContain('async function onClockDriftClosed()')
  })

  it('that handler re-reads the stored configuration', () => {
    const body = source.slice(
      source.indexOf('async function onClockDriftClosed()'),
      source.indexOf('async function onClockDriftClosed()') + 200,
    )
    expect(body).toContain('clockDriftOpen.value = false')
    expect(body).toContain('load(true)')
  })

  it('and the drift dialog closes through it rather than inline', () => {
    expect(source).toContain('@close="onClockDriftClosed"')
    expect(source).not.toContain('@close="clockDriftOpen = false"')
  })
})
