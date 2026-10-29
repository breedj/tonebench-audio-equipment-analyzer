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

/**
 * A slider's own value, as the caption beside it prints it: at most one decimal, and no decimal at
 * all on a whole number.
 *
 * <p>A range input cannot be read back, so every <c>.tb-range</c> here carries its figure in the
 * caption row — and a figure of a slider's own stepping is always short. The ones that are not come
 * from somewhere else: the Canvas 2D decay chart emits a dragged view back into Rotation and Tilt,
 * and that arithmetic is continuous, so <b>Tilt read 29.299999999999997%</b> after a drag (reported
 * 2026-10-28). Binary floating point gives no shorter exact spelling of that number; rounding is
 * the only answer, and one decimal is finer than any of these sliders' own steps.</p>
 *
 * <p>Display only, deliberately. The value the chart and <c>params_json</c> carry stays exactly
 * what the drag produced — rounding at the source would make the picture jump a little at the end
 * of every drag, which is a worse fault than a long caption.</p>
 *
 * <p>A non-finite value prints as nothing rather than as <c>NaN</c>: a cleared or mid-edit model is
 * the caller's business, and every slider here already falls back to its own default when it draws.</p>
 */
export function formatSliderValue(value: number): string {
  if (!Number.isFinite(value)) return ''
  // `+` drops a trailing `.0`, so an integer slider reads `25` and never `25.0`.
  return String(+value.toFixed(1))
}
