import type { ConfirmOptions } from '../composables/useConfirm'

/**
 * The question asked before a measurement and its whole stored history are deleted.
 *
 * <p>Pure, and its own module, because the wording is the safeguard: the row in the sidebar shows a
 * name and a kind icon and says nothing about the results stored under it, so the count is the only
 * part of what the delete takes away that the user can see before agreeing to it. Until 2026-10-02
 * the ✕ deleted both with no question at all, and a stray click took the "Amp 1 kHz at 1 W" type and
 * its five stored results out of a project file.</p>
 *
 * <p>Since the trash exists the delete is recoverable, so the dialog no longer says it cannot be
 * undone — it says where the measurement went instead. The question stays, though: the row is narrow
 * and a mis-click should not make a measurement disappear from the sidebar with nothing on screen
 * explaining it.</p>
 *
 * <p>`resultCount` null means the count could not be read — a failed or refused request, or no
 * project open. That is deliberately <em>not</em> reported as zero: "it has no saved results" is
 * the one sentence here that would talk someone into the delete, so an unknown count says so and
 * warns instead.</p>
 *
 * <p>`retentionDays` null means the retention setting has not been read yet, which is a timing
 * accident rather than a state — the sentence then promises a restore without naming a deadline it
 * cannot vouch for.</p>
 */
export function deleteTypeConfirmation(
  name: string,
  resultCount: number | null,
  retentionDays: number | null = null,
): ConfirmOptions {
  return {
    title: 'Delete measurement',
    message: `Delete "${name}"? ${resultsClause(resultCount)}\n\n${trashClause(retentionDays)}`,
    confirmLabel: 'Delete',
    danger: true,
  }
}

/** What the dialog says about the stored results, which is the part the sidebar row does not show. */
function resultsClause(resultCount: number | null): string {
  if (resultCount === null)
    return 'Every result saved under it goes to the trash with it — how many could not be read, so there may be some.'
  if (resultCount <= 0) return 'It has no saved results.'
  if (resultCount === 1) return 'The 1 result saved under it goes to the trash with it.'
  return `All ${resultCount} results saved under it go to the trash with it.`
}

/** Where it goes, and for how long. Named days where they are known — a deadline is the useful part. */
function trashClause(retentionDays: number | null): string {
  if (retentionDays === null) return 'It goes to the trash, where it can be restored.'
  if (retentionDays === 1) return 'It goes to the trash, where it can be restored for 1 day.'
  return `It goes to the trash, where it can be restored for ${retentionDays} days.`
}
