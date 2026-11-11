import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * The calibration steps must not run while the dialog's own settings have not reached a card.
 *
 * Reported 2026-11-11: a Sound BlasterX G6 on WASAPI paired with a MOTU M4 on ASIO accepts no
 * common sample rate, so `canSave` is false and `applyDevice` returns without a word -- which
 * silently drops *every* field, Output channels among them. Generate sine then played on the
 * previously connected card at offset 0, i.e. channels 1/2, while the dialog read 3/4. The tone
 * went somewhere the user was not measuring and nothing on screen said so.
 *
 * All of it lives in a `.vue`, which is not mountable under this suite's node environment, so this
 * reads the component as text the way `annotationNumbersDefault.test.ts` and `panelIcons.test.ts`
 * do. That catches the one change that would put the fault back: relaxing a disabled binding.
 */
// Normalised, and not as a tidiness: this file is CRLF with a BOM where most of `web/src` is LF,
// so a pattern written with "\n" matches nothing here -- and `indexOf` returning -1 then feeds
// `slice(0, -1)`, which is almost the whole file and contains whatever was being looked for. The
// test passes and guards nothing. That is the same line-ending trap `numberField.test.ts` records.
const source = readFileSync(
  fileURLToPath(new URL('../components/SoundCardDialog.vue', import.meta.url)),
  'utf8',
)
  .replace(/^﻿/, '')
  .replace(/\r\n/g, '\n')

describe('the calibration steps are gated on the settings being in effect', () => {
  it('derives settingsInEffect from the same comparison applyDevice makes', () => {
    const block = source.slice(source.indexOf('const settingsInEffect'))
    expect(block).toContain('canSave')
    // The whole config, not the device fields alone -- applyDevice's own rule, and the reason a
    // change to one of the numeric settings used to be dropped.
    expect(block).toMatch(/JSON\.stringify\(next\)\s*===\s*JSON\.stringify\(config\.value\)/)
  })

  it('turns the tone button off while they are not', () => {
    const button = buttonBinding('toggleTone')
    expect(button).toContain('settingsInEffect')
  })

  it('still lets a playing tone be stopped', () => {
    // The one subtlety: a tone that started while the settings WERE in effect must stay
    // stoppable after they stop being so, or the generator is left playing with no way to
    // silence it but closing the dialog.
    expect(buttonBinding('toggleTone')).toContain('playingDbFs === null')
  })

  it('turns the input step off while they are not', () => {
    const block = source.slice(source.indexOf('const canMeasureInput'))
    expect(block.slice(0, block.indexOf(')\n'))).toContain('settingsInEffect')
  })

  it('says why, rather than leaving dead controls unexplained', () => {
    expect(source).toContain('Nothing here is in effect')
  })

  it('does not flash that notice through every ordinary change', () => {
    // Gated on the durable refusal and not on the buttons' own test, which is false for the
    // 250 ms of the debounce after any dropdown moves -- a red box appearing for a quarter second
    // each time one is touched is alarming about something that is about to be fine.
    const notice = source.lastIndexOf('<p', source.indexOf('Nothing here is in effect'))
    const start = source.indexOf('v-if="', notice) + 'v-if="'.length
    expect(source.slice(start, source.indexOf('"', start))).toBe('settingsRefused')
  })
})

/** The `:disabled` expression of the button whose @click calls `handler`. */
function buttonBinding(handler: string): string {
  const click = source.indexOf(`@click="${handler}"`)
  expect(click, `no button calls ${handler}`).toBeGreaterThan(-1)
  const open = source.lastIndexOf('<button', click)
  const disabled = source.indexOf(':disabled="', open)
  expect(disabled, `the ${handler} button has no :disabled binding`).toBeGreaterThan(-1)
  expect(disabled).toBeLessThan(click)
  const start = disabled + ':disabled="'.length
  return source.slice(start, source.indexOf('"', start))
}
