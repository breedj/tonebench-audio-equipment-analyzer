import type { RecentProject } from '../api/client'

/** Most recently opened first — the server stores them that way, but the welcome screen should not depend on it. */
export function sortRecent(entries: RecentProject[]): RecentProject[] {
  return [...entries].sort((a, b) => Date.parse(b.lastOpenedUtc) - Date.parse(a.lastOpenedUtc))
}

/**
 * "Last opened" as a person would say it: a time for today, "yesterday", a weekday within the
 * week, otherwise the date. `now` and `locale` are parameters so the tests are written against
 * neither the clock nor the machine's language; the UI passes neither.
 */
export function formatLastOpened(iso: string, now: Date = new Date(), locale?: string): string {
  const then = new Date(iso)
  if (Number.isNaN(then.getTime())) return ''

  const time = then.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })
  const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()
  const days = Math.round((startOfDay(now) - startOfDay(then)) / 86_400_000)

  if (days <= 0) return `today ${time}`
  if (days === 1) return `yesterday ${time}`
  if (days < 7) return `${then.toLocaleDateString(locale, { weekday: 'long' })} ${time}`
  return then.toLocaleDateString(locale, { year: 'numeric', month: 'short', day: 'numeric' })
}
