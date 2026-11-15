import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * The Repair button, which is the only recovery action this app offers.
 *
 * <p>Read as text because it lives in a `.vue`, which is not mountable under this suite's node
 * environment — the technique `splitNarrowing.test.ts` and `settingsInEffect.test.ts` use. What is
 * pinned here is the <em>wiring</em>: the arithmetic and the wording are in
 * `databaseProblemAdvice` and on the server, both of which are tested directly.</p>
 *
 * <p>It exists because the message it sits on had no action at all until 2026-11-15, and was
 * reported from a bench in those words. A button that stops being offered, or an outcome line that
 * stops being shown, puts the user straight back there — and neither failure is visible until
 * somebody's database is already damaged, which is the worst possible moment to find out.</p>
 */
const source = readFileSync(
  fileURLToPath(new URL('../components/DeviceStatusBar.vue', import.meta.url)),
  'utf8',
)
  .replace(/^﻿/, '')
  .replace(/\r\n/g, '\n')

describe('the damaged-database line', () => {
  it('says what is at stake rather than only what SQLite said', () => {
    // Both halves come from one place, so the message and the offer cannot disagree about which
    // file is damaged.
    expect(source).toContain('databaseProblemAdvice(props.status)')
    expect(source).toContain('{{ databaseAdvice.problem }}')
    expect(source).toContain('{{ databaseAdvice.advice }}')
  })

  it('offers the repair only where there is one', () => {
    const button = source.slice(source.indexOf('@click="repairDatabase"'))
    expect(button, 'the button exists').not.toBe('')

    const open = source.lastIndexOf('<button', source.indexOf('@click="repairDatabase"'))
    const tag = source.slice(open, source.indexOf('>', open))
    expect(tag).toContain('v-if="databaseAdvice.canRepair"')
    expect(tag).toContain(':disabled="repairing"')
  })

  /**
   * `DatabaseHealth.ConfigProblem` is set once at startup and never re-read, so the server clears
   * it on a successful rebuild and the next poll drops the message. The outcome line therefore has
   * to outlive the message — keyed on the result, never on the problem still being there, or a
   * repair that worked reads as a button that did nothing.
   */
  it('keeps the outcome after the message it replaced has gone', () => {
    const outcome = source.indexOf('{{ repairResult.summary }}')
    expect(outcome, 'the summary is rendered').toBeGreaterThan(-1)

    const open = source.lastIndexOf('<p', outcome)
    const start = source.indexOf('v-if="', open) + 'v-if="'.length
    expect(source.slice(start, source.indexOf('"', start))).toBe('repairResult')
  })

  it('shows the server its own sentence rather than rebuilding one from the numbers', () => {
    // Three counts and a file name assembled per screen is how two screens come to describe one
    // repair differently; the server builds it where the facts are.
    expect(source).toContain('{{ repairResult.summary }}')
    expect(source).not.toContain('repairResult.rowsRecovered')
  })

  it('says when the repair itself failed, rather than leaving the press unanswered', () => {
    expect(source).toContain('{{ repairError }}')
  })
})
