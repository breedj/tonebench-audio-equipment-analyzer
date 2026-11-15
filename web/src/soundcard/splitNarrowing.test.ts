import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * The sound-card dialog shows only the direction the bench in use actually opens.
 *
 * A split uses one direction of this document and discards the other -- the factory masks the
 * unused side out through `SoundCardSettings.ForDirection` -- so a picker for that side offers a
 * choice that cannot take effect, and a calibration step for it offers a button whose only answer
 * is the server's refusal. Both were offered, which is how "should split allow Separate devices?"
 * came to be asked (2026-11-13); the answer was no, and the narrowing is this.
 *
 * <b>Asked again on 2026-11-15 and answered the other way round</b>: the two features were merged
 * rather than kept apart. `DeviceKind.SoundCard` now means ONE physical interface, two cards are a
 * split with a card on each half, and "Separate devices" is gone as a control -- so what used to be
 * a checkbox the narrowing had to hide is now a value derived from the bench itself. The cases
 * below moved with it rather than being deleted: they still pin which direction the dialog offers,
 * which is the question that outlived both answers.
 *
 * All of it lives in a `.vue`, which is not mountable under this suite's node environment, so this
 * reads the component as text the way `settingsInEffect.test.ts`, `annotationNumbersDefault.test.ts`
 * and `panelIcons.test.ts` do. What it catches is the one change that puts the fault back: a gate
 * removed, or a refusal re-keyed on the bench as a whole.
 */
// Normalised for the reason settingsInEffect.test.ts records: this file is CRLF with a BOM where
// most of `web/src` is LF, so a pattern written with "\n" would match nothing and `indexOf`'s -1
// would hand the slice almost the whole file -- a test that passes and guards nothing.
const source = readFileSync(
  fileURLToPath(new URL('../components/SoundCardDialog.vue', import.meta.url)),
  'utf8',
)
  .replace(/^﻿/, '')
  .replace(/\r\n/g, '\n')

/** The body of a computed, up to the next top-level `const`. */
function computedBody(name: string): string {
  const start = source.indexOf(`const ${name} = computed`)
  expect(start, `${name} is declared`).toBeGreaterThan(-1)
  const rest = source.slice(start + 1)
  const end = rest.indexOf('\nconst ')
  return end === -1 ? rest : rest.slice(0, end)
}

/**
 * The `sc-group` wrapper around one direction's rows, found from the caption inside it. Not the
 * nearest preceding `<div`, which is the caption's own rule.
 */
function groupWrapper(caption: 'Input' | 'Output'): string {
  const at = source.indexOf(`<span class="tb-group">${caption}</span>`)
  expect(at, `the ${caption} group is captioned`).toBeGreaterThan(-1)
  const open = source.lastIndexOf('<div class="sc-group"', at)
  const guarded = source.lastIndexOf('<div v-if=', at)
  const start = Math.max(open, guarded)
  return source.slice(start, source.indexOf('>', start) + 1)
}

describe('which direction the dialog offers', () => {
  it('derives it from the analyzer kind and both halves of a split', () => {
    const body = computedBody('cardDirection')

    // 'both' for the Sound card analyzer, which is every bench this dialog had before splits.
    expect(body).toMatch(/deviceKind === 'SoundCard'\)?\s*return 'both'/)

    // Both halves consulted: keyed on one, the dialog would offer the wrong direction on half the
    // pairings rather than none of them, which is the harder fault to notice.
    expect(body).toContain('splitInputKind')
    expect(body).toContain('splitOutputKind')
  })

  it('is told the device status rather than guessing from the stored document', () => {
    // The stored card document has both sides filled in on a bench that has ever used the Sound
    // card analyzer, so it cannot say which direction a split opens. Only the status can.
    expect(source).toMatch(/defineProps<\{[^}]*status: DeviceStatus \| null[^}]*\}>/)
  })
})

describe('the pickers a split does not use', () => {
  it('offers no Separate devices control at all', () => {
    // The merge, stated as an absence. A sound card is one interface, so there is nothing here for
    // a user to decide: two cards is a different analyzer kind, not a checkbox on this one.
    // The control and its handler, not the bare words: a NOTE in the component names what was
    // removed and why, which is worth keeping and must not read as the feature still being there.
    expect(source).not.toContain('separateDevices: false')
    expect(source).not.toContain('v-model="form.separateDevices"')
    expect(source).not.toContain('@change="onSeparateDevicesChanged"')
    expect(source).not.toContain('function onSeparateDevicesChanged')
  })

  it('derives two pickers from the bench rather than from a control', () => {
    // A card on each half of a split: both cards are chosen here, because the card document is
    // still the one place a card is picked. It is its own computed because the host-API rules turn
    // on it too -- two streams have none of one stream's restrictions.
    const bench = computedBody('twoCardBench')
    expect(bench).toContain("deviceKind === 'Split'")
    expect(bench).toContain("splitInputKind === 'SoundCard'")
    expect(bench).toContain("splitOutputKind === 'SoundCard'")

    // And a one-direction bench, which is how a capture-only ADC comes back: one name stored, so
    // the two keys differ and a single picker cannot express it.
    const body = computedBody('separateDevices')
    expect(body).toContain('twoCardBench.value')
    expect(body).toContain('form.inputCardKey !== form.outputCardKey')
  })

  it('reads a card on both halves as both directions, not as the input one', () => {
    const body = computedBody('cardDirection')

    // Order, not presence: the single-half tests below match a two-card split too, so testing
    // either of them first would call it 'input' and hide the output picker behind a note saying
    // the output is set in the split settings -- where there would be nothing to set.
    const both = body.indexOf("splitInputKind === 'SoundCard' && s.splitOutputKind === 'SoundCard'")
    const inputOnly = body.indexOf("if (s.splitInputKind === 'SoundCard') return 'input'")
    expect(both, 'both halves are tested').toBeGreaterThan(-1)
    expect(inputOnly, 'one half is tested').toBeGreaterThan(-1)
    expect(both).toBeLessThan(inputOnly)
  })

  it('hides the input group where the bench captures with something else', () => {
    expect(groupWrapper('Input')).toContain("cardDirection !== 'output'")
  })

  it('hides the output group where the bench plays through something else', () => {
    expect(groupWrapper('Output')).toContain("cardDirection !== 'input'")
  })

  it('says which half the other device is, rather than leaving the gap to be noticed', () => {
    const body = computedBody('splitNote')
    expect(body).toContain('plays through the sound card')
    expect(body).toContain('captures with the sound card')
    expect(source).toContain('v-if="splitNote"')
  })
})

describe('the calibration steps a split does not use', () => {
  /**
   * And the complaints about them.
   *
   * <p>A stale or missing calibration on a direction this bench does not open is an instruction the
   * user cannot carry out: the step that would clear it is not on screen. Seen in the running app on
   * a split that captures with the card, where an output full scale taken through a different DAC
   * read "Run the output step again" beside a dialog with no output step in it.</p>
   */
  it('complains only about the direction in use', () => {
    const body = computedBody('calibrationNotices')
    expect(body).toMatch(/const inUse = \(direction: 'input' \| 'output'\)/)
    expect(body).toContain('state.drifted.filter((d) => inUse(d.direction))')
    expect(body).toContain('state.uncalibrated.filter(inUse)')

    // And nothing reads the unfiltered list past its own declaration, which is what put the notice
    // back: three branches read it and only one had to be missed.
    const decl = body.indexOf('const uncalibrated')
    expect(body.slice(decl + 'const uncalibrated = state.uncalibrated.filter(inUse)'.length))
      .not.toContain('state.uncalibrated')
  })


  it('offers the output step only where this card plays', () => {
    const heading = source.indexOf('Full scale output')
    const section = source.lastIndexOf('<section', heading)
    expect(source.slice(section, source.indexOf('>', section))).toContain(
      "cardDirection !== 'input'",
    )
  })

  it('offers the input steps only where this card captures', () => {
    const heading = source.indexOf('Full scale input')
    const section = source.lastIndexOf('<section', heading)
    expect(source.slice(section, source.indexOf('>', section))).toContain(
      "cardDirection !== 'output'",
    )
  })

  /**
   * The loopback route's refusal belongs to an uncalibrated *card*, not to every bench: a QA40x's
   * output level is factory calibrated, so on a split it is a known source with no calibration step
   * of its own -- which is the pairing this route is most useful on, and the one a whole-bench check
   * refused while telling the user to calibrate an output that cannot be calibrated.
   */
  it('asks for an output calibration only where the card is what plays', () => {
    expect(computedBody('generatorIsCard')).toContain("cardDirection.value !== 'input'")
    expect(computedBody('loopbackNeedsOutputCalibration')).toContain('generatorIsCard')

    // And both readers go through it rather than reading outputCalibrated again.
    expect(computedBody('canMeasureInput')).toContain('loopbackNeedsOutputCalibration')
    expect(source).toContain(
      'v-if="inputStep.source === \'Loopback\' && loopbackNeedsOutputCalibration"',
    )
  })

  it('names what plays the loopback tone instead of claiming it is this card', () => {
    const body = computedBody('generatorName')
    expect(body).toContain('generatorIsCard')
    expect(body).toContain('splitOutputKind')

    // The dropdown option is the one place a reader meets it, through its own capitalised form.
    expect(source).toContain('<option value="Loopback">{{ loopbackSourceLabel }}</option>')
    expect(computedBody('loopbackSourceLabel')).toContain('generatorName')
  })

  /**
   * And the memo around that dropdown has to list the label.
   *
   * <p>These selects are memoised because the dialog polls the level bars every 120 ms and Vue
   * rewrites an `<option>`'s value unconditionally, which resets an open native dropdown. A memo
   * lists what its subtree depends on -- and this one was written when the Loopback option's text
   * was a literal, so adding a computed to it froze the option at "this card's output" on a split
   * the QA40x plays. Caught in the running app, where the note above it had already updated and
   * only the dropdown had not.</p>
   */
  it('lists that label in the memo around the dropdown that renders it', () => {
    // To the first <option> rather than to the first '>': the attributes carry a comment, and a
    // comment ends in '-->'.
    const at = source.indexOf('v-model="inputStep.source"')
    expect(at).toBeGreaterThan(-1)
    const tag = source.slice(source.lastIndexOf('<select', at), source.indexOf('<option', at))
    expect(tag).toContain('v-memo')
    expect(tag).toContain('loopbackSourceLabel')
  })
})
