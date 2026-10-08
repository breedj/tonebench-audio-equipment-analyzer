/**
 * The **Report an issue** dialog's vocabulary: the questions it asks, and the arithmetic it shows.
 *
 * Here rather than in the `.vue` for the reason every other pure module in this app is: nothing in
 * a `.vue` file is testable under the node environment vitest runs in, and two things here are
 * worth a test. The question list is *data* the server prints verbatim into `report.md` — it travels
 * with each answer rather than being looked up server-side, so a question with a duplicate id would
 * silently overwrite another's answer in `report.json`. And the size estimate is what a user decides
 * by: the recordings box can turn a 300 kB report into a 500 MB one, and a figure that is wrong in
 * the reassuring direction is worse than none.
 */

export interface IssueQuestion {
  /** The key this answer is filed under in `report.json`. Stable — it is what a later reader greps. */
  id: string
  /** What the user reads, and what the server prints as the heading above the answer. */
  question: string
  /** The grey prompt inside the empty box. A worked example rather than a restatement of the question. */
  placeholder: string
  /** Shown with a nudge rather than enforced: a half-filled report that gets sent beats a complete one that does not. */
  wanted?: boolean
  /** How tall the box starts. One line for short factual answers, several for the narrative ones. */
  rows: number
}

/**
 * Six questions, in the order the answers are useful to read in.
 *
 * The first two are split deliberately: "what happened" and "what did you expect" are the pair that
 * separates a fault from a misunderstanding of a setting, and a single "describe the problem" box
 * reliably gets only the first half. The steps question asks for a repeat rather than a description,
 * because every fault in this app that was fixed quickly was one that could be reproduced on
 * another bench. "Every time or now and then" is here because the honest answer is usually "now and
 * then", which is itself the most important fact about a fault: it points at a race, a GC pause or a
 * drop rather than at arithmetic. And the wiring question earns its place from experience — two of
 * the longest diagnoses on this bench were unterminated − inputs and a second browser tab.
 */
export const ISSUE_QUESTIONS: readonly IssueQuestion[] = [
  {
    id: 'whatHappened',
    question: 'What happened?',
    placeholder: 'The THD reading jumped to 4 % on the first point of every sweep, then settled.',
    wanted: true,
    rows: 3,
  },
  {
    id: 'whatExpected',
    question: 'What did you expect instead?',
    placeholder: 'All the points to read about the same, the way they did yesterday.',
    wanted: true,
    rows: 2,
  },
  {
    id: 'steps',
    question: 'What were you doing, step by step, so it can be repeated here?',
    placeholder:
      '1. Picked the sound card and set 96 kHz.\n2. Pressed RUN SWEEP on THD vs frequency.\n3. Looked at point 1.',
    wanted: true,
    rows: 4,
  },
  {
    id: 'howOften',
    question: 'Every time, or now and then?',
    placeholder: 'Every time, if I change the sample rate first. Never if I just press RUN twice.',
    rows: 2,
  },
  {
    id: 'bench',
    question: 'What is connected — the analyzer, the equipment under test, the cables and loads?',
    placeholder:
      'MOTU M4 over ASIO, 10:1 divider on the left input, EL84 push-pull amplifier into an 8 Ω dummy load.',
    rows: 3,
  },
  {
    id: 'anythingElse',
    question: 'Anything else? Leave your name or e-mail if you would like a reply.',
    placeholder: 'It started after I updated the card’s driver. — Joost',
    rows: 2,
  },
]

/** Bytes as a person reads them. The same thresholds and wording the server's own summary uses. */
export function describeBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '—'
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} kB`
  return `${bytes} bytes`
}

export interface IssueReportFile {
  name: string
  bytes: number
  modifiedUtc?: string | null
}

export interface IssueReportContents {
  logs: IssueReportFile[]
  diagnostics: IssueReportFile[]
  recordings: IssueReportFile[]
  settingsBytes: number
  streamRecordingEnabled: boolean
  diagnosticLoggingEnabled: boolean
  reportsDirectory: string
}

export interface IssueReportSelection {
  logDays: number
  includeScreenshot: boolean
  includeDiagnostics: boolean
  includeRecordings: boolean
  includeSettings: boolean
}

/**
 * Roughly how big the zip will be, from what the server said is on disk.
 *
 * <p>Deliberately an <b>over</b>-estimate: it adds the files up uncompressed, where logs and JSON
 * deflate to a fraction and the WAVs barely compress at all. A user deciding whether to tick the
 * recordings box is choosing between "a mail attachment" and "something to put on a file-sharing
 * service", and the figure that keeps that decision right is the pessimistic one. The newest
 * recording <i>set</i> only, matching what the server copies — all ten sets would read as several
 * gigabytes for a box that sends a few hundred megabytes.</p>
 */
export function estimateReportBytes(
  contents: IssueReportContents,
  selection: IssueReportSelection,
  screenshotBytes = 0,
): number {
  let total = 8 * 1024 // report.md and report.json, which are a few kB of text at most.

  if (selection.includeScreenshot) total += screenshotBytes
  if (selection.includeSettings) total += Math.max(4 * 1024, contents.settingsBytes)

  for (const log of logsWithin(contents.logs, selection.logDays)) total += log.bytes
  if (selection.includeDiagnostics) total += sum(contents.diagnostics)
  if (selection.includeRecordings) total += sum(newestRecordingSet(contents.recordings))

  return total
}

/**
 * The newest *set* of recording files: one acquisition's input WAV, output WAV and JSON sidecar.
 *
 * <p>A set shares its name up to the `-in` / `-out` suffix, which is the server's own rule
 * (`IssueReportService.CopyNewestRecordingSet`) — so this agrees with what will actually be copied
 * rather than guessing. The list arrives newest first, so the first entry names the set.</p>
 */
export function newestRecordingSet(recordings: readonly IssueReportFile[]): IssueReportFile[] {
  if (recordings.length === 0) return []
  const wanted = recordingStem(recordings[0].name)
  return recordings.filter((f) => recordingStem(f.name) === wanted)
}

function recordingStem(name: string): string {
  const bare = name.replace(/\.[^.]*$/, '')
  return bare.replace(/-(in|out)$/i, '')
}

/**
 * The log files inside the chosen window, counting back from today.
 *
 * <p>By the *name*, not by the modification time the server reports. A log's name carries the day it
 * covers (`tonebench-20261004.log`) and its mtime is when it was last written — which for a day the
 * app was not started is some other day entirely, and for today's file keeps moving while the
 * dialog is open. The server filters by mtime because that is what it has cheaply; this is the
 * figure a user reads, so it uses the one that cannot drift.</p>
 */
export function logsWithin(logs: readonly IssueReportFile[], days: number): IssueReportFile[] {
  if (days <= 0) return []

  const oldest = new Date()
  oldest.setHours(0, 0, 0, 0)
  oldest.setDate(oldest.getDate() - (days - 1))

  return logs.filter((log) => {
    const day = logDate(log.name)
    return day === null || day >= oldest
  })
}

/** The day a log file's name says it covers, or null for a name that is not one of ours. */
export function logDate(name: string): Date | null {
  const match = /tonebench-(\d{4})(\d{2})(\d{2})\.log$/i.exec(name)
  if (!match) return null
  return new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
}

function sum(files: readonly IssueReportFile[]): number {
  return files.reduce((total, file) => total + file.bytes, 0)
}

/**
 * Every question answered, in the shape the server stores. Blank answers are kept rather than
 * dropped: `report.md` prints "(not answered)" under them, and a question visibly left empty says
 * something a missing heading does not — it says it was asked.
 */
export function answersFor(values: Record<string, string>): { id: string; question: string; answer: string }[] {
  return ISSUE_QUESTIONS.map((q) => ({ id: q.id, question: q.question, answer: values[q.id] ?? '' }))
}

/** Whether there is enough here to be worth sending: the first question, at least. */
export function hasSomethingToReport(values: Record<string, string>): boolean {
  return ISSUE_QUESTIONS.some((q) => (values[q.id] ?? '').trim().length > 0)
}

/** Which request failed, so the message can say what was being attempted rather than only that it was. */
export type IssueReportStep = 'listing' | 'building' | 'openingFolder'

/**
 * A failure the dialog can show, in words that name what went wrong.
 *
 * <p>It used to print `Error.message` straight through, and for the commonest failure of all that
 * is the browser's own <b>"Failed to fetch"</b> — the wording `fetch` uses when the request never
 * reached a server at all. On screen it said nothing about what had failed, what had been
 * attempted, or what to do, next to a panel reading "0 files" and "About 0 bytes" that had been
 * emptied by the same failure (reported 2026-10-08, with a screenshot of exactly that). Here the
 * app is served by the app, so "no server" has one overwhelmingly likely cause: the Tonebench
 * window was closed, restarted or rebuilt while this tab stayed open.</p>
 *
 * <p>A 409 is worth its own sentence for the same reason — every repository call raises it when no
 * project is open — and anything the server did answer with keeps its own text, since that is the
 * part a developer reading the report will need.</p>
 */
export function describeIssueReportProblem(error: unknown, step: IssueReportStep): string {
  const attempt: Record<IssueReportStep, string> = {
    listing: 'Could not read what can go into a report',
    building: 'Could not write the report',
    openingFolder: 'Could not open the reports folder',
  }

  const status = errorStatus(error)
  if (status === null) {
    return (
      `${attempt[step]}: the Tonebench app is not answering. ` +
      'It has probably been closed or restarted since this page was opened — start it again and reload this page.'
    )
  }

  const reason = serverReason(error)
  if (status === 409) return `${attempt[step]}: ${reason || 'the app refused the request.'}`
  return `${attempt[step]} (error ${status}): ${reason || 'the app gave no reason.'}`
}

/** The HTTP status an ApiError carries, or null when the request never got an answer at all. */
function errorStatus(error: unknown): number | null {
  const status = (error as { status?: unknown } | null)?.status
  return typeof status === 'number' ? status : null
}

/**
 * What the server itself said: an ApiError's `body`, and only that. Never its `message`, which is
 * prefixed with the method, the path and the status for the console's benefit — none of which
 * belongs in a sentence on screen, and an answer with an empty body is better described by the
 * caller's own "gave no reason" than by leaking that prefix.
 */
function serverReason(error: unknown): string {
  const body = (error as { body?: unknown } | null)?.body
  return typeof body === 'string' ? body.trim() : ''
}
