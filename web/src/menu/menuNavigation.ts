/**
 * Keyboard movement through a menu, skipping disabled items and wrapping at either end. Pure, so
 * ProjectMenu.vue's key handling can be tested here — nothing in a .vue file is, under the node
 * environment vitest runs in.
 */
export interface MenuItemState {
  disabled?: boolean
}

/** The index `delta` enabled items away from `from` (or from just outside the list when `from` is null), wrapping. */
export function nextEnabledIndex(items: MenuItemState[], from: number | null, delta: 1 | -1): number | null {
  const n = items.length
  if (n === 0) return null
  let i = from === null ? (delta > 0 ? -1 : n) : from
  for (let step = 0; step < n; step++) {
    i = (i + delta + n) % n
    if (!items[i].disabled) return i
  }
  return null
}

export function firstEnabledIndex(items: MenuItemState[]): number | null {
  return nextEnabledIndex(items, null, 1)
}

export function lastEnabledIndex(items: MenuItemState[]): number | null {
  return nextEnabledIndex(items, null, -1)
}
