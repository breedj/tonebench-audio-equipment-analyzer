/**
 * Where each panel of a rail sits once the user has dragged them about.
 *
 * <p><b>Why this is arithmetic on `order` rather than a reordered list of components.</b> A
 * measurement screen's rails are template markup, not a list: the settings rail is a run of
 * per-screen components and the display rail is one component whose root is <code>contents</code>,
 * so its sections are the rail's own children. Neither can be sorted from outside without taking
 * the markup apart. What <em>can</em> be sorted is the CSS <code>order</code> of the rail's flex
 * children — which is also the one way to reorder them that moves no DOM node, so nothing is
 * remounted, no chart loses its uPlot instance, no open panel closes and no half-typed box is
 * re-stamped. The rail reads the stored list, works out a number per child, and writes it; that is
 * the whole mechanism.</p>
 *
 * <p>Here rather than in the component for this project's usual reason: nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in, and every rule below is
 * the kind that is quietly wrong rather than visibly broken.</p>
 */

/** The two rails a measurement screen has. The stored document keys by these. */
export type PanelRailId = 'settings' | 'display'

/**
 * A panel's id from its heading, for the panels that declare none.
 *
 * <p>Stable for a fixed title, which is what nearly every panel has. A panel whose title is
 * <em>computed</em> — "dB range" against "Distortion range", "Time window (ms)" against "(µs)" —
 * must declare an id of its own, or its stored place is lost the moment the setting that spells
 * its name is touched.</p>
 */
export function panelSlug(title: string): string {
  return title
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}

/**
 * The order value for each child of a rail, given the ids it holds and the order that was stored.
 *
 * <p><code>ids</code> is one entry per child in DOM order, <code>null</code> for a child that is
 * not a panel — the run row, the Autoset button. Those are <b>not</b> reordered: each keeps the
 * place the screen gives it, and the panels are permuted among the slots that are left. That is
 * what stops a drag sending Run/Single/Stop into the middle of the rail.</p>
 *
 * <p>A panel the stored list does not name — one this build added, or one that was hidden behind a
 * <code>v-if</code> when the list was written — <b>stays with the panel above it</b> rather than
 * going to the end. It is the answer that surprises least: a section that appears when a checkbox
 * is ticked appears where it always did.</p>
 */
export function panelOrders(
  ids: readonly (string | null)[],
  stored: readonly string[],
): number[] {
  const slots: number[] = []
  for (let i = 0; i < ids.length; i++) if (ids[i] !== null) slots.push(i)

  // Sort key per panel: its place in the stored list, or — for an id nobody has ordered — just
  // past the one before it, so it does not jump to either end.
  const keys = new Map<string, number>()
  let previous = -1
  for (const slot of slots) {
    const id = ids[slot] as string
    const known = stored.indexOf(id)
    const key = known >= 0 ? known : previous + 1e-3
    keys.set(id, key)
    previous = key
  }

  const sorted = slots
    .map((slot, index) => ({ slot, index, key: keys.get(ids[slot] as string) ?? 0 }))
    // Stable: two panels that share a key (an id listed twice, or two unknowns off the same
    // neighbour) keep the order the screen gave them.
    .sort((a, b) => (a.key === b.key ? a.index - b.index : a.key - b.key))

  const orders = ids.map((_, index) => index)
  sorted.forEach((entry, position) => {
    orders[entry.slot] = slots[position]
  })
  return orders
}

/**
 * The list to store after a drag: every panel the rail is showing, top first.
 *
 * <p>The whole visible list rather than only what moved, so the document always describes a rail
 * that exists — the alternative is a list of two ids that says nothing about the eight around
 * them.</p>
 */
export function railOrderAfterDrag(
  ids: readonly (string | null)[],
  orders: readonly number[],
): string[] {
  return ids
    .map((id, index) => ({ id, order: orders[index] ?? index }))
    .filter((entry): entry is { id: string; order: number } => entry.id !== null)
    .sort((a, b) => a.order - b.order)
    .map((entry) => entry.id)
}

/**
 * The stored order with one panel moved to where it was dropped.
 *
 * <p>Expressed on the <em>panel</em> list rather than on the rail's children, because that is what
 * is stored and what <code>moveItem</code>'s indices mean. The caller supplies the ids in the order
 * they are drawn and which one is being dragged over, which is all the DOM can say.</p>
 */
export function panelOrderAfterDrop(
  drawn: readonly string[],
  dragged: string,
  over: string,
  after: boolean,
): string[] {
  const from = drawn.indexOf(dragged)
  const target = drawn.indexOf(over)
  if (from < 0 || target < 0) return drawn.slice()

  const to = after ? target + 1 : target
  const next = drawn.slice()
  const [moved] = next.splice(from, 1)
  next.splice(from < to ? to - 1 : to, 0, moved)
  return next
}
