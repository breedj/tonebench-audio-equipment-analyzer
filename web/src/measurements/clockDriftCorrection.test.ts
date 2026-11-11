import { describe, expect, it } from 'vitest'
import type { DriftResidualJson } from '../api/client'
import {
  CLOCK_DRIFT_ACTIONABLE_PPM,
  CLOCK_DRIFT_MAX_FIT_MISS_DEG,
  CLOCK_DRIFT_STALE_DAYS,
  clockDriftAgeDays,
  clockDriftCorrectionLabel,
  clockDriftCorrectionNotice,
  clockDriftFreshnessNotice,
  clockDriftResidualNotice,
  clockDriftStaleNotice,
  offersClockDriftCorrection,
  readClockDriftCorrection,
  readMeasuredDate,
  sendsClockDriftCorrection,
} from './clockDriftCorrection'

/** The bench's own figure, measured 2026-11-11 between a MOTU M4 and a Sound BlasterX G6. */
const PPM = -104.038

describe('offersClockDriftCorrection', () => {
  it('offers the checkbox where a figure and a sweep meet', () => {
    expect(offersClockDriftCorrection(PPM, 'Chirp')).toBe(true)
  })

  it('offers nothing on a bench the server says has no figure', () => {
    expect(offersClockDriftCorrection(null, 'Chirp')).toBe(false)
    expect(offersClockDriftCorrection(undefined, 'Chirp')).toBe(false)
    expect(offersClockDriftCorrection(Number.NaN, 'Chirp')).toBe(false)
  })

  // The correction is built on the chirp's time-frequency map -- every frequency arrives at a known
  // moment, which is what turns a clock ratio into a delay ramp. Noise has no such map.
  it('offers nothing on a noise stimulus, whatever the bench', () => {
    for (const stimulus of ['PinkNoise', 'WhiteNoise', 'SpeechMale2011', 'SpeechFemale2011', 'SpeechMale2020'] as const)
      expect(offersClockDriftCorrection(PPM, stimulus)).toBe(false)
  })
})

describe('sendsClockDriftCorrection', () => {
  it('asks for the correction on a sweep when the box is ticked', () => {
    expect(sendsClockDriftCorrection(true, 'Chirp')).toBe(true)
  })

  it('never asks on a stimulus that cannot carry it', () => {
    expect(sendsClockDriftCorrection(true, 'PinkNoise')).toBe(false)
  })

  it('never asks when the box is clear', () => {
    expect(sendsClockDriftCorrection(false, 'Chirp')).toBe(false)
  })

  /**
   * Deliberately not gated on the figure: the server resolves a request from a bench with no figure
   * to no correction, and gating here as well would make a stale `ranges` the thing that decides
   * whether a measurement is corrected.
   */
  it('still asks when this browser has not been told the figure', () => {
    expect(sendsClockDriftCorrection(true, 'Chirp')).toBe(true)
  })
})

describe('readClockDriftCorrection', () => {
  // The one default in these panels that reads an absent field as *on*. See the module's own note:
  // before a bench runs the drift test there is no figure, so no existing measurement is affected
  // whatever this answers -- and after it, off would mean ticking a box per measurement to get any
  // benefit from having measured.
  it('reads a measurement saved before the correction existed as corrected', () => {
    expect(readClockDriftCorrection(null)).toBe(true)
    expect(readClockDriftCorrection(undefined)).toBe(true)
  })

  it('keeps a deliberate choice either way', () => {
    expect(readClockDriftCorrection(true)).toBe(true)
    expect(readClockDriftCorrection(false)).toBe(false)
  })
})

describe('clockDriftCorrectionLabel', () => {
  // The figure is in the label so a correction is never applied anonymously -- a measurement whose
  // phase has been changed should say by how much on the control that changed it.
  it('names the figure, with its sign', () => {
    expect(clockDriftCorrectionLabel(PPM)).toBe('Correct for clock drift (−104.0 ppm)')
    expect(clockDriftCorrectionLabel(12.3)).toBe('Correct for clock drift (+12.3 ppm)')
  })

  it('falls back to the bare label with no figure', () => {
    expect(clockDriftCorrectionLabel(null)).toBe('Correct for clock drift')
  })
})

describe('clockDriftCorrectionNotice', () => {
  /**
   * The figures this bench measured: 104.038 ppm over a 32768-point record at 48 kHz is 0.683 s,
   * 71 µs and 3.4 samples -- the same slip the plan predicted and the correction removes.
   */
  it('quotes the slip over the record being analysed', () => {
    const notice = clockDriftCorrectionNotice(PPM, 48000, 32768, null)

    expect(notice).toContain('3.4 samples')
    expect(notice).toContain('71 µs')
    expect(notice).toContain('0.68 s record')
  })

  // The slip is proportional to the record, so a finer resolution is a larger error -- which is the
  // whole reason this is quoted per record rather than as a ppm the reader has to convert.
  it('grows with the record length', () => {
    expect(clockDriftCorrectionNotice(PPM, 48000, 131072, null)).toContain('13.6 samples')
  })

  it('says when the figure was measured, and how long ago', () => {
    const notice = clockDriftCorrectionNotice(
      PPM, 48000, 32768, '2026-11-11T12:00:00Z', new Date('2026-11-25T12:00:00Z'))

    expect(notice).toContain('Measured')
    // The date alone makes the reader do the arithmetic, and the age is the part they act on.
    expect(notice).toContain('14 days ago')
  })

  it('leaves the age off a figure measured today', () => {
    const notice = clockDriftCorrectionNotice(
      PPM, 48000, 32768, '2026-11-11T12:00:00Z', new Date('2026-11-11T18:00:00Z'))

    expect(notice).toContain('Measured')
    expect(notice).not.toContain('ago')
  })

  it('says nothing at all without a figure or a record to measure it over', () => {
    expect(clockDriftCorrectionNotice(null, 48000, 32768, null)).toBeNull()
    expect(clockDriftCorrectionNotice(PPM, 0, 32768, null)).toBeNull()
    expect(clockDriftCorrectionNotice(PPM, 48000, 0, null)).toBeNull()
  })
})


describe('clockDriftAgeDays', () => {
  const measured = '2026-11-11T12:00:00Z'

  it('counts the days since the figure was measured', () => {
    expect(clockDriftAgeDays(measured, new Date('2026-11-25T12:00:00Z'))).toBeCloseTo(14, 3)
  })

  // A clock that is ahead of the stored stamp -- a machine whose time was corrected backwards --
  // must read as fresh rather than as negative days, which would print as "-3 days ago".
  it('never reads as negative', () => {
    expect(clockDriftAgeDays(measured, new Date('2026-11-01T12:00:00Z'))).toBe(0)
  })

  it('answers nothing for anything it cannot read', () => {
    expect(clockDriftAgeDays(null)).toBeNull()
    expect(clockDriftAgeDays('the other day')).toBeNull()
  })
})

describe('clockDriftStaleNotice', () => {
  const measured = '2026-11-11T12:00:00Z'
  const after = (days: number) =>
    new Date(Date.parse(measured) + days * 86_400_000).toISOString()

  it('says nothing while the figure is fresh', () => {
    expect(clockDriftStaleNotice(measured, new Date(after(CLOCK_DRIFT_STALE_DAYS - 1)))).toBeNull()
  })

  it('names the age and points at warm-up rather than at the calendar', () => {
    const notice = clockDriftStaleNotice(measured, new Date(after(CLOCK_DRIFT_STALE_DAYS + 4)))

    expect(notice).toContain('34 days old')
    expect(notice).toContain('warmed up')
    // And that the re-measure needs no re-wiring: the drift test measures through whatever is
    // connected, because a DUT changes a tone's level and phase but not its frequency.
    expect(notice).toContain('DUT still connected')
  })

  /**
   * The reassurance is load-bearing, not padding. A correction made with a figure wrong by delta
   * leaves delta/|eps| of the error, so a stale figure still removes most of the ramp -- and a
   * reader told only that the figure is old would reasonably clear the box, which is strictly
   * worse. The line has to say so.
   */
  it('says a stale figure is still better than none', () => {
    const notice = clockDriftStaleNotice(measured, new Date(after(400)))

    expect(notice).toContain('never worse')
  })

  it('says nothing when there is no date to judge', () => {
    expect(clockDriftStaleNotice(null)).toBeNull()
    expect(clockDriftStaleNotice('not a date')).toBeNull()
  })
})

describe('readMeasuredDate', () => {
  it('reads an ISO timestamp', () => {
    expect(readMeasuredDate('2026-11-11T12:00:00Z')).not.toBeNull()
  })

  // A stored document is not a promise. A date that will not parse must cost the caption, never the
  // checkbox beside it.
  it('answers nothing for anything it cannot read', () => {
    expect(readMeasuredDate(null)).toBeNull()
    expect(readMeasuredDate('')).toBeNull()
    expect(readMeasuredDate('the other day')).toBeNull()
  })
})

/**
 * A reading off a 0.68 s record at 48 kHz, this bench's own default resolution — so the slip in
 * samples is `|ppm| * 1e-6 * 32768`, which is what the server computes and sends.
 */
function reading(residualPpm: number, appliedPpm: number, over: Partial<DriftResidualJson> = {}): DriftResidualJson {
  return {
    residualPpm,
    appliedPpm,
    sigmaPpm: 0.02,
    fitRmsDeg: 0.3,
    bins: 15_551,
    fromHz: 20,
    toHz: 22_800,
    slipSamples: Math.abs(residualPpm) * 1e-6 * 32768,
    recordSeconds: 32768 / 48_000,
    ...over,
  }
}

describe('clockDriftResidualNotice', () => {
  // The case the whole detector exists for: the bench's own figure has moved, and the only thing
  // that could notice is the measurement itself.
  it('says the stored figure looks stale, by how much, and what to do', () => {
    const notice = clockDriftResidualNotice(reading(-26.0, -104.0))

    expect(notice?.tone).toBe('warn')
    expect(notice?.text).toContain('\u221226.0 ppm')
    expect(notice?.text).toContain('\u2212104.0 ppm')
    expect(notice?.text).toContain('26 ppm out')
    expect(notice?.text).toContain('Clock drift test')
    // And says the re-measure needs no re-wiring, which is the whole reason it is reasonable to
    // ask for: the drift test measures through whatever is connected, since a DUT changes a tone's
    // level and phase but not its frequency. The first draft of this line told the reader to loop
    // the output back to the input, contradicting the manual's own Clock drift page two sections
    // above it.
    expect(notice?.text).toContain('stay wired as it is')
    expect(notice?.text).not.toContain('looped back')
    // Keep correcting meanwhile: a stale figure removes most of the error, and a reader told only
    // that it is stale would reasonably clear the box, which is strictly worse. Same reasoning as
    // clockDriftStaleNotice's own reassurance.
    expect(notice?.text).toContain('still removes most of the error')
  })

  // The confirmation, which is the half that earns the line its credibility -- and the only thing
  // that can retire the calendar nag.
  it('confirms a figure that is doing its job', () => {
    const notice = clockDriftResidualNotice(reading(-0.4, -104.0))

    expect(notice?.tone).toBe('ok')
    expect(notice?.text).toContain('still current')
  })

  /**
   * Both gates, because either alone is a nag. A residual under
   * {@link CLOCK_DRIFT_ACTIONABLE_PPM} is the bench's own measured signature rather than staleness;
   * one under a tenth of what was applied is a correction doing 90 % of its job.
   */
  it('is quiet about a residual too small to act on, in absolute or in proportion', () => {
    expect(clockDriftResidualNotice(reading(-4.0, -104.0))?.tone).toBe('ok')
    expect(clockDriftResidualNotice(reading(-9.0, -104.0))?.tone).toBe('ok')
    expect(clockDriftResidualNotice(reading(-11.0, -104.0))?.tone).toBe('warn')
    // Small in proportion is not enough on its own: a tenth of 400 ppm is 40 ppm of real error.
    expect(clockDriftResidualNotice(reading(-39.0, -400.0))?.tone).toBe('ok')
    expect(clockDriftResidualNotice(reading(-41.0, -400.0))?.tone).toBe('warn')
  })

  /**
   * The third gate is what the residual costs *this* run. The same ppm matters more at a long
   * record than a short one, so a bench measuring at coarse resolution is not told to re-measure
   * for an error nothing on its screen can show.
   */
  it('is quiet where the residual costs this record almost nothing', () => {
    const tiny = reading(-20.0, -104.0, { slipSamples: 0.05, recordSeconds: 0.0853 })

    expect(clockDriftResidualNotice(tiny)?.tone).toBe('ok')
  })

  // A bench that has never measured its drift. Until this reading existed, nothing anywhere could
  // tell it that the curve on screen was carrying a ramp.
  it('tells an uncorrected run that it is carrying drift', () => {
    const notice = clockDriftResidualNotice(reading(-104.0, 0))

    expect(notice?.tone).toBe('warn')
    expect(notice?.text).toContain('carrying \u2212104.0 ppm')
    expect(notice?.text).toContain('3.4 samples')
    expect(notice?.text).toContain('0.68 s record')
    expect(notice?.text).toContain('Clock drift test')
  })

  /**
   * And says nothing at all about an uncorrected run with no drift worth the name, which is every
   * ordinary single-clock analyzer. A line on every measurement confirming the absence of a fault
   * nobody suspected is the definition of noise.
   */
  it('says nothing about a single-clock bench', () => {
    expect(clockDriftResidualNotice(reading(0.3, 0))).toBeNull()
  })

  it('says nothing where the fit did not describe the phase', () => {
    const wild = reading(-26.0, -104.0, { fitRmsDeg: CLOCK_DRIFT_MAX_FIT_MISS_DEG + 1 })

    expect(clockDriftResidualNotice(wild)).toBeNull()
  })

  it('says nothing without a reading, or with one it cannot read', () => {
    expect(clockDriftResidualNotice(null)).toBeNull()
    expect(clockDriftResidualNotice(undefined)).toBeNull()
    expect(clockDriftResidualNotice(reading(Number.NaN, -104.0))).toBeNull()
    expect(clockDriftResidualNotice(reading(-26.0, -104.0, { recordSeconds: 0 }))).toBeNull()
  })
})

describe('clockDriftFreshnessNotice', () => {
  const measured = '2026-11-11T12:00:00Z'
  const long_after = new Date('2027-06-01T12:00:00Z')

  /**
   * A measurement beats a date, and that ordering is the point. A figure half a year old that this
   * run has just confirmed to a fraction of a ppm does not need re-measuring, and telling its owner
   * to go and do it anyway would teach them to ignore the line.
   */
  it('lets a confirming measurement retire the calendar nag', () => {
    expect(clockDriftStaleNotice(measured, long_after)).not.toBeNull()

    const notice = clockDriftFreshnessNotice(reading(-0.4, -104.0), measured, long_after)

    expect(notice?.tone).toBe('ok')
    expect(notice?.text).toContain('still current')
  })

  it('falls back to the calendar when nothing measured anything', () => {
    const notice = clockDriftFreshnessNotice(null, measured, long_after)

    expect(notice?.tone).toBe('warn')
    expect(notice?.text).toContain('days old')
  })

  it('says nothing when neither has anything to say', () => {
    expect(clockDriftFreshnessNotice(null, measured, new Date('2026-11-12T12:00:00Z'))).toBeNull()
    expect(clockDriftFreshnessNotice(reading(0.1, 0), null)).toBeNull()
  })
})
