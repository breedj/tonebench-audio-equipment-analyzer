/**
 * One option of an <code>IconSelect</code>, and the arithmetic of moving through a list of them.
 *
 * <p>In its own module rather than exported from the component for <code>segmented.ts</code>'
 * reason — <code>&lt;script setup&gt;</code> cannot export, and the component is generic over the
 * value type, so a call site that annotates its own option array needs the interface by name — and
 * for one more: the index arithmetic is the part of a listbox that is got wrong, and nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in.</p>
 *
 * <p><b>Why a listbox at all, where the rest of the app uses a <code>&lt;select&gt;</code>.</b> The
 * Trigger panel's choices are the one place on this screen where the <em>name</em> is the worst
 * part of the option: <em>Edge</em>, <em>Pulse</em>, <em>Window</em>, <em>Dropout</em> and
 * <em>Runt</em> are five words a reader has to parse, where five figures showing what each one
 * fires on are scanned — the same argument <code>scope/measurementIcons.ts</code> makes about
 * thirty measurement names, and a native <code>&lt;option&gt;</code> cannot carry a picture.</p>
 */

export interface IconSelectOption<T> {
  value: T
  label: string
  /**
   * A figure from <code>scope/displayIcons.ts</code>, drawn in <code>currentColor</code>.
   *
   * <p>Optional because a letter sometimes says more: the two input channels are <b>L</b> and
   * <b>R</b> on every scope ever made, and a drawing of a channel is a drawing of nothing.</p>
   */
  icon?: string
  /** A short letter to draw instead of a figure. */
  glyph?: string
  /** Per-option tooltip; the group's own label covers the rest. */
  title?: string
  disabled?: boolean
}

/** Whether a list has anything in it that can be chosen at all. */
export function hasEnabledIconSelectOption<T>(options: readonly IconSelectOption<T>[]): boolean {
  return options.some((option) => !option.disabled)
}

/**
 * The index an arrow key moves to: <paramref name="step"/> along, wrapping, skipping anything
 * disabled.
 *
 * <p>Wrapping rather than stopping at the ends, which is what a listbox does and what makes the
 * last option reachable from the first on a list of five. <b>Skipping the disabled ones is the half
 * that is easy to miss</b>: a list whose only disabled entry sits in the middle would otherwise let
 * the keyboard land on an option a mouse cannot press, and Enter there does nothing at all — a
 * control that reads as broken rather than as refusing.</p>
 *
 * <p>Returns <code>-1</code> when nothing in the list can be chosen, which is the one case a caller
 * must not treat as an index.</p>
 */
export function moveIconSelectIndex<T>(
  options: readonly IconSelectOption<T>[],
  from: number,
  step: number,
): number {
  const count = options.length
  if (count === 0 || !hasEnabledIconSelectOption(options)) return -1

  // A `from` of -1 (nothing active yet) enters the list at the end the key is pointing away from,
  // so the first Down lands on the first option rather than on the second.
  let index = Number.isInteger(from) && from >= 0 && from < count ? from : step > 0 ? -1 : 0
  for (let moved = 0; moved < count; moved += 1) {
    index = (((index + step) % count) + count) % count
    if (!options[index]?.disabled) return index
  }
  return -1
}

/** The first option that can be chosen, or -1. Where Home goes, and where an opened list starts. */
export function firstEnabledIconSelectIndex<T>(options: readonly IconSelectOption<T>[]): number {
  return options.findIndex((option) => !option.disabled)
}

/** The last option that can be chosen, or -1. Where End goes. */
export function lastEnabledIconSelectIndex<T>(options: readonly IconSelectOption<T>[]): number {
  for (let i = options.length - 1; i >= 0; i -= 1) {
    if (!options[i]?.disabled) return i
  }
  return -1
}

/**
 * Where the list opens: on the value it already holds, or on the first option that can be chosen.
 *
 * <p>The selected one even when it is <em>disabled</em> — it is what the control is set to, and
 * opening somewhere else would make the list disagree with the button that opened it.</p>
 */
export function openIconSelectIndex<T>(options: readonly IconSelectOption<T>[], selected: T): number {
  const index = options.findIndex((option) => option.value === selected)
  return index >= 0 ? index : firstEnabledIconSelectIndex(options)
}
