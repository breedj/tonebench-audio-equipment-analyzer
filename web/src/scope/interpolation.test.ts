import { describe, expect, it } from 'vitest'
import {
  SCOPE_DRAWINGS,
  SINC_UPSAMPLE,
  drawingJoins,
  drawingNeedsSamples,
  sincResample,
  sincResampleX,
} from './interpolation'

/** A sine sampled at `samplesPerPeriod`, for `periods` of it. */
function sine(samplesPerPeriod: number, periods: number, phase = 0): number[] {
  const n = Math.round(samplesPerPeriod * periods) + 1
  const x: number[] = []
  for (let i = 0; i < n; i++) x.push(Math.sin((2 * Math.PI * i) / samplesPerPeriod + phase))
  return x
}

/** What the drawn waveform's largest excursion is, under one joining rule. */
function drawnPeak(samples: readonly number[], resampled: readonly (number | null)[]): number {
  void samples
  let peak = 0
  for (const v of resampled) if (v !== null) peak = Math.max(peak, Math.abs(v))
  return peak
}

describe('the modes', () => {
  it('offers each once, lines first', () => {
    expect(SCOPE_DRAWINGS.map((d) => d.value)).toEqual(['Linear', 'Sinc', 'Dots'])
    for (const mode of SCOPE_DRAWINGS) expect(mode.title.length).toBeGreaterThan(40)
  })

  it('knows which joins its samples and which needs them', () => {
    expect(drawingJoins('Linear')).toBe(true)
    expect(drawingJoins('Sinc')).toBe(true)
    expect(drawingJoins('Dots')).toBe(false)

    // Lines can be drawn from min/max columns; the other two cannot, because which values fell
    // where inside a column is exactly what decimation throws away.
    expect(drawingNeedsSamples('Linear')).toBe(false)
    expect(drawingNeedsSamples('Sinc')).toBe(true)
    expect(drawingNeedsSamples('Dots')).toBe(true)
  })
})

describe('the reconstruction', () => {
  /**
   * The case the mode exists for, stated as the error against the waveform the samples came from
   * rather than as a peak: at 2.37 samples a period the samples land somewhere different on every
   * cycle, so straight lines between them draw a shape that is not the tone — and which reads as
   * amplitude modulation or a dirty generator.
   */
  it('draws the waveform the samples came from, where straight lines do not', () => {
    const samplesPerPeriod = 2.37
    const samples = sine(samplesPerPeriod, 40)
    const reconstructed = sincResample(samples)

    const truth = (t: number) => Math.sin((2 * Math.PI * t) / samplesPerPeriod)
    const linearAt = (t: number) => {
      const i = Math.floor(t)
      const f = t - i
      return samples[i] * (1 - f) + samples[Math.min(i + 1, samples.length - 1)] * f
    }

    let worstLinear = 0
    let worstSinc = 0
    // Away from the ends, where the kernel is one-sided and the record has no outside.
    for (let k = 20 * SINC_UPSAMPLE; k < (samples.length - 20) * SINC_UPSAMPLE; k++) {
      const t = k / SINC_UPSAMPLE
      worstLinear = Math.max(worstLinear, Math.abs(linearAt(t) - truth(t)))
      worstSinc = Math.max(worstSinc, Math.abs(reconstructed[k]! - truth(t)))
    }

    // Lines are out by about a third of full scale; the reconstruction by about 0.9 %, which is
    // the finite kernel's own residue this close to Nyquist (2.37 samples a period is 0.42 of the
    // rate) and is two orders better than the thing it replaces.
    expect(worstLinear).toBeGreaterThan(0.3)
    expect(worstSinc).toBeLessThan(0.02)
  })

  /**
   * And the drawn peak follows: at these rates the largest sample is short of the amplitude, and a
   * line can never exceed its own endpoints.
   */
  it('recovers a peak the samples themselves never reach', () => {
    // Exactly three samples a period from a zero crossing: every one of them lands at 0, +0.866 or
    // -0.866 and none at the peak, so a line can never draw more than 0.866 of a 1 V tone.
    const samples = sine(3, 30)
    const largestSample = drawnPeak(samples, samples)
    // Over the middle: at the record's own ends the kernel is one-sided, so the curve overshoots
    // by about 7 %. That is the record having no outside rather than a fault of the kernel, and it
    // is the same edge question the in-path filter's padding answers one layer up.
    const out = sincResample(samples)
    const middle = out.slice(20 * SINC_UPSAMPLE, (samples.length - 20) * SINC_UPSAMPLE)
    const reconstructed = drawnPeak(samples, middle)

    expect(largestSample).toBeCloseTo(Math.sqrt(3) / 2, 6)
    expect(reconstructed).toBeGreaterThan(0.99)
    expect(reconstructed).toBeLessThan(1.01)
  })

  it('leaves a well-sampled waveform where it was', () => {
    const samples = sine(48, 4)
    const out = sincResample(samples)

    // Every input sample is still itself, exactly.
    for (let i = 0; i < samples.length; i++) expect(out[i * SINC_UPSAMPLE]).toBeCloseTo(samples[i], 12)
    expect(drawnPeak(samples, out)).toBeCloseTo(1, 2)
  })

  // The interpolated points are the band-limited signal, which for a pure tone is the tone: this
  // checks the halfway points against the analytic sine rather than against the implementation.
  it('puts the interpolated points on the waveform the samples came from', () => {
    const samplesPerPeriod = 6
    const samples = sine(samplesPerPeriod, 20)
    const out = sincResample(samples)

    // Away from the ends, where the kernel is one-sided.
    for (let k = 20 * SINC_UPSAMPLE; k < (samples.length - 20) * SINC_UPSAMPLE; k += 3) {
      const t = k / SINC_UPSAMPLE
      expect(out[k]!).toBeCloseTo(Math.sin((2 * Math.PI * t) / samplesPerPeriod), 2)
    }
  })

  it('is the samples themselves at every sample, so the picture agrees with the readings', () => {
    const samples = [0, 1, -0.5, 0.25, 0]
    const out = sincResample(samples)
    for (let i = 0; i < samples.length; i++) expect(out[i * SINC_UPSAMPLE]).toBe(samples[i])
  })

  // A gap is a stretch the analyzer did not measure; reconstructing across one would draw a
  // waveform through it.
  it('leaves a gap a gap', () => {
    const samples: (number | null)[] = [0, 1, null, 1, 0]
    const out = sincResample(samples)

    expect(out[2 * SINC_UPSAMPLE]).toBeNull()
    expect(out[2 * SINC_UPSAMPLE - 1]).toBeNull()
    expect(out[2 * SINC_UPSAMPLE + 1]).toBeNull()
    // And the far ends, outside the kernel's reach, still draw.
    expect(out[0]).toBe(0)
  })

  it('has nothing to do with too few samples', () => {
    expect(sincResample([])).toEqual([])
    expect(sincResample([1])).toEqual([1])
    expect(sincResample([1, 2], 1)).toEqual([1, 2])
  })

  it('keeps the span, at the same density as the samples', () => {
    const x = [0, 1, 2, 3]
    const out = sincResampleX(x)

    expect(out.length).toBe(((x.length - 1) * SINC_UPSAMPLE) + 1)
    expect(out[0]).toBe(0)
    expect(out.at(-1)).toBeCloseTo(3, 12)
    expect(out[SINC_UPSAMPLE]).toBeCloseTo(1, 12)
    expect(sincResample([0, 1, 2, 3]).length).toBe(out.length)
  })

  it('leaves an x array it cannot densify alone', () => {
    expect(sincResampleX([5])).toEqual([5])
    expect(sincResampleX([])).toEqual([])
  })

  /** Nothing it produces is anything but a number or a gap, whatever it is handed. */
  it('produces only numbers and gaps', () => {
    for (const samples of [[0, 0, 0], [1e30, -1e30, 1e30], [null, 1, null] as (number | null)[]]) {
      for (const v of sincResample(samples)) {
        if (v !== null) expect(Number.isFinite(v)).toBe(true)
      }
    }
  })
})
