// How many records one measurement may fold into a single estimate.
//
// Mirrors `FrequencyResponseRequest.MaxAverageCount`, which every kind that averages whole records
// now reads: the frequency response, the impulse response, the stepped sine, crosstalk, output
// impedance and impedance. It lived as a literal `10` in five panels and a sixth that had been
// raised to 20 on its own, which is how the sixth came to differ without anyone deciding it should.
//
// **Not the sweeps' figure.** THD and IMD vs amplitude or frequency cap their averaging at 64, and
// that is a different quantity: those average *spectra* at one point of a grid, where these average
// whole records of one measurement.

/** The ceiling, shared with the server. Raised from 10 on 2026-10-21. */
export const MAX_AVERAGE_COUNT = 20

/** The floor. One record is not an average, and zero is not a measurement. */
export const MIN_AVERAGE_COUNT = 1

/** The message a panel shows for a count outside the range, worded the way the server words it. */
export function averageCountError(count: number): string | null {
  return count < MIN_AVERAGE_COUNT || count > MAX_AVERAGE_COUNT
    ? `Averages must be between ${MIN_AVERAGE_COUNT} and ${MAX_AVERAGE_COUNT}.`
    : null
}
