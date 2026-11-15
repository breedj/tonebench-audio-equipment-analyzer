/**
 * What the sound-card dialog should **ask the driver**, which is not always "does this pairing
 * work".
 *
 * <p>The probe exists to answer one question — will the thing this dialog describes open, and at
 * which rates — and it can only answer it by asking about the streams the bench will actually
 * open. There are three shapes, and for a long time the dialog knew only the first:</p>
 *
 * <ul>
 *   <li><b>One card, both directions</b> — one duplex stream, so the pairing is the question and
 *   the two devices must agree on a rate. This is what <c>SoundCardDevice</c> opens.</li>
 *   <li><b>One direction</b> — a capture-only ADC, a playback-only DAC, or a split with a card on
 *   one half (where <c>SoundCardSettings.ForDirection</c> blanks the other side). One stream, one
 *   device, and nothing to pair it with.</li>
 *   <li><b>A card on each half of a split</b> — two one-direction streams on two transfer threads
 *   since 2026-11-15, which is the whole point of that feature. Two devices, never one stream.</li>
 * </ul>
 *
 * <p><b>Asking the wrong one does not answer wrongly, it refuses.</b> Both reported faults were
 * this, from opposite ends. A playback-only DAC was never probed at all, because the client asked
 * for a pairing and gave up when one half was blank: no rates, so nothing could be saved and every
 * calibration step was disabled — on a bench the device layer opens perfectly well. And a MOTU
 * under ASIO against a Topping under WASAPI, chosen as a two-card split, was probed as a duplex
 * pairing and refused every rate: measured on that bench 2026-11-15, the two accept
 * 44.1–192 kHz and 44.1–768 kHz when asked one at a time and <i>nothing at all</i> when asked
 * together, which is exactly the restriction two streams remove.</p>
 *
 * <p>In a module rather than in the component for the usual reason: nothing in a `.vue` is
 * testable under this suite's node environment.</p>
 */

/** Which half of the bench the card serves. Mirrors the dialog's own `cardDirection`. */
export type CardDirection = 'both' | 'input' | 'output' | null

/** The sides a bench opens, and whether one stream carries both of them. */
export interface OpenedSides {
  /** This bench captures with the card. */
  input: boolean
  /** This bench plays through the card. */
  output: boolean
  /**
   * One stream carries both directions, so the two devices have to agree on a rate and the
   * pairing is what to probe. False whenever two streams are opened, or only one direction is.
   */
  oneStream: boolean
}

/**
 * Which streams this bench opens.
 *
 * <p>Derived from the bench rather than from what happens to be selected, because a split leaves
 * the half it does not use <i>stored</i>: a bench that plays through the card still carries
 * whatever input was chosen when it was a plain sound card, and probing that as a pairing asks
 * about a stream nothing will ever open.</p>
 */
export function openedSides(direction: CardDirection, twoCardBench: boolean): OpenedSides {
  const input = direction === 'both' || direction === 'input'
  const output = direction === 'both' || direction === 'output'
  return { input, output, oneStream: input && output && !twoCardBench }
}

/** One side's probe, as the dialog holds it. `null` where this bench does not open that side. */
export interface ProbedSide {
  /** The device, for a message that has to name it. */
  name: string
  /** Which of the app's candidate rates the driver accepted. */
  rates: number[]
  /** What the server said instead, where it said something. */
  problem: string | null
}

export interface ProbedBench {
  input: ProbedSide | null
  output: ProbedSide | null
  /** See {@link OpenedSides.oneStream}. */
  oneStream: boolean
}

/** A row of the dialog's rate readout. */
export interface RateRow {
  label: string
  rates: number[]
}

/**
 * The rate rows to print: one for a pairing or a lone device, one per card for a two-card bench.
 *
 * <p>Two rows rather than an intersection, and that is not a presentation choice —
 * <c>SplitAnalyzerDevice.Capabilities</c> says in its own remarks that the two halves are
 * <b>merged and not intersected</b>: the analysis rate is the capture card's and the generator
 * keeps a declared list of its own. A single row would have to invent a shared list the bench does
 * not require.</p>
 *
 * <p>A side that accepted nothing contributes no row; the refusal below is what speaks for it, so
 * the two never say the same thing twice.</p>
 */
export function rateRows(bench: ProbedBench): RateRow[] {
  if (bench.oneStream) {
    const rates = bench.input?.rates ?? bench.output?.rates ?? []
    return rates.length > 0 ? [{ label: 'Sample rates', rates }] : []
  }

  const both = bench.input !== null && bench.output !== null
  const rows: RateRow[] = []
  if (bench.input && bench.input.rates.length > 0)
    rows.push({ label: both ? 'Input rates' : 'Sample rates', rates: bench.input.rates })
  if (bench.output && bench.output.rates.length > 0)
    rows.push({ label: both ? 'Output rates' : 'Sample rates', rates: bench.output.rates })
  return rows
}

/**
 * Why nothing can be opened, or null when everything this bench needs was accepted.
 *
 * <p>It names the <i>device</i> when the sides were probed one at a time, and the pairing when
 * they were probed together, because those are two different faults: one card that cannot do any
 * rate this analyzer measures at, against two that cannot do one between them. The old message
 * said "this pairing" in both cases, which on a two-card split blamed a pairing the bench never
 * opens.</p>
 *
 * @param askedKhz the candidate rates, already spelled — "44.1, 48 and 192 kHz".
 */
export function rateProblem(bench: ProbedBench, askedKhz: string): string | null {
  const tail = `${askedKhz}, which are the rates this analyzer asks a card about.`

  if (bench.oneStream) {
    const side = bench.input ?? bench.output
    if (!side || side.rates.length > 0) return null
    return side.problem ?? `This pairing accepts none of ${tail}`
  }

  const bad = [bench.input, bench.output].filter(
    (side): side is ProbedSide => side !== null && side.rates.length === 0,
  )
  if (bad.length === 0) return null

  const said = bad.find((side) => side.problem !== null)
  if (said) return said.problem

  const named = bad.map((side) => `‘${side.name}’`).join(' and ')
  return bad.length === 1
    ? `${named} accepts none of ${tail}`
    : `${named} accept none of ${tail}`
}

/**
 * The one thing two independently probed cards can still get wrong between them.
 *
 * <p>Each card opens at its own rate, so neither list refuses the other — but a split whose
 * generator is set to <i>follow the capture rate</i> asks the playback card for the capture card's
 * rate, and with no rate in common there is none it can be given. Said here rather than left to
 * fail at the first RUN, where it arrives as a stream that will not open.</p>
 */
export function sharedRateNote(bench: ProbedBench): string | null {
  if (bench.oneStream || !bench.input || !bench.output) return null
  if (bench.input.rates.length === 0 || bench.output.rates.length === 0) return null

  const shared = bench.input.rates.filter((rate) => bench.output!.rates.includes(rate))
  if (shared.length > 0) return null

  return (
    'These two cards accept no rate in common, so the generator cannot follow the capture rate. '
    + 'Give it a rate of its own in the split settings.'
  )
}
