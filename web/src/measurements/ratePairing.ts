/**
 * Whether a bench's two sample rates can carry a stimulus that has a period of its own, and what to
 * say when they cannot.
 *
 * <p>A split runs its generating and capturing halves at independent rates. A chirp, a periodic
 * noise sequence — anything whose period has to be a whole number of frames at <i>both</i> ends —
 * needs those rates to be an integer ratio. Within one rate family (48 kHz × 2ᵏ, or 44.1 kHz × 2ᵏ)
 * every pairing is a power-of-two ratio and so always is. Across them it provably never is: a record
 * of N frames at 192 kHz needs N × 147/640 to be an integer, and 640 = 2⁷ × 5 while 147 = 3 × 7²
 * carries no factor 5, so no power-of-two N ever is — and radix-2 forbids the only N that would.</p>
 *
 * <p>So a mixed-family pairing is permanently tone-only. That is not a gap to be closed later, and
 * saying so before Run is the whole point of this module: the server refuses the run
 * (`DeviceSessionService.RequireGeneratorRateUsable`) with a message of its own, but a panel that
 * lets the user compose a measurement and press Run only to be handed a 409 is a worse way to find
 * out.</p>
 *
 * <p><b>Mirrored arithmetic, deliberately.</b> `isPeriodicRatePair` is four lines and is the
 * client's copy of the server's rule of the same name, the way `periodicWave.ts` mirrors
 * `PeriodicWave`'s quantisation. The alternative — having the server answer per staged span — does
 * not work: the capture rate here comes from the span <i>this panel</i> is set to, which the server
 * has not been told about yet.</p>
 *
 * Its own module because nothing in a `.vue` file is testable under the node environment vitest
 * runs in.
 */

/**
 * Whether two rates are an integer ratio either way round, which is what a periodic stimulus needs
 * to land on the same grid at both ends. Equal rates trivially are, and so is a generator that
 * simply follows the capture rate.
 *
 * <p>The server's `DeviceSessionService.IsPeriodicRatePair` is the same function; they are pinned
 * against each other by the cases in `ratePairing.test.ts`, which use the rate pairs the app can
 * actually offer.</p>
 */
export function isPeriodicRatePair(aHz: number, bHz: number): boolean {
  if (!Number.isFinite(aHz) || !Number.isFinite(bHz) || aHz <= 0 || bHz <= 0) return false

  const ratio = Math.max(aHz, bHz) / Math.min(aHz, bHz)
  return Math.abs(ratio - Math.round(ratio)) < 1e-9
}

export interface MixedRatePair {
  /**
   * The rate the generating half is pinned to, or 0/null where it follows the capture rate.
   *
   * <p>Following, there is nothing to warn about: one rate is trivially an integer ratio with
   * itself. This is `DeviceRanges.generatorRateOverrideHz`, which is the pinned figure rather than
   * the last rate anything streamed at — see that field for why the distinction matters.</p>
   */
  generatorRateHz?: number | null
  /** The rate this measurement will capture at, which on a response panel is its span doubled. */
  captureRateHz: number
  /**
   * What to do instead, named in the panel's own words. The impedance screen says "Choose Stepped
   * sine", the chirp screens name the stepped-sine measurement — a message that only says a run is
   * impossible leaves the reader with nothing to act on, which is the fault
   * `OutputLevelReach.FloorMessage` was written to correct one measurement along.
   */
  alternative: string
}

/**
 * Why a periodic stimulus cannot run on this bench, or null when it can.
 *
 * <p>Null is the answer for every single-device bench and for every split inside one rate family,
 * which is to say almost always: this fires only where the two halves are in different families.</p>
 */
export function mixedRatePairNotice(pair: MixedRatePair): string | null {
  const generatorRateHz = pair.generatorRateHz
  if (typeof generatorRateHz !== 'number' || !Number.isFinite(generatorRateHz) || generatorRateHz <= 0) {
    return null
  }

  if (!Number.isFinite(pair.captureRateHz) || pair.captureRateHz <= 0) return null
  if (isPeriodicRatePair(generatorRateHz, pair.captureRateHz)) return null

  return (
    `This bench's generator runs at ${kHz(generatorRateHz)} kHz while this measurement captures at ` +
    `${kHz(pair.captureRateHz)} kHz — not an integer ratio, so a stimulus with a period of its own ` +
    `cannot line up with the analysis record, at any resolution. ${pair.alternative}`
  )
}

/** The way out the two chirp screens offer. */
export const STEPPED_SINE_ALTERNATIVE =
  'Measure the frequency response with the stepped sine instead, or set a generator rate in the ' +
  'same family as the capture rate in the split settings.'

/** The impedance screen's, where the alternative is a stimulus on this same screen. */
export const IMPEDANCE_STEPPED_ALTERNATIVE =
  'Set Stimulus to Stepped sine, which measures one tone at a time and is unaffected, or set a ' +
  'generator rate in the same family as the capture rate in the split settings.'

function kHz(hz: number): string {
  return Number((hz / 1000).toFixed(3)).toString()
}
