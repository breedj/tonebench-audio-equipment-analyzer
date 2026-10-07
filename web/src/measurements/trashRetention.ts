import { commitNumber } from '../config/numberField'

/**
 * How long the trash keeps things, as the Trash view's own field commits it.
 *
 * The bounds mirror `Tonebench.Data.TrashRetentionConfig` — the server rejects anything outside
 * them with a 400, so clamping here is what keeps a typo from becoming an error banner. Whole days
 * only, since that is what the purge compares.
 */
export const TRASH_RETENTION_MIN_DAYS = 1
export const TRASH_RETENTION_MAX_DAYS = 3650
export const TRASH_RETENTION_DEFAULT_DAYS = 14

/**
 * The number a typed retention should be stored as.
 *
 * <p>Zero clamps up to one day rather than being accepted: the trash exists to survive a misclick,
 * and a retention of nothing is not a shorter safety net but none at all — the delete would be as
 * irreversible as it was before the trash existed, while the UI went on offering a restore. An
 * emptied field is the fortnight, not the minimum, because clearing a box means "whatever the
 * normal thing is".</p>
 */
export function commitRetentionDays(raw: string): number {
  return Math.round(
    commitNumber(raw, {
      min: TRASH_RETENTION_MIN_DAYS,
      max: TRASH_RETENTION_MAX_DAYS,
      fallback: TRASH_RETENTION_DEFAULT_DAYS,
    }),
  )
}
