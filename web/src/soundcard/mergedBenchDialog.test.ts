import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * One dialog for the devices a bench is made of, and the rules only the merged one can get wrong.
 *
 * Configuring a split meant three gear buttons and three dialogs: one chose the two *kinds*, one
 * chose *which card* each half was, and a third held the QA40x's own settings. They were not
 * alternatives — `splitHalves` keyed each gear on the bench's halves, so a QA40x+card split showed
 * all three at once — so "which device captures" and "which card that is" were two questions about
 * one half, in two places, with a prose link between them. Asked for 2026-11-15; this is the merge.
 *
 * What is asserted here is the merge's own seams, not the card dialog's behaviour (that is
 * `splitNarrowing.test.ts`) nor the module's rules (`benchHalves.test.ts`). Four of them can break
 * silently, and each has its own case below: a computed re-pointed back at `props.status`, a
 * refusal that stops gating the write, the two documents written in the wrong order, and a card
 * control left on screen for a bench with no card in it.
 *
 * Read as text because it lives in a `.vue`, which is not mountable under this suite's node
 * environment — the technique `settingsInEffect.test.ts` and `splitNarrowing.test.ts` use.
 */
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

/** The body of a function, up to the next top-level declaration. */
function functionBody(name: string): string {
  const start = source.indexOf(`function ${name}(`)
  expect(start, `${name} is declared`).toBeGreaterThan(-1)
  const rest = source.slice(start + 1)
  const end = rest.search(/\n(const|function|async function|watch\()/)
  return end === -1 ? rest : rest.slice(0, end)
}

describe('the dialog says which bench it is configuring', () => {
  it('is titled for the analyzer, not for the card', () => {
    expect(computedBody('dialogTitle')).toContain("'Split devices'")
    expect(computedBody('dialogTitle')).toContain("'Sound card'")
    expect(source).toContain(':title="dialogTitle"')
  })

  it('sends the footer to the manual page for the bench on screen', () => {
    // Two benches, two pages. A single link would send a split user to the sound-card page, which
    // describes neither the pairing nor the generator rate.
    expect(source).toContain("openManual(isSplitBench ? 'split-device' : 'sound-card')")
  })
})

describe('every narrowing reads the form', () => {
  /**
   * The one regression that re-creates a dialog disagreeing with itself. Judged on `props.status`
   * these describe the bench being *replaced*, which was a quarter-second wrinkle while the halves
   * could only be changed in another dialog and is the common case after every pick here.
   */
  it('derives which half is a card, which streams open and which refusals apply from it', () => {
    for (const name of ['cardDirection', 'twoCardBench', 'generatorIsCard', 'generatorName']) {
      const body = computedBody(name)
      // Either directly, or through cardDirection, which is the one that reads it.
      expect(body, `${name} reads the form`).toMatch(
        /form\.(input|output)Kind|isSplitBench|cardDirection/,
      )
      expect(body, `${name} does not read the session's halves`).not.toContain('splitInputKind')
      expect(body, `${name} does not read the session's halves`).not.toContain('splitOutputKind')
    }

    // asioRefusal goes through twoCardBench, so it follows the form too -- judged on the session it
    // is silent on exactly the pick that creates the pair, for as long as the write takes to land.
    expect(computedBody('asioRefusal')).toContain('twoCardBench.value')
  })

  it('and the probe asks about the bench the form stages', () => {
    expect(computedBody('opens')).toContain('openedSides(cardDirection.value, twoCardBench.value)')
  })
})

describe('a refused bench is never written', () => {
  it('gates the write on the pairing as well as the cards and the rates', () => {
    const body = computedBody('canSave')
    expect(body).toContain('asioRefusal.value')
    expect(body).toContain('pairingProblem.value')
  })

  it('and says why, through the notice that promises a reason is on screen', () => {
    // settingsInEffect reads canSave, so a refused pairing disables the tone button and the input
    // step for free -- but the notice has to name the refusal too, or it points at nothing.
    expect(computedBody('settingsRefused')).toContain('pairingProblem.value')
    expect(source).toContain('{{ pairingProblem }}')
  })

  it('leaves the two-cards branch to the one refusal that has its own line', () => {
    // benchPairingProblem answers it as well, by delegating to the same function, so judging it in
    // both places would print one sentence twice.
    const body = computedBody('pairingProblem')
    expect(body).toContain('inputCardKey.value')
    expect(body).toContain('outputCardKey.value')
    expect(body).toContain('return null')
  })
})

describe('the two documents are written in the order that costs one reopen', () => {
  const body = functionBody('applyDevice')

  it('writes only what moved', () => {
    expect(body).toContain('documentsTouched(')
    expect(body).toContain('if (moved.card)')
    expect(body).toContain('if (moved.split)')
  })

  it('writes the card document before the split one', () => {
    // PUT /config/soundcard reopens whenever the bench uses a card and PUT /config/split-device
    // reopens on a split, which here it always is. The other way round, the split's reopen builds
    // both halves against the OLD card names and the card write then reopens a second time -- so
    // the first open claims hardware the user is in the middle of leaving.
    const card = body.indexOf('moved.card')
    const split = body.indexOf('moved.split')
    expect(card, 'the card write exists').toBeGreaterThan(-1)
    expect(split, 'the split write exists').toBeGreaterThan(-1)
    expect(card).toBeLessThan(split)
  })

  it('keeps the stored split describing the server even when the write fails', () => {
    // splitInEffect compares against it, so holding a value the server rejected would report a
    // bench as applied that is not -- and that flag is what holds the drift run.
    const save = functionBody('saveSplit')
    expect(save).toContain('storedSplit.value = await api.setSplitDeviceConfig(next)')
    expect(save).toContain('await loadSplit()')
  })

  it('rides one debounce, so a pick that moves both is one write of each', () => {
    // The watcher's own list, found from the call it ends in rather than from a field name -- the
    // same fields appear in stagedBench() above it.
    const at = source.indexOf('() => applyDeviceSoon(),')
    expect(at, 'the watcher exists').toBeGreaterThan(-1)
    const list = source.slice(source.lastIndexOf('watch(', at), at)

    expect(list).toContain('form.inputKind,')
    expect(list).toContain('form.outputKind,')
    expect(list).toContain('form.generatorSampleRateHz,')

    // And the card document's own fields are still in it, or a channel pair stops being saved.
    expect(list).toContain('form.inputChannelOffset,')
    expect(list).toContain('form.streamStartSettleMs,')
  })
})

/**
 * A split may have no card on either half — a QA40x against the simulator — and both of these were
 * found in the running app on exactly that pairing. They are the one bench the merged dialog made
 * reachable that neither dialog it replaced had to think about: the card dialog was never opened on
 * a bench with no card in it.
 */
describe('a bench with no card on either half', () => {
  it('is still written, although there is no card to probe', () => {
    // The three tests in canSave are about a card opening. Judged unconditionally they were false
    // for ever on this pairing, so its own document was never written and the drift button never
    // came alive -- and nothing said so, because the notice is gated on a refusal there was none of.
    const body = computedBody('canSave')
    expect(body).toContain('usesAnyCard.value')

    const gate = body.indexOf('usesAnyCard.value')
    const probe = body.indexOf('probedBench.value.input')
    expect(probe, 'the probe tests exist').toBeGreaterThan(-1)
    expect(gate, 'and sit inside the card gate').toBeLessThan(probe)
  })

  it('offers no ASIO control panel for a half that is not a card', () => {
    // inputCardKey is the *stored* card, which a half that is not a card deliberately leaves alone
    // -- so without this the button sat beside an Input card dropdown reading QA40x and would have
    // opened the panel of a card nothing in the bench opens. Seen in the running app.
    expect(computedBody('selectedCard')).toContain('inputIsCard.value')
  })

  it('shows no card rows at all, because cardDirection is null there', () => {
    // The groups were gated on `cardDirection !== 'output'` / `!== 'input'`, which held while that
    // computed was never null on a reachable bench. `null !== 'output'` is true, so both groups
    // rendered every card row they had -- a read-only device row naming a card nothing was playing
    // through, and the two figures of a document this bench does not open. Positive tests instead.
    expect(source).not.toContain("v-if=\"cardDirection !== 'output'\"")
    expect(source).not.toContain("v-if=\"cardDirection !== 'input'\"")
    expect(computedBody('inputIsCard')).toContain("form.inputKind === 'SoundCard'")
    expect(computedBody('outputIsCard')).toContain("form.outputKind === 'SoundCard'")

    // The rows that name a device inside a card, and the two figures that belong to the card
    // document, are each gated on a half actually being one.
    expect(source).toContain('v-if="inputIsCard && cardInputs.length === 1"')
    expect(source).toContain('v-if="inputIsCard && inputPairs.length > 1"')
    expect(source).toContain('v-if="outputIsCard && cardOutputs.length === 1"')
    expect(source).toContain('v-if="outputIsCard && outputPairs.length > 1"')
    expect(source).toContain('v-if="usesAnyCard"')
  })
})

/**
 * The output step's second route: the capturing half reads the card instead of a voltmeter.
 *
 * <p>It is the mirror of the input step's Loopback, and it is offered under the mirror condition —
 * the half doing the reading must not be a card, because a card's own input full scale is the very
 * thing that is not known yet. On a plain sound-card bench both halves are that card, so the route
 * cannot exist there and the voltmeter is the only way in; a split is what makes it possible.</p>
 */
describe('measuring the output with the analyzer', () => {
  it('is offered only where the capturing half is not a card', () => {
    const body = computedBody('analyzerCanMeasureOutput')
    expect(body).toContain('playsThroughCard.value')
    expect(body).toContain('!capturesWithCard.value')

    const at = source.indexOf('<span class="sc-label">Measured by</span>')
    expect(at, 'the route dropdown exists').toBeGreaterThan(-1)
    expect(source.slice(source.lastIndexOf('<label', at), at)).toContain('analyzerCanMeasureOutput')
  })

  it('falls back to the voltmeter wherever it cannot be offered', () => {
    // Staged state outlives the bench that allowed it: picking the analyzer route and then putting
    // a card on the capturing half would otherwise leave a route selected that the server refuses.
    const body = computedBody('outputRoute')
    expect(body).toContain('analyzerCanMeasureOutput.value')
    expect(body).toContain("'Voltmeter'")
  })

  it('swaps the voltmeter entry and the button with the route', () => {
    expect(source).toContain('v-if="outputRoute === \'Voltmeter\'"')

    // The analyzer route plays its own tone in the same request, so it does not inherit the
    // voltmeter route's "start the tone first" precondition.
    const at = source.indexOf('@click="acceptOutput"')
    const button = source.slice(source.lastIndexOf('<button', at), at)
    expect(button).toContain("outputRoute === 'Analyzer' ? !settingsInEffect : playingDbFs === null")
  })

  it('sends it to the step that measures rather than the one that is told', () => {
    const body = functionBody('acceptOutput')
    expect(body).toContain('measureOutputViaAnalyzer(outputStep.dbFs)')
    expect(body).toContain('applyOutput(outputStep.reading, outputStep.unit)')
  })
})

describe('a control is on screen only where it applies', () => {
  it('offers the generator rate on a split and nowhere else', () => {
    const at = source.indexOf('<span class="sc-label">Generator rate</span>')
    expect(at, 'the row exists').toBeGreaterThan(-1)
    const label = source.lastIndexOf('<label', at)
    expect(source.slice(label, at)).toContain('isSplitBench')
  })

  it('offers the device dropdowns on a split and the card list otherwise', () => {
    // A QA40x entry on a plain sound-card bench would have to re-point the whole session, which is
    // the status bar's Analyzer dropdown's job.
    const at = source.indexOf('v-for="o in inputHalfOptions"')
    expect(at, 'the merged dropdown exists').toBeGreaterThan(-1)
    const select = source.slice(source.lastIndexOf('<select', at), at)
    expect(select).toContain('v-if="isSplitBench"')

    // And the card list is still there for the bench that wants it.
    expect(source).toContain('v-else-if="!separateDevices"')
    expect(source).toContain('v-model="form.cardKey"')
  })

  it('carries the QA40x ranging delay for a half that has a ranger', () => {
    // It had a gear and a dialog of its own until the merge, and that gear now stands down on a
    // split -- so without this the setting would be unreachable on exactly the bench that has it.
    const at = source.indexOf('Auto-range step-down delay')
    expect(at, 'the row exists').toBeGreaterThan(-1)
    const section = source.lastIndexOf('<section', at)
    expect(source.slice(section, source.indexOf('>', section))).toContain('hasHardwareRanger')
    expect(computedBody('hasHardwareRanger')).toContain('autoRangeStepDownDelayMs')
  })

  it('shows the clock drift panel on a split, after both calibration steps', () => {
    // The run needs both halves working before it means anything.
    const panel = source.indexOf('<BenchClockDriftPanel')
    const inputStep = source.indexOf('Full scale input')
    expect(panel, 'the panel is rendered').toBeGreaterThan(-1)
    expect(inputStep).toBeLessThan(panel)
  })
})
