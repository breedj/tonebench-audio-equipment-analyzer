/**
 * A number input's value brought back inside its own floor and ceiling.
 *
 * <p><b>Why the floor cannot stay in the `min` attribute.</b> HTML anchors a number input's step
 * grid on `min`: the values its spinner offers are <c>min + k·step</c>, not <c>k·step</c>. So
 * `min="2" step="1000"` offers 2, 1002, 2002 — and a field sitting at 20000 steps <em>up</em> to
 * 20002 and back <em>down</em> to 19002, rather than to 21000 and 19000. Reported 2026-10-25
 * against the decay charts' To (Hz) box, and true of every field in the app whose floor was not a
 * multiple of its step: eight of them, including both From/To pairs, the two sweep stop thresholds,
 * the gate's After and the decay span.</p>
 *
 * <p>The platform gives exactly one lever. With no `min` attribute the step base is 0, which puts
 * the grid on round numbers — and the floor then has to be kept here instead. `max` is unaffected
 * and stays an attribute, being no part of the step base.</p>
 *
 * <p>Call it on <b>change</b>, never on input: a figure is typed one digit at a time, so "2000"
 * passes through 2 and 20, and a field that clamped those could not be filled in at all.</p>
 *
 * <p>A value that is not a finite number is handed back untouched. An emptied box is `''` rather
 * than `NaN` — Vue's `.number` modifier returns the original string when it cannot parse one — and
 * every panel that carries one of these fields already has its own answer for an empty box, which
 * this must not pre-empt by inventing a number the user did not type.</p>
 */
export function clampNumberField(value: number, min: number, max = Number.POSITIVE_INFINITY): number {
  if (!Number.isFinite(value)) return value
  return Math.min(max, Math.max(min, value))
}
