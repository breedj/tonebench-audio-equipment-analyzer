import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * A WASAPI render loopback endpoint, and why the dialog has to say it dropped one.
 *
 * Windows offers one beside every playback device so an app can capture what is being played to it,
 * and PortAudio lists it with input channels and no output channels -- which is an ADC's shape, so
 * the picker offered a TOPPING D50 III, a DAC, as an input card (reported 2026-11-13). The server
 * now keeps them out of the input list and refuses to open one, so the only thing left for the
 * client is to explain a stored selection that has just disappeared.
 *
 * The ordering is the whole of that: `reconcileWithDevices` clears a stored device that is no longer
 * in the list and autosaves, so anything reading the config afterwards sees an empty string and the
 * selection has simply vanished. Measured in the running app before the fix -- the dropdown read
 * "--" with nothing anywhere saying why.
 */
// Normalised: this component is CRLF with a BOM where most of `web/src` is LF. See
// settingsInEffect.test.ts for what a pattern written with "\n" costs here.
const source = readFileSync(
  fileURLToPath(new URL('../components/DeviceSettingsDialog.vue', import.meta.url)),
  'utf8',
)
  .replace(/^\ufeff/, '')
  .replace(/\r\n/g, '\n')

describe('a stored render loopback input', () => {
  it('is captured before the reconcile that drops it', () => {
    // Scoped to the open watcher: `refreshDevices` reconciles too and sits earlier in the file, so
    // an unscoped indexOf compares against the wrong call and fails on correct code.
    const body = source.slice(source.indexOf('() => props.open,'))
    expect(body, 'the open watcher is found').not.toBe('')

    const capture = body.indexOf('storedLoopbackInput.value =')
    const reconcile = body.indexOf('await reconcileWithDevices()')

    expect(capture, 'the capture exists').toBeGreaterThan(-1)
    expect(reconcile, 'the reconcile exists').toBeGreaterThan(-1)
    expect(capture).toBeLessThan(reconcile)
  })

  it('reads the suffix PortAudio appends, case-insensitively', () => {
    expect(source).toContain("const RENDER_LOOPBACK_SUFFIX = ' [loopback]'")
    expect(source).toMatch(/toLowerCase\(\)\.endsWith\(RENDER_LOOPBACK_SUFFIX\)/)
  })

  it('names the device and says what it is', () => {
    const at = source.indexOf('v-if="storedLoopbackInput"')
    expect(at).toBeGreaterThan(-1)
    const block = source.slice(at, source.indexOf('</p>', at))
    expect(block).toContain('{{ storedLoopbackInput }}')
    expect(block).toContain('not a recording device')
    // And that it is gone from the list, which is the question an empty dropdown raises.
    expect(block).toContain('no longer offered')
  })
})
