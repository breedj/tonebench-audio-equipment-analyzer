/**
 * Committing a number the user typed into a settings field.
 *
 * Separate from the dialog because nothing in a `.vue` is testable under the node environment
 * vitest runs in, and the edge cases here are the ones that bite: an emptied field, a half-typed
 * minus sign, a paste of something that is not a number at all.
 */
export interface NumberFieldBounds {
  min: number
  max: number
  /** What an unparseable or empty entry means. Not always the minimum: an emptied gain box means 0, which is the middle of its range. */
  fallback: number
}

/**
 * The value a field's text should be stored as: parsed, clamped to the bounds, and falling back
 * rather than throwing.
 *
 * An **empty field is the fallback, not a rejection** — clearing a box and tabbing away is how
 * people say "none of this", and refusing it would leave the field disagreeing with what is
 * stored. `Number('')` is 0, which happens to be right for a gain and wrong as a general rule, so
 * emptiness is tested for rather than relied on.
 */
export function commitNumber(raw: string, bounds: NumberFieldBounds): number {
  const trimmed = raw.trim()
  if (trimmed.length === 0) return bounds.fallback

  const value = Number(trimmed)
  if (!Number.isFinite(value)) return bounds.fallback

  return Math.max(bounds.min, Math.min(bounds.max, value))
}
