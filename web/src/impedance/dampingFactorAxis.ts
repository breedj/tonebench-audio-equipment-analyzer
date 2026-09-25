/**
 * The damping-factor axis, as a relabelling of the impedance axis.
 *
 * <p><b>Why it is not a curve of its own.</b> `DF = nominal / |Z|`, so the two carry exactly the same
 * measurement — a second line adds no information, and it can only be drawn as a mirror of the first
 * on a <i>logarithmic</i> ohms axis, because only there does the reciprocal become a reflection
 * (`log DF = log nominal − log |Z|`). On a linear ohms axis no mirror exists, and the attempts to
 * force one are both wrong in their own way: share the ohms distribution and the DF curve collapses
 * onto the bottom edge (measured on a real EL84 run, a 0.9–2 Ω Zout landed within 0.12 % of the plot
 * floor against an axis reading 4–4000), or give DF its own logarithmic scale and the plot carries
 * two axes of different kinds for quantities that are reciprocals, which mirror nothing and disagree
 * visibly.</p>
 *
 * <p>So the chart draws one curve and reads it twice, the way a chart carries °C and °F: the ticks
 * below are round damping factors placed at the impedance each one corresponds to. Exact on a linear
 * axis and on a logarithmic one, with nothing to collapse.</p>
 */

/** Round damping factors worth a label, one decade's worth; scaled by powers of ten across the axis. */
const STEPS = [1, 2, 5] as const

/**
 * The least a tick may sit from its neighbour, as a fraction of the plot's height. Labels are a
 * line of text tall, so anything under a few per cent of a normal chart overlaps the one below.
 */
const MIN_GAP_FRACTION = 0.055

/**
 * The impedance positions, in ohms, at which to label round damping factors — ascending, and only
 * those inside `[zMin, zMax]`.
 *
 * <p><b>Spacing is judged in screen position, not in damping factor</b>, and that is the whole of the
 * difficulty. A tick sits at `nominal / df`, so on a <i>linear</i> ohms axis the ladder bunches
 * towards zero: against a 0–2 Ω window with an 8 Ω nominal, DF 100, 200, 300, 500 and 1000 land at
 * 0.08, 0.04, 0.027, 0.016 and 0.008 Ω — every one of them inside the bottom 4 % of the plot, which
 * draws as a stack of labels on top of each other. Thinning the <i>ladder</i> does not help; it
 * removes values from the readable end at the same rate as from the jammed one. So candidates are
 * walked from the sparse end and one is kept only when it clears the last by
 * {@link MIN_GAP_FRACTION} of the plot. On a logarithmic ohms axis the ladder is already evenly
 * spaced and nothing is dropped.</p>
 *
 * <p>Empty when the window or the nominal load cannot produce one: a non-positive nominal load has no
 * damping factor at all, and a window that does not reach a positive impedance has nowhere to put a
 * tick (`nominal / 0` is unbounded, which is why the zero a linear ohms window sits on is not a
 * position).</p>
 */
export function dampingFactorTicks(
  zMin: number,
  zMax: number,
  nominalOhms: number,
  logScale = false,
  maxTicks = 12,
): number[] {
  if (!(nominalOhms > 0) || !Number.isFinite(zMin) || !Number.isFinite(zMax) || !(zMax > 0)) return []

  const lo = Math.max(zMin, 0)
  // A log axis cannot reach zero, so its own window never does; a linear one may.
  if (logScale && !(lo > 0)) return []

  const dfHigh = lo > 0 ? nominalOhms / lo : Number.POSITIVE_INFINITY
  const dfLow = nominalOhms / zMax
  if (!(dfLow > 0)) return []

  // Where a tick lands on the plot, 0 at the bottom of the window and 1 at the top.
  const span = logScale ? Math.log10(zMax) - Math.log10(lo) : zMax - lo
  if (!(span > 0)) return []
  const position = (z: number) => (logScale ? Math.log10(z) - Math.log10(lo) : z - lo) / span

  // Unbounded above only when the window touches zero ohms; cap it at the decade that fills the plot.
  const topDecade = Number.isFinite(dfHigh) ? Math.floor(Math.log10(dfHigh)) : Math.floor(Math.log10(dfLow)) + 4
  const candidates: number[] = []
  for (let decade = Math.floor(Math.log10(dfLow)); decade <= topDecade + 1; decade++) {
    for (const step of STEPS) {
      const df = step * Math.pow(10, decade)
      if (df < dfLow || df > dfHigh) continue
      candidates.push(df)
    }
  }

  // Ascending in DF is descending in ohms, i.e. from the top of the plot downwards — and the sparse
  // end first, so the labels that survive are the ones with room around them.
  const kept: number[] = []
  let last: number | null = null
  for (const df of candidates) {
    const z = nominalOhms / df
    if (last !== null && Math.abs(position(last) - position(z)) < MIN_GAP_FRACTION) continue
    kept.push(z)
    last = z
    if (kept.length >= maxTicks) break
  }

  // Ascending in *ohms*, which is the axis these are positions on.
  return kept.sort((a, b) => a - b)
}

/** A damping factor as the axis labels it: no more precision than the number deserves. */
export function formatDampingFactor(df: number): string {
  if (!Number.isFinite(df)) return ''
  return df >= 100 ? df.toFixed(0) : df >= 10 ? df.toFixed(1) : df.toFixed(2)
}
