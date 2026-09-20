/**
 * How a date and time is written wherever the app puts one on paper: the Screenshot's date line,
 * a report's "Taken" row and its "Generated" line. Configuration ▸ General ▸ "Date and time format"
 * picks one of these; the id is what the server stores (`ui.dateTimeFormat`, see
 * useDateTimeFormat), so renaming an id orphans every stored choice — add, don't rename.
 *
 * `system` is what the app always did: `toLocaleString()` in the browser's locale, which on the
 * same bench PC follows the Windows region settings. The fixed formats exist because a document
 * that travels — a report sent to a customer, a screenshot pasted into a forum post — is read in
 * *someone else's* locale, where `09/10/2026` is ambiguous and the ISO order is not.
 *
 * Pure: the formatting is arithmetic on the local-time fields, so it is unit-tested without a
 * browser (dateTime.test.ts). Local time, not UTC — a bench log says when the measurement was
 * taken by the clock on the wall.
 */

export type DateTimeFormatId =
  | 'system'
  | 'iso'
  | 'iso-seconds'
  | 'dmy'
  | 'dmy-seconds'
  | 'mdy'
  | 'dmy-text'
  | 'mdy-text'

export interface DateTimeFormatOption {
  id: DateTimeFormatId
  /** What the Configuration dropdown shows — the pattern, since the example beside it is live. */
  label: string
}

export const DATE_TIME_FORMATS: readonly DateTimeFormatOption[] = [
  { id: 'system', label: 'System (browser locale)' },
  { id: 'iso', label: 'YYYY-MM-DD HH:mm' },
  { id: 'iso-seconds', label: 'YYYY-MM-DD HH:mm:ss' },
  { id: 'dmy', label: 'DD-MM-YYYY HH:mm' },
  { id: 'dmy-seconds', label: 'DD-MM-YYYY HH:mm:ss' },
  { id: 'mdy', label: 'MM/DD/YYYY h:mm AM/PM' },
  { id: 'dmy-text', label: 'D Mon YYYY HH:mm' },
  { id: 'mdy-text', label: 'Mon D, YYYY h:mm AM/PM' },
]

export const DEFAULT_DATE_TIME_FORMAT: DateTimeFormatId = 'system'

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** A stored id, or anything else, as a format this build knows — an unknown id reads as the system locale. */
export function toDateTimeFormatId(value: unknown): DateTimeFormatId {
  return DATE_TIME_FORMATS.some((f) => f.id === value) ? (value as DateTimeFormatId) : DEFAULT_DATE_TIME_FORMAT
}

function pad2(n: number): string {
  return n < 10 ? `0${n}` : String(n)
}

function twelveHour(hours: number): { h: number; suffix: string } {
  const suffix = hours < 12 ? 'AM' : 'PM'
  const h = hours % 12 === 0 ? 12 : hours % 12
  return { h, suffix }
}

/**
 * `date` written in `format`, in local time. An invalid date comes back as an empty string rather
 * than "Invalid Date" or "NaN-NaN-NaN" — the callers put the result on paper.
 */
export function formatDateTime(date: Date, format: DateTimeFormatId): string {
  if (Number.isNaN(date.getTime())) return ''
  if (format === 'system') return date.toLocaleString()

  const yyyy = String(date.getFullYear())
  const mm = pad2(date.getMonth() + 1)
  const dd = pad2(date.getDate())
  const hh = pad2(date.getHours())
  const mi = pad2(date.getMinutes())
  const ss = pad2(date.getSeconds())
  const { h, suffix } = twelveHour(date.getHours())
  const mon = MONTHS[date.getMonth()]

  switch (format) {
    case 'iso':
      return `${yyyy}-${mm}-${dd} ${hh}:${mi}`
    case 'iso-seconds':
      return `${yyyy}-${mm}-${dd} ${hh}:${mi}:${ss}`
    case 'dmy':
      return `${dd}-${mm}-${yyyy} ${hh}:${mi}`
    case 'dmy-seconds':
      return `${dd}-${mm}-${yyyy} ${hh}:${mi}:${ss}`
    case 'mdy':
      return `${mm}/${dd}/${yyyy} ${h}:${mi} ${suffix}`
    case 'dmy-text':
      return `${date.getDate()} ${mon} ${yyyy} ${hh}:${mi}`
    case 'mdy-text':
      return `${mon} ${date.getDate()}, ${yyyy} ${h}:${mi} ${suffix}`
  }
}
