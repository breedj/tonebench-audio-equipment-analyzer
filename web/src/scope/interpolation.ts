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
export function drawingNeedsSamples(mode: ScopeDrawing): boolean {
  return mode !== 'Linear'
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
 * The band-limited waveform through <paramref name="samples"/>, at
 * <see cref="SINC_UPSAMPLE"/> points per sample.
 *
 * <p><b>Lanczos-windowed</b>, not a bare sinc: a bare one is infinitely long, and truncating it
 * leaves ripples either side of every edge — the drawn overshoot of a square wave would then be the
 * truncation's rather than the DUT's, which on a screen whose Overshoot reading is taken from the
 * samples would put the number and the picture at odds. The window tapers the kernel to nothing at
 * the last tap instead.</p>
 *
 * <p>The ends are summed over whatever samples exist, so the first and last few output points are
 * reconstructed from a one-sided kernel. That is the record's edge again: there is nothing outside
 * it to sum over, and inventing something would be the padding question one layer up.</p>
 */
export function sincResample(
  samples: readonly (number | null)[],
  upsample: number = SINC_UPSAMPLE,
  taps: number = SINC_TAPS,
): (number | null)[] {
  const n = samples.length
  if (n < 2 || upsample < 2) return [...samples]

  const out = new Array<number | null>(((n - 1) * upsample) + 1)
  for (let k = 0; k < out.length; k++) {
    const t = k / upsample
    const centre = Math.floor(t)

    // An output point that lands exactly on a sample is that sample, gaps included: no sum can
    // improve on a value that is already known, and it keeps a null from being averaged away.
    if (t === centre) {
      out[k] = samples[centre] ?? null
      continue
    }

    let sum = 0
    let weight = 0
    let sawGap = false
    for (let j = centre - taps + 1; j <= centre + taps; j++) {
      if (j < 0 || j >= n) continue
      const value = samples[j]
      if (value === null) {
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
    out[k] = sawGap || weight === 0 ? null : sum
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
