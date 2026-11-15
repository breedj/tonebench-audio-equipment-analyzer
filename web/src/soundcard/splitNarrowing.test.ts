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
 * <b>And asked a third time when the three device dialogs became one.</b> The two halves are now
 * chosen *here*, one dropdown per direction naming the device, so what the narrowing reads is
 * `form.inputKind` / `form.outputKind` rather than the session's `splitInputKind` /
 * `splitOutputKind`. That is not a tidy-up: judged on the session every rule below describes the
 * bench being replaced, which was a quarter-second disagreement while the halves could only be
 * changed in another dialog and is the common case after every pick now. The cases moved again,
 * and the `not.toContain` in the first of them is what stops them moving back.
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
  fileURLToPath(new URL('../components/DeviceSettingsDialog.vue', import.meta.url)),
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
  it('derives it from the two halves in the form, not from the bench in use', () => {
    const body = computedBody('cardDirection')

    // Both halves consulted: keyed on one, the dialog would offer the wrong direction on half the
    // pairings rather than none of them, which is the harder fault to notice. Through the two
    // per-half computeds, which are the one definition of "this half is a card".
    expect(body).toContain('inputIsCard.value')
    expect(body).toContain('outputIsCard.value')
    expect(computedBody('inputIsCard')).toContain("form.inputKind === 'SoundCard'")
    expect(computedBody('outputIsCard')).toContain("form.outputKind === 'SoundCard'")

    // And the regression this replaced: read off the session, every rule below describes the bench
    // being *replaced*, so the dialog disagrees with itself for as long as the write takes to land.
    // That was tolerable while the halves could only be changed in another dialog; with the two
    // dropdowns here it is the common case after every pick.
    expect(body).not.toContain('splitInputKind')
    expect(body).not.toContain('splitOutputKind')
    expect(body).not.toContain('deviceKind')
  })

  it('opens those halves on the bench the session has, and on nothing else', () => {
    // The form is seeded once per open: a split names its two halves, a plain sound card is the
    // card both ways, and a QA40x or the simulator is neither direction of this document -- which
    // leaves every card control off screen, and is also when this dialog is unreachable.
    const body = source.slice(
      source.indexOf('function benchHalfKinds('),
      source.indexOf('function benchHalfKinds(') + 500,
    )
    expect(body).toContain("kind === 'Split' && split")
    expect(body).toContain('split.inputKind')
    expect(body).toContain('split.outputKind')
    expect(body).toContain("kind === 'Hardware' || kind === 'Simulated'")
    expect(body).toContain("return { input: 'SoundCard', output: 'SoundCard' }")
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
    expect(bench).toContain('isSplitBench.value')
    expect(bench).toContain("form.inputKind === 'SoundCard'")
    expect(bench).toContain("form.outputKind === 'SoundCard'")

    // The analyzer kind really is the session's -- it decides whether there is a pairing to choose
    // at all, and moving between a split and a plain card is the status bar's Analyzer dropdown.
    expect(computedBody('isSplitBench')).toContain("props.status?.deviceKind === 'Split'")

    // Every split picks per direction, two cards or not: the single picker's premise is "one
    // interface, both directions", which is exactly what a split is not -- and on a split with a
    // QA40x half there is no shared interface for one dropdown to name.
    const body = computedBody('separateDevices')
    expect(body).toContain('isSplitBench.value')

    // And a one-direction bench, which is how a capture-only ADC comes back: one name stored, so
    // the two keys differ and a single picker cannot express it.
    expect(body).toContain('form.inputCardKey !== form.outputCardKey')
  })

  it('reads a card on both halves as both directions, not as the input one', () => {
    const body = computedBody('cardDirection')

    // Order, not presence: the single-half tests below match a two-card split too, so testing
    // either of them first would call it 'input' and hide the output picker entirely -- on a bench
    // where the output card is one of the two things being chosen.
    const both = body.indexOf("if (input && output) return 'both'")
    const inputOnly = body.indexOf("if (input) return 'input'")
    expect(both, 'both halves are tested').toBeGreaterThan(-1)
    expect(inputOnly, 'one half is tested').toBeGreaterThan(-1)
    expect(both).toBeLessThan(inputOnly)
  })

  /**
   * The group wrappers hold each half's own device dropdown now, so on a split they always render
   * and it is the rows *inside* them that narrow — see `mergedBenchDialog.test.ts`, which pins the
   * card rows and the reason the test moved. A positive test either way: `cardDirection` is null on
   * a bench with no card on either half, and `null !== 'output'` rendered every card row there.
   */
  it('keeps the input group where this bench captures with a card, or picks the device', () => {
    expect(groupWrapper('Input')).toContain('isSplitBench || inputIsCard')
  })

  it('keeps the output group where this bench plays through a card, or picks the device', () => {
    expect(groupWrapper('Output')).toContain('isSplitBench || outputIsCard')
  })

  /**
   * And what replaced the note that used to stand in for the hidden half.
   *
   * <p>`splitNote` said which device the *other* half was and sent the reader to the split settings
   * for it. Both halves of that sentence stopped being true when the dialogs merged: each
   * direction's own dropdown names its device, and there is no second dialog to be sent to. What a
   * reader needs instead is the bench as a whole, in one sentence, so the two dropdowns cannot be
   * read the wrong way round — which is the thing an absence could never have supported either.</p>
   */
  it('names every half in its own dropdown, and the bench in a sentence', () => {
    // The computed and its render, not the bare word: two NOTEs in the component name what was
    // removed and why, which is worth keeping and must not read as the note still being there.
    // The same reading as the Separate devices case above.
    expect(source).not.toContain('const splitNote = computed')
    expect(source).not.toContain('v-if="splitNote"')

    // Both directions offer the two non-card kinds, so neither half is a gap to be inferred.
    expect(source).toContain('v-for="o in inputHalfOptions"')
    expect(source).toContain('v-for="o in outputHalfOptions"')

    const summary = computedBody('benchSummary')
    expect(summary).toContain('generates')
    expect(summary).toContain('captures')
    expect(source).toContain('{{ benchSummary }}')
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


  /**
   * Both steps are gated through `playsThroughCard` / `capturesWithCard` rather than on
   * `cardDirection` directly, and the extra half of each is the 2026-11-15 report: `cardDirection`
   * answers which half of the *bench* the card serves, which on a plain sound card is always
   * 'both' -- so a playback-only DAC was offered an input calibration whose source dropdown opens
   * on "this card's output, through a patch cable" over a card that has no input to loop back
   * into. The split rule is still the one being asserted; it is now the first clause of it.
   */
  it('offers the output step only where this card plays, and has an output to play through', () => {
    const heading = source.indexOf('Full scale output')
    const section = source.lastIndexOf('<section', heading)
    expect(source.slice(section, source.indexOf('>', section))).toContain('playsThroughCard')

    const body = computedBody('playsThroughCard')
    expect(body).toContain('opens.value.output')
    expect(body).toContain('selectedOutput.value')
  })

  it('offers the input steps only where this card captures, and has an input to capture with', () => {
    const heading = source.indexOf('Full scale input')
    const section = source.lastIndexOf('<section', heading)
    expect(source.slice(section, source.indexOf('>', section))).toContain('capturesWithCard')

    const body = computedBody('capturesWithCard')
    expect(body).toContain('opens.value.input')
    expect(body).toContain('selectedInput.value')
  })

  // And `opens` is still the split's own rule, so the two above carry it: openedSides() maps
  // 'input'/'output'/'both' onto the sides, and probeSides.test.ts pins that mapping.
  it('derives the sides it opens from the split pairing', () => {
    expect(computedBody('opens')).toContain('openedSides(cardDirection.value, twoCardBench.value)')
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
    expect(body).toContain('form.outputKind')

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
