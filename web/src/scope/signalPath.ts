/**
 * The oscilloscope's display path.
 *
 * <p>It runs on the analyzer, over the whole record, before anything is drawn or measured. That is
 * what keeps the picture and the figures beside it describing the same trace: doing it on the
 * client would change what is drawn and leave every reading describing something else.</p>
 *
 * <p>Here: the table and the words — the half nothing in a <code>.vue</code> could be tested on.</p>
 *
 * <p><b>The in-path filter was removed on 2026-11-18</b> (low-pass / high-pass / band-pass / notch,
 * with its order, Q and response preview), to be rebuilt properly later. What it got wrong was not
 * the arithmetic: it was zero-phase and correct in the middle of a record, but a record has no
 * signal outside itself, so the pad standing in for the missing signal left the first and last
 * period of the corner reading up to a third of full scale wrong. Removing that rather than
 * bounding it means filtering <i>in context</i> — keeping a margin of real samples either side of
 * the record when it is taken — which is a change to the capture path. See
 * <code>ScopeSignalPath.cs</code> for the fuller note; the arithmetic is in git.</p>
 */

export type ScopeCoupling = 'Dc' | 'Ac'

/** What the display path is set to. One field for now; the filter will join it again. */
export interface ScopeSignalPathSettings {
  coupling: ScopeCoupling
}

export const DEFAULT_SCOPE_PATH: Readonly<ScopeSignalPathSettings> = Object.freeze({
  coupling: 'Dc',
})

/**
 * The two couplings as the toggle offers them.
 *
 * <p>Each carries a <b>figure</b> and its own <b>hue</b> — Joost's design of 2026-11-20. The
 * difference between them is where the wave sits against zero, which a picture says at a glance
 * and two initials do not: <em>DC</em> and <em>AC</em> are a pair a reader has to already know.
 * The hue is the app's own blue and pink and reaches only the option that is on, so the colour
 * never carries the state on its own — the track's lift already does that.</p>
 */
export const SCOPE_COUPLINGS: readonly {
  value: ScopeCoupling
  label: string
  title: string
  icon: string
  dot: string
}[] = Object.freeze([
    {
      value: 'Dc',
      label: 'DC',
      icon: 'couplingDc',
      dot: 'var(--kind-blue-chip-fg)',
      title: 'The trace as the analyzer captured it, standing offset and all.',
    },
    {
      value: 'Ac',
      label: 'AC',
      icon: 'couplingAc',
      dot: 'var(--kind-pink-chip-fg)',
      title:
        'The record’s own mean subtracted. On this hardware that is exactly the right operation: both the QA403 and a sound card already block DC, so what is left is the front end’s own standing offset — a constant, which a mean removes exactly and a high-pass would only approximate while ringing at the start of the record.',
    },
  ])

/** Whether anything in the path changes the trace. */
export function pathChangesTrace(settings: ScopeSignalPathSettings): boolean {
  return settings.coupling === 'Ac'
}

/**
 * What the path does, in one line — the badge on the chart, the note in the panel, and the row in a
 * report.
 *
 * <p>Said wherever the figures are, because the path changes what they mean: AC coupling moves
 * every level reading by the record's own mean, and nothing else on screen would say so.</p>
 */
export function pathSummary(settings: ScopeSignalPathSettings): string | null {
  return settings.coupling === 'Ac' ? 'AC coupled' : null
}

/** The query the record fetch carries, so one setting reaches the live trace and every reference. */
export function pathQuery(settings: ScopeSignalPathSettings): Record<string, string> {
  return settings.coupling === 'Ac' ? { coupling: 'Ac' } : {}
}
