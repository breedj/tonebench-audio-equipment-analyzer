import { describe, expect, it } from 'vitest'
import {
  SCOPE_DRAWINGS,
  SINC_TAPS,
  SINC_UPSAMPLE,
  drawingJoins,
  drawingNeedsRawSlice,
  drawingReconstructs,
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
    expect(drawingNeedsRawSlice('Linear')).toBe(false)
    expect(drawingNeedsRawSlice('Sinc')).toBe(true)
    expect(drawingNeedsRawSlice('Dots')).toBe(true)
  })

  /**
   * The whole of the Dots bug in one case. One predicate answered both questions until
   * 2026-11-19, so Dots -- whose entire point is that it never draws anything the analyzer did
   * not measure -- was run through the reconstruction and its markers placed on the upsampled
   * curve instead of on the samples.
   */
  it('knows that needing the samples is not the same as reconstructing between them', () => {
    expect(drawingReconstructs('Linear')).toBe(false)
    expect(drawingReconstructs('Sinc')).toBe(true)
    expect(drawingReconstructs('Dots')).toBe(false)

    // Dots still needs a raw slice, which is the half that is easy to lose in the split.
    expect(drawingNeedsRawSlice('Dots')).toBe(true)
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
    // Away from the ends, which near Nyquist are their own question -- see the end cases below.
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
    // Over the middle. Three samples a period is 0.33 of the rate, where the ends are their own
    // question whatever is done with them -- see 'is hopeless near Nyquist' below.
    const out = sincResample(samples)
    const middle = out.slice(20 * SINC_UPSAMPLE, (samples.length - 20) * SINC_UPSAMPLE)
    const reconstructed = drawnPeak(samples, middle)

    expect(largestSample).toBeCloseTo(Math.sqrt(3) / 2, 6)
    expect(reconstructed).toBeGreaterThan(0.99)
    expect(reconstructed).toBeLessThan(1.01)
  })

  /**
   * The reported artefact, stated as the number it was measured at.
   *
   * <p>A kernel reaching past the end of the array is short by whatever the missing taps would
   * have contributed, and that deficit is proportional to the signal <em>there</em> — so a window
   * whose ends land on the waveform's peaks is the worst case, and <b>Autoset produces it
   * reliably</b>: three periods with the trigger a quarter in puts both ends on a peak. Measured
   * through the running app on a 1 kHz tone at 192 kS/s, the drawn curve reached 1.147 against a
   * largest sample of 1.0075, and this synthetic stand-in reproduces that to the digit — 13.90 %
   * un-normalised against 0.00 % with it.</p>
   */
  it('does not overshoot at an end that lands on a peak', () => {
    const samples = sine(192, 3, Math.PI / 2)
    const out = sincResample(samples)

    const largest = drawnPeak(samples, samples)
    let overshoot = 0
    for (const v of out) if (v !== null) overshoot = Math.max(overshoot, Math.abs(v) - largest)

    // 0.1390 against this same tone without the weight division, which is the 13.9 % reported.
    expect(overshoot).toBeLessThan(0.001)
  })

  /**
   * And the better half of the same fix: a window inside a longer record is reconstructed at its
   * ends from the signal that really continues there, which is <em>exact</em> rather than merely
   * well-behaved — the analyzer sends those samples because only it holds the whole record.
   */
  it('reconstructs an end exactly when it is given the samples either side', () => {
    const whole = sine(192, 5, Math.PI / 2)
    const lead = whole.slice(0, SINC_TAPS)
    const slice = whole.slice(SINC_TAPS, whole.length - SINC_TAPS)
    const trail = whole.slice(whole.length - SINC_TAPS)

    const reference = sincResample(whole)
    const withContext = sincResample(slice, SINC_UPSAMPLE, SINC_TAPS, { lead, trail })
    const without = sincResample(slice)

    // Against the very same output points of a reconstruction that never had an edge there.
    let worstWith = 0
    let worstWithout = 0
    for (let k = 0; k < withContext.length; k++) {
      const truth = reference[k + (SINC_TAPS * SINC_UPSAMPLE)]!
      worstWith = Math.max(worstWith, Math.abs(withContext[k]! - truth))
      worstWithout = Math.max(worstWithout, Math.abs(without[k]! - truth))
    }

    expect(worstWith).toBeLessThan(1e-9)
    // Renormalising alone leaves a residue at the ends; context removes it outright.
    expect(worstWithout).toBeGreaterThan(worstWith)
  })

  /** A margin shorter than the kernel still helps, and no margin is simply the case above. */
  it('takes whatever context it is given', () => {
    const whole = sine(48, 6)
    const slice = whole.slice(4)

    const some = sincResample(slice, SINC_UPSAMPLE, SINC_TAPS, { lead: whole.slice(0, 4) })
    const none = sincResample(slice)
    const reference = sincResample(whole)

    const truth = reference[4 * SINC_UPSAMPLE + 4]!
    expect(Math.abs(some[4]! - truth)).toBeLessThan(Math.abs(none[4]! - truth))

    // Null and an empty list both mean "none", so a slice at the record's own end needs no branch.
    expect(sincResample(slice, SINC_UPSAMPLE, SINC_TAPS, { lead: null, trail: [] })).toEqual(none)
  })

  /**
   * Where the weight division does <em>not</em> help, recorded so it is not mistaken for a
   * regression. The Lanczos weights alternate in sign, so their partial sum only estimates the
   * missing contribution while the signal is slowly varying across the kernel — true at 192
   * samples a period, false at three. Both answers there are hopeless, which is why the choice
   * between them costs nothing; what fixes that case is context, like every other case.
   */
  it('is hopeless at an end near Nyquist, with or without the weight division', () => {
    const samples = sine(3, 10)
    const out = sincResample(samples)

    let worst = 0
    for (let k = 0; k < SINC_TAPS * SINC_UPSAMPLE; k++) {
      const t = k / SINC_UPSAMPLE
      worst = Math.max(worst, Math.abs(out[k]! - Math.sin((2 * Math.PI * t) / 3)))
    }

    // 0.298 normalised, against 0.224 without — two bad answers a third of a scale apart.
    expect(worst).toBeGreaterThan(0.1)
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

    // Away from the ends. The weight division fixes those for a slowly varying signal -- see 'does
    // not overshoot at an end that lands on a peak' -- but six samples a period is a sixth of the
    // rate, where it only improves them, and what makes an end exact is context rather than
    // arithmetic. Each of those is its own case below; this one is about the middle.
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
