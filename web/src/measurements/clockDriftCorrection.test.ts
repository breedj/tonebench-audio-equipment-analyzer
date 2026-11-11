import { describe, expect, it } from 'vitest'
import {
  clockDriftCorrectionLabel,
  clockDriftCorrectionNotice,
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

  it('says when the figure was measured, when that is known', () => {
    const notice = clockDriftCorrectionNotice(PPM, 48000, 32768, '2026-11-11T12:00:00Z')

    expect(notice).toContain('Measured')
  })

  it('says nothing at all without a figure or a record to measure it over', () => {
    expect(clockDriftCorrectionNotice(null, 48000, 32768, null)).toBeNull()
    expect(clockDriftCorrectionNotice(PPM, 0, 32768, null)).toBeNull()
    expect(clockDriftCorrectionNotice(PPM, 48000, 0, null)).toBeNull()
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
