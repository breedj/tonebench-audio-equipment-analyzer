import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { SPLASH_FADE_MS, SPLASH_HOLD_MS } from './splash'

const COMPONENT = readFileSync(new URL('./components/SplashScreen.vue', import.meta.url), 'utf8')

describe('the splash screen durations', () => {
  it('holds for the four seconds that were asked for', () => {
    expect(SPLASH_HOLD_MS).toBe(4000)
  })

  /**
   * The component fades with a Tailwind `duration-<n>` class and unmounts itself on a timer of
   * SPLASH_FADE_MS, so the two have to be the same number. Changing one alone fails quietly: a fade
   * longer than the timer leaves the splash on screen (transparent, `pointer-events-none`, so the
   * app is visible underneath and clickable — it looks fine and is simply still mounted), and a fade
   * shorter than the timer leaves a blank frame before the app appears.
   *
   * Read out of the template because that is where the real value lives; Tailwind's duration scale
   * is already in milliseconds, so the class's number is this constant.
   */
  it('fades for exactly as long as the class in the template says', () => {
    const match = /\bduration-(\d+)\b/.exec(COMPONENT)

    expect(match, 'SplashScreen.vue should carry a Tailwind duration- class on its overlay').not.toBeNull()
    expect(Number(match![1])).toBe(SPLASH_FADE_MS)
  })

  it('fades in well under the hold, so the greeting is not mostly a fade', () => {
    expect(SPLASH_FADE_MS).toBeLessThan(SPLASH_HOLD_MS / 4)
  })
})
