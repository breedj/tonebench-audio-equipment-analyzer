import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

/**
 * <p><b>The Screenshot handler says why it did nothing, rather than returning.</b>
 * `screenshotChart` guards everything after the chart lookup inside one `try` that ends in
 * `reportError`, so every failure it can have — a chart that will not export, a clipboard the
 * browser refuses — reaches the user as a sentence. The chart lookup itself was the one exception:
 * a bare `return`, which writes no file, copies nothing and shows nothing, so the button simply
 * does nothing. That is the one outcome a user can neither act on nor report usefully, and it is
 * the state `chartOnScreen`'s own remark about the decay surfaces ("this must not go null") was
 * already written against.</p>
 *
 * <p>It is reachable on the impulse screen, whose decay branch returns whichever of two charts is
 * mounted, and not on the FFT screen, where `SpectrumChart` is rendered unconditionally — so this
 * is hardening rather than the fix for a reproduced fault. It is pinned because nothing else would
 * notice it being simplified back: the refusal is one line in a function whose every other exit is
 * already covered, and a silent `return` reads as tidier than the branch it replaces.</p>
 *
 * <p>App.vue is a `.vue`, and nothing in a `.vue` is testable under the node environment vitest
 * runs in, so this reads it as text — the technique `measurements/storedDisplayDefaults.test.ts`
 * and `charts/annotationNumbersDefault.test.ts` already use, and for their reason: the thing being
 * checked is a fact about the source rather than about any value this process can compute.</p>
 */
const appSource = readFileSync(resolve(import.meta.dirname, 'App.vue'), 'utf8')

/** `screenshotChart`'s body, up to the `try` that covers the rest of it. */
function screenshotPreamble(): string {
  const start = appSource.indexOf('async function screenshotChart(')
  expect(start, 'screenshotChart is gone or renamed').toBeGreaterThan(-1)
  const tryAt = appSource.indexOf('\n  try {', start)
  expect(tryAt, 'screenshotChart no longer guards its body in a try').toBeGreaterThan(start)
  return appSource.slice(start, tryAt)
}

describe('the Screenshot button refusing', () => {
  it('reports when there is no chart to export, instead of returning silently', () => {
    const preamble = screenshotPreamble()
    expect(preamble).toContain('chartOnScreen()')
    expect(preamble, 'a chart lookup that fails must say so').toContain('reportError(')
    // The shape that was there, and the one worth failing on: a lookup answered by nothing at all.
    expect(preamble.replace(/\s+/g, ' ')).not.toContain('if (!chart) return')
  })

  it('still reaches the user through the one error seam the rest of the handler uses', () => {
    // Not an alert, not a console line, not a second notice of its own: the status bar's error
    // line is where every other screenshot failure already lands, so a refusal that chose its own
    // channel would be the one message a user has to look somewhere else for.
    expect(appSource).toContain('function reportError(')
  })
})
