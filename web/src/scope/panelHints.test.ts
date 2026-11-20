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
    for (const line of [
      // The view that cannot be drawn with one input.
      'Nothing to draw: an X–Y figure needs both inputs',
      // The drawing mode that has fallen back because the slice is decimated.
      'Lines for now: this window is wide enough',
      // What is in the signal path at the moment.
      'In the path: {{ pathNote }}',
      'Nothing in the path:',
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
})
