import { describe, expect, it } from 'vitest'
import {
  ISSUE_QUESTIONS,
  answersFor,
  describeBytes,
  estimateReportBytes,
  hasSomethingToReport,
  logDate,
  logsWithin,
  newestRecordingSet,
  type IssueReportContents,
  type IssueReportFile,
} from './issueReport'

function file(name: string, bytes: number): IssueReportFile {
  return { name, bytes }
}

const CONTENTS: IssueReportContents = {
  logs: [file('tonebench-20261004.log', 200 * 1024), file('tonebench-20261003.log', 100 * 1024)],
  diagnostics: [file('capture-1.csv', 50 * 1024)],
  recordings: [
    file('acq-20261004-120000-in.wav', 40 * 1024 * 1024),
    file('acq-20261004-120000-out.wav', 40 * 1024 * 1024),
    file('acq-20261004-120000.json', 2 * 1024),
    file('acq-20261004-110000-in.wav', 90 * 1024 * 1024),
  ],
  settingsBytes: 300 * 1024,
  streamRecordingEnabled: true,
  diagnosticLoggingEnabled: true,
  reportsDirectory: 'C:\\Users\\x\\AppData\\Local\\Tonebench\\issue-reports',
}

const NOTHING = {
  logDays: 0,
  includeScreenshot: false,
  includeDiagnostics: false,
  includeRecordings: false,
  includeSettings: false,
}

describe('the question list', () => {
  /**
   * The ids are the keys answers are filed under in report.json, so a duplicate silently overwrites
   * another question's answer — and the report then shows one question that was never asked and
   * loses one that was. Nothing else in the app would notice.
   */
  it('has a unique id for every question', () => {
    const ids = ISSUE_QUESTIONS.map((q) => q.id)
    expect(new Set(ids).size).toBe(ids.length)
  })

  // Each heading has to read as something to answer rather than as a label: the server prints it
  // verbatim above the answer in report.md, where "Bench" would leave the reader guessing what was
  // asked. A question mark anywhere in it is the cheapest test of that ("Anything else? Leave your
  // name..." is a question with an instruction after it).
  it('asks every question as a question, and offers an example under each', () => {
    for (const q of ISSUE_QUESTIONS) {
      expect(q.question).toContain('?')
      expect(q.placeholder.length).toBeGreaterThan(10)
      expect(q.rows).toBeGreaterThan(0)
    }
  })

  /**
   * Blank answers travel too. report.md prints "(not answered)" under them, which says the question
   * was asked and skipped — a missing heading cannot say that, and the maintainer would not know
   * whether to ask again.
   */
  it('sends every question even when it was left blank', () => {
    const answers = answersFor({ whatHappened: 'it broke' })

    expect(answers).toHaveLength(ISSUE_QUESTIONS.length)
    expect(answers.find((a) => a.id === 'whatHappened')?.answer).toBe('it broke')
    expect(answers.find((a) => a.id === 'steps')?.answer).toBe('')
    expect(answers.every((a) => a.question.length > 0)).toBe(true)
  })

  it('knows when there is nothing worth sending', () => {
    expect(hasSomethingToReport({})).toBe(false)
    expect(hasSomethingToReport({ whatHappened: '   ' })).toBe(false)
    expect(hasSomethingToReport({ bench: 'MOTU M4' })).toBe(true)
  })
})

describe('the newest recording set', () => {
  /**
   * The server copies one acquisition's files and no more, grouping by the name with `-in`/`-out`
   * removed. This has to group the same way or the size shown is for a different selection than the
   * one made — and the error is in the reassuring direction, which is the one that matters.
   */
  it('is the input, the output and the sidecar of one acquisition', () => {
    const set = newestRecordingSet(CONTENTS.recordings).map((f) => f.name)

    expect(set).toEqual([
      'acq-20261004-120000-in.wav',
      'acq-20261004-120000-out.wav',
      'acq-20261004-120000.json',
    ])
  })

  it('is empty when nothing has been recorded', () => {
    expect(newestRecordingSet([])).toEqual([])
  })
})

describe('the log window', () => {
  /**
   * Read from the *name*, which carries the day the file covers, not from the modification time —
   * which for today's still-open file keeps moving and for a day the app was not started belongs to
   * some other day entirely.
   */
  it('reads the day out of the file name', () => {
    expect(logDate('tonebench-20261004.log')).toEqual(new Date(2026, 9, 4))
    expect(logDate('something-else.txt')).toBeNull()
  })

  it('keeps nothing at zero days', () => {
    expect(logsWithin(CONTENTS.logs, 0)).toEqual([])
  })

  it('keeps a file whose name is not one of ours, rather than silently dropping it', () => {
    const odd = [file('archived.log', 10)]
    expect(logsWithin(odd, 1)).toHaveLength(1)
  })

  it('keeps today alone at one day', () => {
    const today = `tonebench-${new Date().toISOString().slice(0, 10).replace(/-/g, '')}.log`
    const logs = [file(today, 10), file('tonebench-20200101.log', 10)]

    expect(logsWithin(logs, 1).map((f) => f.name)).toEqual([today])
  })
})

describe('the size estimate', () => {
  it('is a few kB with everything unticked', () => {
    expect(estimateReportBytes(CONTENTS, NOTHING)).toBe(8 * 1024)
  })

  /**
   * The recordings box is the whole reason this estimate exists: it is the difference between a mail
   * attachment and something that needs a file-sharing service, and the user has to see that before
   * the zip is built rather than after.
   */
  it('jumps by the newest recording set when the recordings box is ticked', () => {
    const without = estimateReportBytes(CONTENTS, { ...NOTHING, logDays: 2 })
    const withWavs = estimateReportBytes(CONTENTS, { ...NOTHING, logDays: 2, includeRecordings: true })

    expect(withWavs - without).toBe(40 * 1024 * 1024 + 40 * 1024 * 1024 + 2 * 1024)
    // And not the older set beside it, which would read as 170 MB for a 80 MB selection.
    expect(withWavs - without).toBeLessThan(90 * 1024 * 1024)
  })

  it('counts the screenshot it is given', () => {
    const base = estimateReportBytes(CONTENTS, NOTHING)
    expect(estimateReportBytes(CONTENTS, { ...NOTHING, includeScreenshot: true }, 120_000)).toBe(base + 120_000)
  })
})

describe('describeBytes', () => {
  it.each([
    [0, '0 bytes'],
    [512, '512 bytes'],
    [2048, '2.0 kB'],
    [5 * 1024 * 1024, '5.0 MB'],
    [3 * 1024 ** 3, '3.0 GB'],
  ])('writes %i as %s', (bytes, expected) => {
    expect(describeBytes(bytes)).toBe(expected)
  })

  it('does not invent a figure for a nonsensical one', () => {
    expect(describeBytes(Number.NaN)).toBe('—')
    expect(describeBytes(-1)).toBe('—')
  })
})
