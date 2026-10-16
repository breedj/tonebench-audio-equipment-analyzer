/**
 * The FFT screen's Frequency range dropdowns: what each one offers, and the rule that keeps the
 * two of them from crossing over.
 *
 * <p>Both lists used to stop where the other one started -- From ended at 500 Hz and To began at
 * 1 kHz -- so a window inside one decade (5-10 kHz, say, to look at a tweeter's breakup, or
 * 20-100 Hz for a hum) could not be chosen from the panel at all, only dragged on the chart and
 * then lost on the next reset. They now overlap across the whole band (Joost, 2026-10-16), which is
 * what makes the pairing rule below necessary: overlapping lists are the only way to select
 * From > To, and a reversed window draws nothing while looking like an ordinary pick.</p>
 *
 * <p>Pure, and therefore tested -- nothing in a `.vue` is testable here. The panel only binds the
 * two selects and routes their change through {@link freqRangeAfterFromChange} /
 * {@link freqRangeAfterToChange}.</p>
 */

/**
 * The From dropdown. Up to 50 kHz and no further: the rule below answers a From at or above the
 * current To by moving To *up*, and above 50 kHz there is nothing to move it to on a 192 kHz
 * capture (To's own ladder ends at 100 kHz; the experimental 384 kHz rate adds 200 kHz, and a list
 * that reached 100 kHz would therefore behave differently depending on a Configuration checkbox).
 */
export const FREQ_FROM_PRESETS: readonly number[] = [
  5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000,
]

/**
 * The To dropdown. Down to 10 Hz, which is as low as From's second entry -- From's 5 Hz is kept
 * below every To, so "To 10 Hz" always has a From to sit above.
 */
export const FREQ_TO_PRESETS: readonly number[] = [
  10, 20, 50, 100, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000,
]

/** The smallest option strictly above `value`, or null when `value` is already at or past the top. */
function nextAbove(options: readonly number[], value: number): number | null {
  let best: number | null = null
  for (const option of options) {
    if (option > value && (best === null || option < best)) best = option
  }
  return best
}

/** The largest option strictly below `value`, or null when `value` is already at or below the bottom. */
function nextBelow(options: readonly number[], value: number): number | null {
  let best: number | null = null
  for (const option of options) {
    if (option < value && (best === null || option > best)) best = option
  }
  return best
}

/** A frequency window, as the two dropdowns hold it. */
export interface FreqRange {
  fromHz: number
  toHz: number
}

/**
 * The window after From has been picked: To is pushed up to the next option above the pick when it
 * would otherwise reverse or collapse the window.
 *
 * <p>`toOptions` is the To dropdown's list <i>as offered</i>, which is not always
 * {@link FREQ_TO_PRESETS}: it carries the experimental 384 kHz rate's 200 kHz entry, and whatever
 * Autofit last left selected (ChartDisplayPanel's `withCurrentValue`). Taking it as an argument is
 * what keeps this rule from naming a value the dropdown does not contain -- which would read as the
 * control jumping to a blank.</p>
 *
 * <p>A pick with nothing above it in that list is refused outright -- `current` comes back
 * unchanged, so the select snaps back to what it was showing. Unreachable from
 * {@link FREQ_FROM_PRESETS}, whose top entry is deliberately below To's, and the reason it is.</p>
 */
export function freqRangeAfterFromChange(
  pickedFromHz: number,
  current: FreqRange,
  toOptions: readonly number[],
): FreqRange {
  if (pickedFromHz < current.toHz) return { fromHz: pickedFromHz, toHz: current.toHz }
  const above = nextAbove(toOptions, pickedFromHz)
  return above === null ? current : { fromHz: pickedFromHz, toHz: above }
}

/**
 * The window after To has been picked: From is pulled down to the next option below the pick, on
 * the same terms as above. `fromOptions` for the same reason `toOptions` is taken there.
 */
export function freqRangeAfterToChange(
  pickedToHz: number,
  current: FreqRange,
  fromOptions: readonly number[],
): FreqRange {
  if (current.fromHz < pickedToHz) return { fromHz: current.fromHz, toHz: pickedToHz }
  const below = nextBelow(fromOptions, pickedToHz)
  return below === null ? current : { fromHz: below, toHz: pickedToHz }
}
