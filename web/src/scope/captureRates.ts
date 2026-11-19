/**
 * How fast records are captured, against how fast they reach the chart.
 *
 * <p>Three counters in series, each lower than the last, and the whole point of the module is the
 * difference between them. <b>Committed</b> is what the trigger engine put down, bounded by the
 * device's block rather than by the trigger -- one record per block, about 47 a second on a QA403
 * at 192 kS/s. <b>Collected</b> is what the run's 10 ms poll took off the engine's single slot.
 * <b>Drawn</b> is what this browser fetched, at one per 120 ms status poll.</p>
 *
 * <p>The persistence haze accumulates per <i>drawn</i> record, so that last figure is the one that
 * decides how fast an intensity-graded picture builds -- which is the question this exists to
 * answer.</p>
 *
 * <p>Here rather than in the component for the usual reason: nothing in a `.vue` is testable under
 * the node environment vitest runs in, and every rule below is one that is quietly wrong rather
 * than visibly broken.</p>
 */

/**
 * How far back a rate is measured over.
 *
 * <p>A single 120 ms poll sees 3-6 records, so one record of jitter is ±8/s on a figure whose whole
 * subject is a difference of about 17/s. Two seconds is ~17 samples, which is enough to read
 * steadily without being so long that the number lags a change in the settings.</p>
 */
export const RATE_WINDOW_MS = 2000

/**
 * Below this the window is not long enough to divide by. A rate from one sample is not a rate, and
 * a figure that is wrong in the reassuring direction is worse than no figure at all.
 */
export const MIN_RATE_SPAN_MS = 500

/** One reading of the three counters, stamped when the status that carried them arrived. */
export interface ScopeRateSample {
  /** `performance.now()`, not `Date.now()` -- monotonic, and immune to the clock being set. */
  atMs: number
  /** `OscilloscopeStatus.recordsCommitted`. */
  committed: number
  /** `OscilloscopeStatus.recordsTaken`. */
  collected: number
  /** This client's own count of records that reached the chart. */
  drawn: number
}

/** The three rates over the window, with the counts they were derived from. */
export interface ScopeCaptureRates {
  committedPerS: number
  collectedPerS: number
  drawnPerS: number
  /** Records in the window, so the tooltip can state both without re-deriving one from the other. */
  committed: number
  collected: number
  drawn: number
  spanMs: number
}

/**
 * Adds a sample and drops whatever has aged out.
 *
 * <p>Returns a new array when anything changed, so a caller holding it in a ref gets a reactive
 * update, and the same array when nothing did.</p>
 */
export function pushRateSample(
  samples: readonly ScopeRateSample[],
  next: ScopeRateSample,
): ScopeRateSample[] {
  // A counter going backwards is a new run, an adopted owner or a Clear -- all three restart every
  // counter at zero. Keeping the older samples would subtract a large number from a small one and
  // report a negative rate, so the window starts again from here.
  const last = samples.at(-1)
  if (
    last !== undefined &&
    (next.committed < last.committed || next.collected < last.collected || next.drawn < last.drawn)
  ) {
    return [next]
  }

  const kept = [...samples, next]
  const oldest = next.atMs - RATE_WINDOW_MS
  while (kept.length > 2 && kept[0]!.atMs < oldest) kept.shift()
  return kept
}

/** The three rates, or null while there is not enough window to divide by. */
export function captureRates(samples: readonly ScopeRateSample[]): ScopeCaptureRates | null {
  if (samples.length < 2) return null

  const first = samples[0]!
  const last = samples.at(-1)!
  const spanMs = last.atMs - first.atMs
  if (!(spanMs >= MIN_RATE_SPAN_MS)) return null

  const perMs = 1000 / spanMs
  const committed = last.committed - first.committed
  const collected = last.collected - first.collected
  const drawn = last.drawn - first.drawn

  return {
    committedPerS: committed * perMs,
    collectedPerS: collected * perMs,
    drawnPerS: drawn * perMs,
    committed,
    collected,
    drawn,
    spanMs,
  }
}

/** The badge's label: the three rates in the order they fall. */
export function captureRateLabel(rates: ScopeCaptureRates): string {
  const n = (v: number) => (v >= 10 ? Math.round(v).toString() : v.toFixed(1))
  return `${n(rates.committedPerS)} → ${n(rates.collectedPerS)} → ${n(rates.drawnPerS)} /s`
}
