/**
 * How the chart joins one sample to the next.
 *
 * <p><b>Straight lines are a lie near the top of the band, and it is not a small one.</b> A sampled
 * waveform is band-limited by the ADC's own decimation filter, so between any two samples there is
 * exactly one signal it could have been — and it is not the straight line. At 48 kS/s a 1 kHz tone
 * has 48 samples a period and the difference is invisible; a 20 kHz tone has 2.4, and joining those
 * samples with lines draws a jagged waveform whose peak wanders by several decibels from cycle to
 * cycle, because the samples land at a different part of each one. Readers reasonably take that for
 * amplitude modulation or for a dirty generator. Reconstructing the band-limited signal instead
 * draws the sine that was really there.</p>
 *
 * <p><b>It is reconstruction, not resolution.</b> Nothing here adds information: it draws the one
 * waveform consistent with the samples and with the band limit the hardware already imposes. Where
 * that band limit does not hold — a signal with content above Nyquist, which this analyzer cannot
 * capture correctly in the first place — it is as wrong as the straight lines, in a different
 * direction.</p>
 */

export type ScopeDrawing = 'Linear' | 'Sinc' | 'Dots'

export const SCOPE_DRAWINGS: readonly { value: ScopeDrawing; label: string; title: string }[] =
  Object.freeze([
    {
      value: 'Linear',
      label: 'Lines',
      title:
        'Straight lines between samples. Exact where there are plenty of samples a cycle, and visibly wrong near the top of the band, where it draws a peak that wanders from cycle to cycle.',
    },
    {
      value: 'Sinc',
      label: 'sin(x)/x',
      title:
        'The band-limited waveform the samples came from, which is the one signal they could have been. Reconstruction rather than resolution — it adds no information, it draws what the samples already say.',
    },
    {
      value: 'Dots',
      label: 'Dots',
      title:
        'The samples alone, joined by nothing. The honest picture when what you are reading is where the samples fell — and the one that never draws something the analyzer did not measure.',
    },
  ])

/** Whether the mode joins its samples at all. */
export function drawingJoins(mode: ScopeDrawing): boolean {
  return mode !== 'Dots'
}

/**
 * Whether the mode needs the samples themselves.
 *
 * <p>Both of the interesting ones do, and a wide window has none: past
 * <code>RawSamplesPerColumn</code> the analyzer sends both extremes of each column instead, and
 * which values fell where inside one is exactly what that throws away. So on a decimated slice
 * these fall back to lines, and the rail says so rather than leaving a control that does
 * nothing.</p>
 */
export function drawingNeedsRawSlice(mode: ScopeDrawing): boolean {
  return mode !== 'Linear'
}

/**
 * Whether the mode <em>reconstructs</em> between the samples.
 *
 * <p><b>Only sin(x)/x, and that is the whole difference between this and the predicate above.</b>
 * One answer served both questions until 2026-11-19, so <b>Dots</b> — whose entire point is that
 * it never draws anything the analyzer did not measure — was run through the reconstruction and
 * its markers placed on the 8x upsampled curve rather than on the captured samples. The two
 * questions are genuinely different: Dots needs the samples (so it falls back to lines on a
 * decimated slice, and the rail warns about it) and must not be interpolated.</p>
 */
export function drawingReconstructs(mode: ScopeDrawing): boolean {
  return mode === 'Sinc'
}

/**
 * How many output points each input sample becomes.
 *
 * <p>Eight. The slice is only ever served raw when it holds at most two samples per column, so
 * eight puts at least four points in every pixel — past which the curve is smooth and the extra
 * points are drawn on top of each other.</p>
 */
export const SINC_UPSAMPLE = 8

/**
 * How many samples either side each output point is summed over.
 *
 * <p>Sixteen, which is where the Lanczos window has taken the kernel to nothing. The cost is
 * 32 multiply-adds per output point — on a raw slice of at most 3200 samples that is 1.6 million
 * per channel per redraw, which is a millisecond.</p>
 */
export const SINC_TAPS = 16

function sinc(x: number): number {
  if (x === 0) return 1
  const pix = Math.PI * x
  return Math.sin(pix) / pix
}

/**
 * Real samples either side of the slice, for the kernel to reach into at the ends.
 *
 * <p>These are never drawn. They exist so that a window <em>inside</em> a record is reconstructed
 * at its edges from the signal that really continues there, instead of from a kernel that has run
 * out of array — which is the difference between a trace whose ends are exact and one whose ends
 * are merely well-behaved. The analyzer sends them because only it holds the whole record; a
 * window at the record's own end gets fewer, or none, and there is nothing wrong with that.</p>
 */
export interface SincContext {
  /** The samples immediately before the slice, in order, oldest first. */
  lead?: readonly number[] | null
  /** The samples immediately after the slice, in order. */
  trail?: readonly number[] | null
}

/**
 * The band-limited waveform through <paramref name="samples"/>, at
 * <see cref="SINC_UPSAMPLE"/> points per sample.
 *
 * <p><b>Lanczos-windowed</b>, not a bare sinc: a bare one is infinitely long, and truncating it
 * leaves ripples either side of every edge — the drawn overshoot of a square wave would then be the
 * truncation's rather than the DUT's, which on a screen whose Overshoot reading is taken from the
 * samples would put the number and the picture at odds. The window tapers the kernel to nothing at
 * the last tap instead.</p>
 *
 * <p><b>The ends are where this goes wrong, and it went wrong by 14 % of amplitude.</b> A kernel
 * reaching past either end of the array has fewer taps to sum with, so the sum is short by
 * whatever the missing ones would have contributed — and that deficit is proportional to the
 * signal level at the edge. Measured through the running app on a 1 kHz tone at 192 kS/s, a
 * 3 ms window whose ends land on the waveform's peaks: the first output points climb to
 * <b>1.147</b> against a largest sample of <b>1.0075</b>, and the last to 1.1446, while the
 * middle of the same slice is out by 0.00007. That is the reported artefact, and <b>Autoset
 * produces the worst case of it reliably</b> — three periods with the trigger a quarter in puts
 * both ends on a peak.</p>
 *
 * <p><b>Two fixes, and the order matters.</b> Real signal is the better one: for a window
 * <em>inside</em> a record the missing samples exist on the analyzer, so it sends them as
 * <paramref name="context"/> and the ends are reconstructed exactly like the middle — the same
 * capture then reads <b>0.01 %</b> instead of 13.9 %. Where there is no context to be had — the
 * record's own ends, where there genuinely is no outside — the sum is divided by the weight it
 * was taken over, which reads <b>0.003 %</b> on that capture.</p>
 *
 * <p><b>Renormalising is not universally right, which is why it is the fallback and not the
 * fix.</b> The Lanczos weights alternate in sign, so a partial sum of them is only a good estimate
 * of the missing contribution while the signal is slowly varying across the kernel — which is the
 * ordinary case and emphatically not the case near Nyquist. Measured on a synthetic tone at three
 * samples a period it is slightly <em>worse</em> than not normalising at all, 0.298 against 0.224.
 * Both of those are hopeless; the choice between them costs nothing, and being 30 times better at
 * 192 samples a period is worth having. Context fixes that case too, at 0.0013.</p>
 */
export function sincResample(
  samples: readonly (number | null)[],
  upsample: number = SINC_UPSAMPLE,
  taps: number = SINC_TAPS,
  context?: SincContext | null,
): (number | null)[] {
  const n = samples.length
  if (n < 2 || upsample < 2) return [...samples]

  // The context is spliced on and taken off again, rather than special-cased inside the loop: the
  // kernel then has one array to reach into and cannot tell a lead sample from a slice sample,
  // which is the point. The pad-wide-then-take-the-middle shape is ArbitraryWaveformLoader's.
  const lead = context?.lead ?? []
  const trail = context?.trail ?? []
  const padded: (number | null)[] =
    lead.length === 0 && trail.length === 0 ? (samples as (number | null)[]) : [...lead, ...samples, ...trail]
  const total = padded.length
  const offset = lead.length

  const out = new Array<number | null>(((n - 1) * upsample) + 1)
  for (let k = 0; k < out.length; k++) {
    const t = (k / upsample) + offset
    const centre = Math.floor(t)

    // An output point that lands exactly on a sample is that sample, gaps included: no sum can
    // improve on a value that is already known, and it keeps a null from being averaged away.
    if (t === centre) {
      out[k] = padded[centre] ?? null
      continue
    }

    let sum = 0
    let weight = 0
    let sawGap = false
    for (let j = centre - taps + 1; j <= centre + taps; j++) {
      if (j < 0 || j >= total) continue
      const value = padded[j]
      if (value === null || value === undefined) {
        sawGap = true
        continue
      }
      const d = t - j
      const w = sinc(d) * sinc(d / taps)
      sum += value * w
      weight += w
    }

    // A gap in the samples stays a gap: reconstructing across one would draw a waveform through a
    // stretch the analyzer did not measure.
    //
    // Divided by the weight it was summed over, which is a no-op in the middle -- a whole kernel's
    // weights total one -- and is the whole of the answer at an end with no context to reach into.
    out[k] = sawGap || weight === 0 ? null : sum / weight
  }

  return out
}

/** The x array the resampled series is drawn against — the same span, at the same density. */
export function sincResampleX(x: readonly number[], upsample: number = SINC_UPSAMPLE): number[] {
  const n = x.length
  if (n < 2 || upsample < 2) return [...x]

  const step = (x[1] - x[0]) / upsample
  const out = new Array<number>(((n - 1) * upsample) + 1)
  for (let k = 0; k < out.length; k++) out[k] = x[0] + (k * step)
  return out
}
