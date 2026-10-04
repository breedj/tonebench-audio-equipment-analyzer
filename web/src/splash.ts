/**
 * The splash screen's two durations.
 *
 * Here rather than in `SplashScreen.vue` because the fade has to agree with a Tailwind class in that
 * component's template, and nothing in a `.vue` is testable under the node environment vitest runs
 * in. The two are easy to change independently and the symptom of doing so is quiet: a fade longer
 * than the timer leaves the splash on screen with `pointer-events-none`, so the app is visible
 * underneath and the first click lands on it, while a fade shorter than the timer leaves a blank
 * frame. `splash.test.ts` pins them against the class the template actually spells.
 */

/** How long the splash is held before it starts fading. Four seconds, as asked for. */
export const SPLASH_HOLD_MS = 4000

/**
 * How long the fade out takes, and therefore how long after the hold the element is unmounted.
 *
 * Must equal the `duration-<n>` Tailwind class on the overlay in `SplashScreen.vue` — Tailwind's
 * scale is in milliseconds, so `duration-300` is this number.
 */
export const SPLASH_FADE_MS = 300
