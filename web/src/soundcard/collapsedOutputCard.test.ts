import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * Unticking Separate devices must say which card the output just left.
 *
 * Reported 2026-11-11, and it had already cost a calibration. With a Sound BlasterX G6 as the input
 * and a MOTU M4 as the output, unticking collapsed both directions onto the G6 — the input's card
 * wins, which is the documented rule — so the *output* silently moved to the G6's speakers. The
 * output calibration run twenty minutes later therefore measured the G6: a 3874 mV full scale
 * belonging to an interface the bench was not driving, used from then on for every level.
 *
 * The calibration-drift notice did catch it afterwards, which is what that notice is for. What was
 * missing was anything saying the output had moved at the moment it moved, so the drift notice read
 * as a mistake rather than as a consequence.
 *
 * It lives in a `.vue`, so this reads the component as text as the suite's other template rules do.
 */
const source = readFileSync(
  fileURLToPath(new URL('../components/SoundCardDialog.vue', import.meta.url)),
  'utf8',
)
  .replace(/^﻿/, '')
  .replace(/\r\n/g, '\n')

const handler = source.slice(
  source.indexOf('function onSeparateDevicesChanged'),
  source.indexOf('watch(() => form.cardKey'),
)

describe('collapsing two cards onto one says so', () => {
  it('still keeps the input’s card, which is the rule the output follows', () => {
    expect(handler).toContain('form.cardKey = form.inputCardKey || form.outputCardKey')
  })

  it('records the output card it discarded', () => {
    expect(handler).toContain('collapsedOutputCard.value')
    // Read BEFORE cardKey is reassigned -- afterwards the watcher below has already re-pointed the
    // output device, and the card that was dropped is no longer anywhere to be read.
    expect(handler.indexOf('const dropped = form.outputCardKey')).toBeLessThan(
      handler.indexOf('form.cardKey = form.inputCardKey'),
    )
  })

  it('says nothing when both sides were already the same card', () => {
    // Ticking and unticking without changing anything must not accuse the dialog of moving an
    // output it left alone.
    expect(handler).toMatch(/dropped !== form\.inputCardKey/)
  })

  it('clears the note when Separate devices is ticked again', () => {
    const on = handler.slice(handler.indexOf('if (form.separateDevices)'), handler.indexOf('const dropped'))
    expect(on).toContain('collapsedOutputCard.value = null')
  })

  it('tells the reader to re-run the output calibration, which is the part that bit', () => {
    expect(source).toContain('Re-run the output')
    expect(source).toContain('does not describe')
  })
})
