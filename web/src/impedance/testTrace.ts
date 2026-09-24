import type { ImpedanceTrace } from '../composables/useImpedance'
import { ohmsToDbOhms } from './equivalent'

/**
 * An impedance trace built from ohms and degrees, for tests: min and max columns equal (a stepped
 * sweep's shape), coherence 1, the peak read off the curve. Shared by every test that needs a curve
 * in the wire's shape rather than a bare `freqHz`/`ohms` pair.
 */
export function impedanceTestTrace(freqHz: number[], ohms: number[], phaseDeg: number[] = freqHz.map(() => 0), kind: ImpedanceTrace['kind'] = 'Impedance'): ImpedanceTrace {
  const db = ohms.map(ohmsToDbOhms)
  return {
    kind,
    startFreqHz: freqHz[0],
    endFreqHz: freqHz[freqHz.length - 1],
    freqHz,
    magnitudeMinDbOhms: db,
    magnitudeMaxDbOhms: [...db],
    phaseDeg,
    coherence: freqHz.map(() => 1),
    peakDbOhms: Math.max(...db),
    peakFreqHz: freqHz[db.indexOf(Math.max(...db))],
  }
}

/**
 * A driver's impedance as the textbook models it: the voice coil in series with a parallel motional
 * resonance, plus the coil's own inductance — `Z = Re + jωLe + Rm/(1 + jQ(f/fs − fs/f))`, so at fs
 * the curve peaks at exactly `Re + Rm`.
 */
export function driverTestTrace(
  { reOhms = 6.4, rmOhms = 53.6, fsHz = 45, qms = 2.5, leHenry = 0 } = {},
  { fromHz = 5, toHz = 20000, perOctave = 48 } = {},
): ImpedanceTrace {
  const freqHz: number[] = []
  const ohms: number[] = []
  const phaseDeg: number[] = []
  const steps = Math.round(Math.log2(toHz / fromHz) * perOctave)
  for (let i = 0; i <= steps; i++) {
    const f = fromHz * Math.pow(2, i / perOctave)
    const detune = qms * (f / fsHz - fsHz / f)
    const denom = 1 + detune * detune
    const re = reOhms + rmOhms / denom
    const im = 2 * Math.PI * f * leHenry - (rmOhms * detune) / denom
    freqHz.push(f)
    ohms.push(Math.hypot(re, im))
    phaseDeg.push((Math.atan2(im, re) * 180) / Math.PI)
  }
  return impedanceTestTrace(freqHz, ohms, phaseDeg)
}

/**
 * A ported (bass-reflex) loudspeaker's impedance: the same voice coil with <b>two</b> motional
 * resonances in series rather than one, which is what the box's Helmholtz resonance does to the
 * driver's — two peaks straddling a minimum at roughly the tuning frequency.
 *
 * <p>The defaults are shaped on the Jamo 504 measured on 2026-09-24 (12.20 Ω at 38.1 Hz, 14.38 Ω at
 * 104.0 Hz, 6.30 Ω at 63.0 Hz), which is the measurement the two-peak detection was written for.
 * It is a shape, not a model of a vented alignment — the tests assert that two peaks and their
 * valley are found, not that these are a particular box's numbers.</p>
 */
export function portedDriverTestTrace(
  { reOhms = 5.6, lowerHz = 38, upperHz = 104, rmOhms = 7, qms = 10 } = {},
  { fromHz = 13, toHz = 20000, perOctave = 48 } = {},
): ImpedanceTrace {
  const freqHz: number[] = []
  const ohms: number[] = []
  const phaseDeg: number[] = []
  const steps = Math.round(Math.log2(toHz / fromHz) * perOctave)

  for (let i = 0; i <= steps; i++) {
    const f = fromHz * Math.pow(2, i / perOctave)
    let re = reOhms
    let im = 0

    for (const fsHz of [lowerHz, upperHz]) {
      const detune = qms * (f / fsHz - fsHz / f)
      const denom = 1 + detune * detune
      re += rmOhms / denom
      im -= (rmOhms * detune) / denom
    }

    freqHz.push(f)
    ohms.push(Math.hypot(re, im))
    phaseDeg.push((Math.atan2(im, re) * 180) / Math.PI)
  }

  return impedanceTestTrace(freqHz, ohms, phaseDeg)
}
