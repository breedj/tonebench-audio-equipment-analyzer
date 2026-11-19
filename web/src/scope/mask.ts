/**
 * The pass/fail mask: a reference waveform opened out by a tolerance, and whether the live trace
 * stays inside it.
 *
 * <p><b>What a mask is, and why it is built this way.</b> A tolerance on a waveform has two parts,
 * and only one of them is a level. A DUT whose output is a millivolt low everywhere fails a
 * ±1 mV level tolerance; one whose output is identical but arrives 2 µs late fails it too, far
 * harder, because near an edge a couple of microseconds is most of the amplitude. So the band is
 * the reference <em>dilated</em> in both directions at once: at each column it runs from the
 * lowest the reference reaches anywhere within ±Δt, less ΔV, up to the highest it reaches in that
 * same stretch, plus ΔV. On a flat stretch that is simply ±ΔV; on an edge it opens out to the
 * whole height of the edge, which is exactly the allowance a timing tolerance means.</p>
 *
 * <p><b>Here rather than in the chart</b> for this project's usual reason — nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in — and because a mask that
 * is quietly a little too wide passes everything and says nothing about it.</p>
 *
 * <p><b>Absent is null, never a number.</b> A column the reference has nothing to say about — one
 * outside the stored capture's own span — has no band, and nothing is judged there. A band built
 * by filling those with zeros would read as a tolerance around silence, and a trace that is not
 * silent there would fail against a reference that was never measured there.</p>
 */

/** The band, on the live trace's own x array: one upper and one lower value per column. */
export interface ScopeMask {
  upper: (number | null)[]
  lower: (number | null)[]
  /** How many columns carry a band at all. */
  columns: number
}

/** What one record's comparison against the band came to. */
export interface ScopeMaskVerdict {
  /** Columns that had both a band and a sample to judge. */
  checked: number
  /** How many of those were outside it. */
  failed: number
  /** The first column that failed, for the chart and the readout, or null where none did. */
  firstFailIndex: number | null
  /** The largest distance outside the band, in the trace's own units. */
  worstExcess: number
}

/** A verdict of nothing: no band, or nothing to judge against it. */
export const NO_MASK_VERDICT: ScopeMaskVerdict = Object.freeze({
  checked: 0,
  failed: 0,
  firstFailIndex: null,
  worstExcess: 0,
})

/**
 * Builds the band from a reference already on the live trace's x array.
 *
 * <p><b>A decimated reference is dilated from both of its outlines.</b> Where the window is wide
 * the analyzer sends a min and a max per column, and a band built from the maxima alone would be
 * narrower at the bottom than the capture it was taken from — so a live trace that reproduced the
 * reference exactly would fail against it. With one array the same values serve both ends, which
 * is what a raw slice has.</p>
 *
 * @param reference The reference's own values per column, null where it has none.
 * @param dxS Seconds per column, which is what the time tolerance is converted through.
 * @param toleranceV The level tolerance, either side. Negative is read as zero.
 * @param toleranceS The time tolerance, either side.
 * @param lowerReference The reference's lower outline where it has one; its own values otherwise.
 */
export function buildScopeMask(
  reference: readonly (number | null)[],
  dxS: number,
  toleranceV: number,
  toleranceS: number,
  lowerReference?: readonly (number | null)[] | null,
): ScopeMask {
  const count = reference.length
  const upper = new Array<number | null>(count).fill(null)
  const lower = new Array<number | null>(count).fill(null)

  const volts = Number.isFinite(toleranceV) && toleranceV > 0 ? toleranceV : 0
  // Rounded to whole columns, since that is the resolution the band can have: a tolerance finer
  // than one column is a tolerance this picture cannot express, and reporting it as if it could
  // would be the kind of quiet over-width that makes a mask pass everything.
  const half =
    Number.isFinite(dxS) && dxS > 0 && Number.isFinite(toleranceS) && toleranceS > 0
      ? Math.round(toleranceS / dxS)
      : 0

  let columns = 0
  for (let i = 0; i < count; i += 1) {
    // The band exists where the REFERENCE does. A gap inside the dilation window is skipped, but a
    // gap at the column itself means this reference has nothing to say here — covering it from its
    // neighbours would invent a tolerance around a stretch that was never measured.
    const own = reference[i]
    if (own === null || own === undefined || !Number.isFinite(own)) continue

    // The column's own lower value where the reference carries one, and its own value otherwise —
    // which is what a raw slice has, there being nothing left to summarise in a single sample.
    let high = own
    let low = finiteOr(lowerReference?.[i], own) ?? own
    const from = Math.max(0, i - half)
    const to = Math.min(count - 1, i + half)
    for (let j = from; j <= to; j += 1) {
      const top = reference[j]
      if (top !== null && top !== undefined && Number.isFinite(top) && top > high) high = top
      const bottom = finiteOr(lowerReference?.[j], top)
      if (bottom !== null && bottom < low) low = bottom
    }

    upper[i] = high + volts
    lower[i] = low - volts
    columns += 1
  }

  return { upper, lower, columns }
}

/** A value where it is a real number, and the fallback where it is not. */
function finiteOr(value: number | null | undefined, fallback: number | null | undefined): number | null {
  if (value !== null && value !== undefined && Number.isFinite(value)) return value
  if (fallback !== null && fallback !== undefined && Number.isFinite(fallback)) return fallback
  return null
}

/**
 * Judges one record against the band.
 *
 * <p><b>Both outlines of a decimated trace are judged, not just the drawn line.</b> Where the
 * window is wide the analyzer sends a min and a max per column, and a spike that lives inside one
 * column is exactly what a mask is pointed at — judging the maxima alone would pass a trace that
 * dips through the floor of the band between two pixels.</p>
 *
 * <p>A column with no band is not checked, and nor is one with no sample: neither is a pass and
 * neither is a failure, which is why <code>checked</code> is reported beside the count.</p>
 */
export function maskVerdict(
  mask: ScopeMask,
  upperTrace: readonly (number | null)[],
  lowerTrace?: readonly (number | null)[] | null,
): ScopeMaskVerdict {
  let checked = 0
  let failed = 0
  let firstFailIndex: number | null = null
  let worstExcess = 0

  const count = Math.min(mask.upper.length, upperTrace.length)
  for (let i = 0; i < count; i += 1) {
    const high = mask.upper[i]
    const low = mask.lower[i]
    if (high === null || low === null) continue

    let excess = 0
    let judged = false
    for (const value of [upperTrace[i], lowerTrace?.[i]]) {
      if (value === null || value === undefined || !Number.isFinite(value)) continue
      judged = true
      if (value > high) excess = Math.max(excess, value - high)
      else if (value < low) excess = Math.max(excess, low - value)
    }
    if (!judged) continue

    checked += 1
    if (excess > 0) {
      failed += 1
      if (firstFailIndex === null) firstFailIndex = i
      if (excess > worstExcess) worstExcess = excess
    }
  }

  return { checked, failed, firstFailIndex, worstExcess }
}

/** A run's tally of records judged. */
export interface ScopeMaskTally {
  passed: number
  failed: number
}

export function newMaskTally(): ScopeMaskTally {
  return { passed: 0, failed: 0 }
}

/**
 * Folds one record's verdict into the tally.
 *
 * <p>A record nothing could be judged on is <b>neither</b> — the mask's own "absent is not a pass"
 * rule at the level of a whole record. A band that covers none of the window, because the reference
 * is of another stretch entirely, would otherwise report every record as a pass.</p>
 */
export function addMaskVerdict(tally: ScopeMaskTally, verdict: ScopeMaskVerdict): void {
  if (verdict.checked === 0) return
  if (verdict.failed > 0) tally.failed += 1
  else tally.passed += 1
}
