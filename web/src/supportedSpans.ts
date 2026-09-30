/**
 * Which chirp frequency spans the connected analyzer can actually run.
 *
 * A span implies a sample rate — the sweep is analysed to Nyquist, so a 96 kHz span needs 192 kHz —
 * and a QA403 has all three, which is why the span list was a constant. A sound card's rates are
 * probed from the hardware at connect, so one that cannot do 192 kHz must not be offered the span
 * that asks for it: the server refuses that run, and a dropdown offering a choice the server will
 * decline is a worse way to find out than not offering it.
 *
 * Its own module because three panels ask the same question and nothing in a `.vue` is testable
 * under the node environment vitest runs in.
 */

/** A span is analysed to Nyquist, so it needs twice its own width in sample rate. */
export function sampleRateForSpan(spanHz: number): number {
  return spanHz * 2
}

/**
 * The spans this analyzer can run, in the order given.
 *
 * Falls back to the full list whenever the rates are not known yet — the ranges are fetched
 * asynchronously, and a dropdown that is briefly empty on load reads as a broken panel. An analyzer
 * that reports no rates at all is treated the same way, since refusing every span would be a worse
 * answer than letting the server say why.
 */
export function supportedSpans(allSpansHz: readonly number[], sampleRatesHz: readonly number[] | null | undefined): number[] {
  if (!sampleRatesHz || sampleRatesHz.length === 0) return [...allSpansHz]

  const supported = allSpansHz.filter((span) =>
    sampleRatesHz.some((rate) => Math.abs(rate - sampleRateForSpan(span)) < 1e-6),
  )

  return supported.length > 0 ? supported : [...allSpansHz]
}

/**
 * The span to fall back to when the chosen one is not available on this analyzer — the largest that
 * is, so the band on screen shrinks rather than the panel silently keeping a value the server will
 * refuse. Null when the current one is fine.
 */
export function spanFallback(currentSpanHz: number, availableSpansHz: readonly number[]): number | null {
  if (availableSpansHz.length === 0) return null
  if (availableSpansHz.some((span) => Math.abs(span - currentSpanHz) < 1e-6)) return null

  return availableSpansHz.reduce((best, span) => (span > best ? span : best), availableSpansHz[0]!)
}
