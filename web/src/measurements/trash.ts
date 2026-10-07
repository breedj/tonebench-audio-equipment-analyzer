import type { ConfirmOptions } from '../composables/useConfirm'
import type { TrashListing, TrashResultEntry, TrashTypeEntry } from '../api/client'

/**
 * The Trash view's rows, and the questions asked before anything in it is destroyed.
 *
 * <p>Pure, and its own module, because nothing in a `.vue` is testable under the node environment
 * vitest runs in — and the two things worth testing here are both easy to get subtly wrong: which
 * rows may be restored, and whether the wording of a permanent delete tells the truth.</p>
 */

/** A measurement in the trash, or a measurement still in use whose results are. */
export interface TrashGroup {
  measurementTypeId: number
  name: string
  kind: string
  /** Null for a measurement that is still there — the view draws it as a header, not an item. */
  deletedUtc: string | null
  /** True when restoring the measurement would also bring results back, and how many. */
  restorableResultCount: number
  canRestore: boolean
  /** Why not, for a tooltip beside a disabled Restore. Null when it can be restored. */
  restoreBlockedReason: string | null
  results: TrashItem[]
}

export interface TrashItem {
  resultId: number
  name: string
  notes: string | null
  timestampUtc: string
  deletedUtc: string
  canRestore: boolean
  restoreBlockedReason: string | null
}

/**
 * Turns the server's listing into the view's rows, deciding what may be restored.
 *
 * <p>Two rules, and they are the user's own: a measurement that is still there is not itself in the
 * trash and so cannot be restored; and a result can only come back while its measurement exists,
 * because the history list hides a result whose measurement is trashed — restoring it under one
 * would read as a restore that silently did nothing.</p>
 */
export function toTrashGroups(listing: TrashListing): TrashGroup[] {
  return listing.types.map((type) => ({
    measurementTypeId: type.measurementTypeId,
    name: type.name,
    kind: type.kind,
    deletedUtc: type.deletedUtc,
    restorableResultCount: type.restorableResultCount,
    canRestore: type.deletedUtc !== null && !type.nameTaken,
    restoreBlockedReason: typeRestoreBlockedReason(type),
    results: type.results.map((result) => toTrashItem(result, type)),
  }))
}

function typeRestoreBlockedReason(type: TrashTypeEntry): string | null {
  if (type.deletedUtc === null) return 'This measurement is not deleted — only the results listed under it are.'
  if (type.nameTaken) return `Another measurement is called "${type.name}". Rename one of them first.`
  return null
}

function toTrashItem(result: TrashResultEntry, type: TrashTypeEntry): TrashItem {
  const parentInTrash = type.deletedUtc !== null
  return {
    resultId: result.resultId,
    name: result.name,
    notes: result.notes,
    timestampUtc: result.timestampUtc,
    deletedUtc: result.deletedUtc,
    canRestore: !parentInTrash,
    restoreBlockedReason: parentInTrash
      ? `Its measurement "${type.name}" is in the trash too. Restore that first.`
      : null,
  }
}

/** What the sidebar's badge shows. 0 hides it rather than showing a zero. */
export function trashCount(listing: TrashListing | null): number {
  return listing?.totalCount ?? 0
}

/**
 * Empty Trashcan. This is one of the two places in the app where "This cannot be undone." is still
 * true, so it is still said — and the counts are named, because the view is a list of rows and
 * nothing in it adds up the measurements and the results for you.
 */
export function emptyTrashConfirmation(listing: TrashListing): ConfirmOptions {
  const groups = listing.types.filter((t) => t.deletedUtc !== null).length
  const results = listing.types.reduce((sum, t) => sum + t.results.length, 0)

  return {
    title: 'Empty trash',
    message: `Permanently delete ${describeContents(groups, results)}?\n\nThis cannot be undone.`,
    confirmLabel: 'Empty trash',
    danger: true,
  }
}

/** A single row's permanent delete, which is the other one. */
export function deletePermanentlyConfirmation(name: string, kind: 'measurement' | 'result'): ConfirmOptions {
  const tail =
    kind === 'measurement'
      ? ' Every result saved under it goes too.'
      : ''

  return {
    title: 'Delete permanently',
    message: `Permanently delete the ${kind} "${name}"?${tail}\n\nThis cannot be undone.`,
    confirmLabel: 'Delete permanently',
    danger: true,
  }
}

/** "2 measurements and 1 result", with each half left out when it is zero. */
function describeContents(measurements: number, results: number): string {
  const parts: string[] = []
  if (measurements > 0) parts.push(plural(measurements, 'measurement'))
  if (results > 0) parts.push(plural(results, 'result'))
  if (parts.length === 0) return 'everything in the trash'
  return parts.join(' and ')
}

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? '' : 's'}`
}
