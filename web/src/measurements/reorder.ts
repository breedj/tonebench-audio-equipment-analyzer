/**
 * Where a dragged list item ends up. Pure so the drag arithmetic — the part that is easy to get
 * off by one, and impossible to check by eye once it is tangled up in dragover handlers — is
 * testable on its own; MeasurementSidebar.vue only supplies "which row, which half".
 */

/**
 * Moves the item at `from` so it sits at index `to` in the resulting list, shifting the rest along.
 * Returns a copy unchanged for a no-op move or an out-of-range index, so a drop on the row being
 * dragged costs nothing and never has to be special-cased by the caller.
 */
export function moveItem<T>(items: readonly T[], from: number, to: number): T[] {
  const next = items.slice()
  if (from === to) return next
  if (from < 0 || from >= items.length || to < 0 || to >= items.length) return next
  const [moved] = next.splice(from, 1)
  next.splice(to, 0, moved)
  return next
}

/**
 * The destination index for a drop on row `over`, where `after` is true when the pointer is past
 * that row's midpoint. The midpoint split is what makes the last position reachable at all — a row
 * can only ever mean "before me" otherwise, and nothing can be dropped below the final one.
 *
 * Dragging downwards, "after row i" is index i itself and not i + 1, because moveItem lifts the
 * dragged row out before inserting and every row below it has already shifted up by one.
 */
export function dropIndex(from: number, over: number, after: boolean): number {
  const target = after ? over + 1 : over
  return from < target ? target - 1 : target
}
