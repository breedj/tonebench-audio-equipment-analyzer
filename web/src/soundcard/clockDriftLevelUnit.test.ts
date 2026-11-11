import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * The clock-drift dialog's Level box must name the unit it is really in.
 *
 * Reported 2026-11-11: "shouldn't we change that dBV to dBFS instead, that is why I was confused".
 * dBV asserts volts, and an uncalibrated card has no volts scale — `SoundCardCalibration` assumes
 * root-2 V peak for one precisely so that dBV and dBFS coincide numerically, which makes the figure
 * right and a fixed "dBV" label a claim the app cannot back. The status bar has said "not
 * calibrated, so levels are dBFS rather than volts" all along; this is that convention applied to
 * the one control that sets a level.
 *
 * It lives in a `.vue`, which is not mountable under this suite's node environment, so this reads
 * the component as text the way `settingsInEffect.test.ts` and `panelIcons.test.ts` do. The
 * regression it exists for is someone putting the literal back — which is invisible on a calibrated
 * bench, because there dBV is the right answer and nothing looks wrong.
 */
const source = readFileSync(
  fileURLToPath(new URL('../components/ClockDriftDialog.vue', import.meta.url)),
  'utf8',
)
  .replace(/^﻿/, '')
  .replace(/\r\n/g, '\n')

describe('the clock drift Level box names its own unit', () => {
  it('decides it from the output calibration, not a literal', () => {
    const unit = source.slice(source.indexOf('const levelUnit'))
    const line = unit.slice(0, unit.indexOf('\n'))

    // The OUTPUT full scale, not the pair and not `soundCardCalibrated`: this box drives the
    // generator, and an input-only calibration says nothing about what it plays.
    expect(line).toContain('outputFullScalePeakMv')
    expect(line).toContain("'dBV'")
    expect(line).toContain("'dBFS'")
  })

  it('renders that unit beside the field rather than a fixed dBV', () => {
    const level = source.indexOf('form.levelDbv')
    expect(level).toBeGreaterThan(-1)

    // The suffix span within the Level row -- the next unit-ish span after the input.
    const after = source.slice(level, level + 600)
    expect(after).toContain('{{ levelUnit }}')
    expect(after).not.toMatch(/<span class="text-neutral-500">dBV<\/span>/)
  })

  it('still says in words what an uncalibrated card means', () => {
    // The label alone answers "what unit"; it does not answer "so do I have to calibrate first?",
    // which is the question that was actually asked.
    expect(source).toContain('Calibration is not needed for this')
    expect(source).toContain('dBFS')
  })
})
