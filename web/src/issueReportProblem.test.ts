import { describe, expect, it } from 'vitest'
import { ApiError } from './api/client'
import { describeIssueReportProblem } from './issueReport'

describe('describeIssueReportProblem', () => {
  /**
   * The failure this exists for, reported 2026-10-08 with a screenshot: the dialog printed the
   * browser's own "Failed to fetch" — a `fetch` that never reached a server — beside a panel
   * reading "0 files" and "About 0 bytes" that the same failure had emptied. Nothing on screen
   * said what had failed or what to do about it.
   */
  it('turns a request that reached no server into the one cause it almost always has', () => {
    const message = describeIssueReportProblem(new TypeError('Failed to fetch'), 'listing')

    expect(message).not.toContain('Failed to fetch')
    expect(message).toContain('Could not read what can go into a report')
    expect(message).toContain('not answering')
    expect(message).toContain('closed or restarted')
  })

  it('names which of the three requests failed', () => {
    const dead = new TypeError('Failed to fetch')

    expect(describeIssueReportProblem(dead, 'building')).toContain('Could not write the report')
    expect(describeIssueReportProblem(dead, 'openingFolder')).toContain('Could not open the reports folder')
  })

  /** Every repository call raises this one when no project is open, so it is worth its own sentence. */
  it('passes a refusal through in the server’s own words', () => {
    const refused = new ApiError(409, 'No project is open. Open or create a project first.', 'GET /api/x failed: 409 ...')

    const message = describeIssueReportProblem(refused, 'building')

    expect(message).toContain('Could not write the report')
    expect(message).toContain('No project is open')
    expect(message).not.toContain('409')
  })

  /**
   * The body, not the Error's message: that one is prefixed with the method, the path and the
   * status for the console's benefit, none of which belongs in a sentence on screen.
   */
  it('quotes the server’s reason and the status for anything else', () => {
    const failed = new ApiError(500, 'The log file is in use.', 'POST /api/issue-report failed: 500 The log file is in use.')

    const message = describeIssueReportProblem(failed, 'building')

    expect(message).toContain('error 500')
    expect(message).toContain('The log file is in use.')
    expect(message).not.toContain('POST /api/issue-report failed')
  })

  it('still says something when there is no reason to quote', () => {
    const silent = new ApiError(503, '', 'GET /api/x failed: 503 ')

    expect(describeIssueReportProblem(silent, 'listing')).toContain('gave no reason')
  })

  it('does not fall over on something that is not an Error at all', () => {
    expect(describeIssueReportProblem('boom', 'listing')).toContain('not answering')
    expect(describeIssueReportProblem(null, 'listing')).toContain('not answering')
  })
})
