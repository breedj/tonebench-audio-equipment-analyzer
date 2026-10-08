/**
 * Whether a measurement screen's side rail is folded to its strip, from the two things that can
 * fold it: the user's stored preference, and a window too narrow to carry it.
 *
 * <p>Its own module because nothing in a `.vue` is testable under the node environment vitest runs
 * in, and the interesting part is not the arithmetic but the interaction — what the strip's button
 * does at a width that has already collapsed the rail.</p>
 */
export interface RailState {
  /** The stored preference (`useUiLayout`), which survives a restart. */
  stored: boolean
  /** Opened by hand at a width that would otherwise collapse it. Session-only; see `railToggled`. */
  overrideOpen: boolean
}

/**
 * Folded or not.
 *
 * <p>Above the width, the stored preference is the whole answer. Below it the rail starts folded
 * whatever the preference says — and the preference is deliberately left alone, so widening the
 * window brings back exactly the layout the user last chose, the same contract the measurement
 * sidebar's pin has had since it gained one.</p>
 */
export function railCollapsed(state: RailState, wideEnough: boolean): boolean {
  return wideEnough ? state.stored : !state.overrideOpen
}

/**
 * What pressing the strip's button does.
 *
 * <p>Below the width it opens and closes the rail <b>for this session only</b>, without writing a
 * preference. That matters in both directions: a rail the window folded is one the user did not
 * ask to fold, so unfolding it must not be recorded as "I like it open" and then surprise them on
 * a wide screen later — and, the other way, a button that writes a preference nothing on screen
 * reflects is a button that does nothing, which is worse than no button. The width decides the
 * default; the user still gets the last word at any width.</p>
 */
export function railToggled(state: RailState, wideEnough: boolean): RailState {
  if (wideEnough) return { stored: !state.stored, overrideOpen: false }
  return { stored: state.stored, overrideOpen: !state.overrideOpen }
}

/**
 * The state to carry across a change of width. The override is dropped: it answered "open it at
 * this width", and this is no longer that width — keeping it would make a window dragged wide and
 * back again disagree with the same window opened at that size.
 */
export function railAtNewWidth(state: RailState): RailState {
  return { stored: state.stored, overrideOpen: false }
}
