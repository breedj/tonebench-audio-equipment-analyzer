import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * Where a panel's explanation lives, and what must not follow it there.
 *
 * <p>The scope's display rail used to carry a paragraph under most of its sections explaining how
 * their controls work, which is a great deal of height spent on text a reader needs once. Those
 * moved to <code>CollapsibleSection</code>'s <code>hint</code>, shown on hovering the panel's icon.
 * <b>The rule that decides each case is: state stays on screen, explanation moves to the hint.</b>
 * A caption saying what is happening <em>now</em> — a warning, a count, which coupling is in the
 * path — is the panel talking about the present and has to stay visible.</p>
 *
 * <p>Both halves live in <code>.vue</code> files and the suite runs on <code>environment: node</code>,
 * so this reads them as text, the way the persistence and number-draft audits already do.</p>
 */

const read = (name: string) =>
  readFileSync(resolve(import.meta.dirname, '..', 'components', name), 'utf8')

const rail = read('ScopeDisplayPanel.vue')
const section = read('CollapsibleSection.vue')

describe('the display rail’s panels', () => {
  it('gives every section a hint, so none of them loses its explanation outright', () => {
    // Each opening tag, up to the first '>' that is not inside an attribute value.
    const opens = [...rail.matchAll(/<CollapsibleSection\b([\s\S]*?)>\s*\n/g)].map((m) => m[1])

    expect(opens.length).toBeGreaterThanOrEqual(9)
    for (const attrs of opens) {
      const title = /:?title="([^"]*)"/.exec(attrs)?.[1] ?? '(untitled)'
      expect(/\bhint="|:hint="/.test(attrs), `${title} has no hint`).toBe(true)
    }
  })

  /**
   * The other half, and the one that is easy to lose in a tidy-up: what a panel is saying about
   * right now cannot move into a tooltip, because a reader who is not hovering would never see it.
   */
  it('leaves every state line and warning on screen', () => {
    // Two left this list on 2026-11-21, both at Joost's ask and both because the *control* had
    // come to say the same thing: the drawing that has fallen back is now the dimmed button with
    // the reason on its title, and the coupling in force is the lit half of its own toggle, in its
    // own hue and with its own figure. What a caption may not do is leave with nothing in its
    // place, so each is checked for below.
    for (const line of [
      // The view that cannot be drawn with one input.
      'Nothing to draw: an X–Y figure needs both inputs',
      // A math channel that cannot be built, and one that is railing. Per channel now, so each
      // says which of the two it is about.
      'has nothing to draw: it needs the',
      'samples railed at ±1000',
      // The mask: which reference, and how the run is going.
      'Tick a history item to compare against',
      'passed, {{ maskTally.failed.toLocaleString() }} failed',
    ]) {
      expect(rail, `the rail no longer says "${line}"`).toContain(line)
    }
  })

  /**
   * The two captions removed on 2026-11-21 said something about the present, so the rule above
   * applies to them: what replaced each is on the control itself, which is where a reader who is
   * not hovering still sees it.
   */
  it('says a fallen-back drawing on the button, now that it no longer says it underneath', () => {
    expect(rail).not.toContain('Lines for now: this window is wide enough')
    // Lit, chosen, and not what is being drawn: dimmed, with the reason on its own title.
    expect(rail).toMatch(/drawing === option\.value && !drawingInForce \? 'opacity-50'/)
    expect(rail).toMatch(/lines for now: this window is wide enough/)
  })

  it('says the coupling on its toggle, now that it no longer says it underneath', () => {
    expect(rail).not.toContain('In the path:')
    expect(rail).not.toContain('Nothing in the path:')
    // The toggle is the state: each option carries its own figure and its own hue, so the lit half
    // says which coupling is in force without a line under it.
    expect(rail).toMatch(/:options="couplingOptions"/)
  })

  /**
   * And the sentence that used to be visible must not be only a tooltip: a title attribute is not
   * in the accessibility tree, so the same words are repeated into a visually hidden span.
   */
  it('keeps the hint readable by something other than a mouse', () => {
    expect(section).toContain(':title="hint"')
    expect(section).toContain('class="sr-only"')
    // On a wrapper, because PanelIcon is aria-hidden and takes no title of its own -- one written
    // at its call site would land on a hidden element.
    expect(section).toMatch(/<span v-if="hint" :title="hint"[\s\S]{0,80}<PanelIcon/)
  })
})

/**
 * The rail's own readout panel, which is facts about the capture and no readings at all.
 *
 * <p>Every figure measured <em>from</em> the record moved to the Selected measurements table under
 * the chart, where a reader picks them from a figure of each. The four that stayed are the ones no
 * measurement can report, because they describe the record rather than the signal in it — so the
 * panel is called <b>Record</b>, which is what it now holds.</p>
 */
describe('the Record panel', () => {
  const panel = read('ScopeMeasurementsPanel.vue')

  it('is named for what it holds', () => {
    expect(panel).toContain('title="Record"')
    expect(panel).not.toContain('title="Measurements"')
  })

  it('keeps the four facts about the capture', () => {
    for (const row of ['>Record<', '>Sampled at<', '>Pre-trigger<', '>Timebase<']) {
      expect(panel, `the panel no longer says "${row}"`).toContain(row)
    }
  })

  it('carries no readings and no controls', () => {
    // The readings are in the catalogue and tickable into the table; the controls are in that
    // table's own header. A second list of either here is one too many.
    for (const gone of [
      'scopeChannelReadings',
      'residualReadings',
      'mathReadings',
      'xyReadings',
      'eyeReadings',
      'useScopeMeasurements',
      'pickMeasurements',
    ]) {
      expect(panel, `the panel still reaches for ${gone}`).not.toContain(gone)
    }
  })

  it('leaves the capture\u2019s own flags to the chart, which already badges them', () => {
    // Spliced, untriggered and the display path are all on the chart's header row. A paragraph
    // that comes and goes above a chart takes its height off the canvas every time it appears.
    // The template half only: the component's own doc block is free to say what moved and why.
    const template = panel.slice(panel.indexOf('<template>'))
    expect(template).not.toContain('spliced')
    expect(template).not.toContain('pathNote')
  })

  it('still carries a hint, like every other panel on this screen', () => {
    expect(panel).toMatch(/hint="[^"]{40,}"/)
  })

  /**
   * The estimate moved here from the Acquisition panel on 2026-11-20, which is the same kind of
   * thing this panel already held — what a capture is, rather than what it reads.
   */
  it('carries the estimate of the next capture as well as the record on screen', () => {
    // As name-and-value rows like the table below it, not badges (Joost, 2026-11-21): two
    // halves that say the same kind of thing read alike, and four pills spent the rail's width
    // on their own chrome rather than on the figures.
    expect(panel).not.toContain('EstimateBadge')
    for (const figure of [
      'staged.recordMs',
      'staged.frames',
      'staged.intervalUs',
      'staged.captureMs',
      'staged.rate',
    ]) {
      expect(panel, `the panel no longer estimates "${figure}"`).toContain(figure)
    }
    // A row of its own, whose label sits on its own line under a long title attribute.
    expect(panel).toMatch(/>\s*Capture\s*</)
    // And it is gone from where it was, or the rail says the same thing twice.
    expect(read('ScopeConfigPanel.vue')).not.toContain('EstimateBadge')
  })

  it('says which half is which, because they are routinely different', () => {
    // Change the rate or the timebase without running again and the estimate moves while the
    // record stays: one heading over each is what stops that reading as a contradiction.
    expect(panel).toContain('>Next capture<')
    expect(panel).toContain('>On screen<')
  })

  it('keeps the analyzer’s own edge caveat, in the hint rather than as a paragraph', () => {
    // Explanation, not state -- the rail's own rule -- so it moves to the hint with the rest.
    expect(panel).toMatch(/hint="[^"]*4 µs[^"]*"/)
    expect(read('ScopeConfigPanel.vue')).not.toContain('reconstruction filter, not the DUT')
  })
})
