/**
 * <p>Which runs the two decay views draw, and in what order.</p>
 *
 * <p>A decay surface is a heatmap: it cannot overlay the way a curve can, so comparing runs means
 * a chart each, stacked (asked for 2026-10-26). This is the list those charts are rendered from,
 * and it is a module rather than a computed in <c>App.vue</c> for the usual reason — nothing in a
 * <c>.vue</c> is testable under the node environment vitest runs in — and because the ordering
 * rule below is the one thing here that can quietly go wrong.</p>
 */

/** One run on screen: the live measurement, or a checked history item. */
export interface DecaySubject {
  /** Stable across re-renders, and distinct from every measurement id. */
  key: string
  /** The stored result to analyse, or null for the live run. */
  measurementId: number | null
  /** What the caption above its chart says. */
  label: string
}

/** What this needs of a checked history item; the real one carries a great deal more. */
export interface DecayOverlay {
  id: number
  detail: { name: string }
}

/** The caption the live run's chart carries once there is something to tell it apart from. */
export const LIVE_DECAY_LABEL = 'Measured'

/**
 * The runs to draw, primary first.
 *
 * <p><b>The live measurement is always first when there is one</b>, and that is the rule worth
 * holding: subject zero is the <em>primary</em>, whose surface stays in the pair of refs that
 * everything else on the screen reads — the screenshot gate, the notes under the chart, a report.
 * Put a history item there instead and all three would quietly start describing somebody else's
 * run.</p>
 *
 * <p>With nothing live the first checked item takes that place, which is exactly what the single
 * chart drew before this existed: a checked item used to draw its waveform and its gated response
 * and then leave both decay views blank, which reads as the analysis being broken rather than as
 * having no input.</p>
 *
 * <p>The count is bounded by the checklist's own three-overlay cap rather than here, so at most
 * four charts. Nothing is deduplicated: a result cannot be checked twice.</p>
 */
export function decaySubjects(hasLiveRun: boolean, overlays: readonly DecayOverlay[]): DecaySubject[] {
  const subjects: DecaySubject[] = []
  if (hasLiveRun) subjects.push({ key: 'live', measurementId: null, label: LIVE_DECAY_LABEL })
  for (const overlay of overlays) {
    subjects.push({ key: String(overlay.id), measurementId: overlay.id, label: overlay.detail.name })
  }
  return subjects
}

/** The stored ids among them — what the surfaces of unchecked runs are pruned against. */
export function decaySubjectIds(subjects: readonly DecaySubject[]): Set<number> {
  return new Set(subjects.map((s) => s.measurementId).filter((id): id is number => id !== null))
}
