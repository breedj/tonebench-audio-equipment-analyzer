// The four classic low-pass alignments the simulator offers as DUTs, prototype for prototype as
// `FilterPrototype` (src/Qa403.Device/Simulation/FilterPrototypes.cs) holds them: fifth order,
// normalised to a corner of 1 rad/s, computed with scipy.signal (butter, cheby1 rp=1,
// bessel norm='mag', ellip rp=1 rs=60; analog=True, output='zpk'). Edit the two tables together —
// the C# is the one that produces the samples, this one is what the manual draws.
//
// Each prototype is realised the way `FilterPrototype.Sections` does it: one biquad per conjugate
// pole pair (with the elliptic's zero pair in its numerator), a first-order section for the real
// pole, every section unity at DC, all mapped by the bilinear transform prewarped at the corner.
import { Cascade, fromAnalogPrewarped, type Biquad } from './biquad'

export interface FilterPrototype {
  name: string
  /** Each conjugate pole pair's upper member, `sigma + j·omega`. */
  polePairs: readonly (readonly [sigma: number, omega: number])[]
  /** The one real pole, as a positive distance down the negative real axis. */
  realPole: number
  /** The elliptic's zero pairs `±j·omega`, one per pole pair in the same order; empty for the all-pole designs. */
  zeroOmegas: readonly number[]
}

/** Where all four put their corner: an octave above the FFT screen's default tone, a decade below 20 kHz. */
export const FILTER_CORNER_HZ = 2000

/** The Bessel prototype's group delay at DC in units of 1/ωc: 193 µs at 2 kHz. */
export const BESSEL_DELAY_AT_DC = 2.4274107

export const BUTTERWORTH: FilterPrototype = {
  name: 'Butterworth',
  polePairs: [
    [-0.309016994374947, 0.951056516295154],
    [-0.809016994374947, 0.587785252292473],
  ],
  realPole: 1,
  zeroOmegas: [],
}

export const CHEBYSHEV: FilterPrototype = {
  name: 'Chebyshev',
  polePairs: [
    [-0.08945836220019, 0.990107112003389],
    [-0.234205032817997, 0.611919847721094],
  ],
  realPole: 0.289493341235613,
  zeroOmegas: [],
}

export const BESSEL: FilterPrototype = {
  name: 'Bessel',
  polePairs: [
    [-0.957676548562681, 1.471124320730394],
    [-1.380877325860439, 0.717909587626768],
  ],
  realPole: 1.502316271447478,
  zeroOmegas: [],
}

export const ELLIPTIC: FilterPrototype = {
  name: 'Elliptic',
  polePairs: [
    [-0.071731220371103, 0.99419350430997],
    [-0.230737733537983, 0.66479439438804],
  ],
  realPole: 0.325517851567125,
  zeroOmegas: [1.740548328054631, 2.6540921270289],
}

/** The sections that realise `prototype` as a low-pass with its corner at `cornerHz` — see the file comment. */
export function filterSections(prototype: FilterPrototype, cornerHz: number, sampleRateHz: number): Biquad[] {
  const wc = 2 * Math.PI * cornerHz
  const sections: Biquad[] = []

  prototype.polePairs.forEach(([sigma, omega], i) => {
    const poleMagnitudeSquared = (sigma * sigma + omega * omega) * wc * wc
    const a1 = -2 * sigma * wc
    const zeroOmega = prototype.zeroOmegas[i]
    if (zeroOmega !== undefined) {
      const zeroSquared = zeroOmega * zeroOmega * wc * wc
      const gain = poleMagnitudeSquared / zeroSquared
      sections.push(fromAnalogPrewarped(gain, 0, gain * zeroSquared, 1, a1, poleMagnitudeSquared, sampleRateHz, cornerHz))
    } else {
      sections.push(fromAnalogPrewarped(0, 0, poleMagnitudeSquared, 1, a1, poleMagnitudeSquared, sampleRateHz, cornerHz))
    }
  })

  const a = prototype.realPole * wc
  sections.push(fromAnalogPrewarped(0, 0, a, 0, 1, a, sampleRateHz, cornerHz))
  return sections
}

export function butterworthLowPass(sampleRateHz: number): Cascade {
  return new Cascade(filterSections(BUTTERWORTH, FILTER_CORNER_HZ, sampleRateHz), sampleRateHz)
}

export function chebyshevLowPass(sampleRateHz: number): Cascade {
  return new Cascade(filterSections(CHEBYSHEV, FILTER_CORNER_HZ, sampleRateHz), sampleRateHz)
}

export function besselLowPass(sampleRateHz: number): Cascade {
  return new Cascade(filterSections(BESSEL, FILTER_CORNER_HZ, sampleRateHz), sampleRateHz)
}

export function ellipticLowPass(sampleRateHz: number): Cascade {
  return new Cascade(filterSections(ELLIPTIC, FILTER_CORNER_HZ, sampleRateHz), sampleRateHz)
}

/**
 * The analog frequency a digital one stands for under the prewarped bilinear transform,
 * `k·tan(πf/fs)/2π` with `k = ωc/tan(ωc/2fs)`: what a closed form has to be evaluated at to match
 * the sampled filter exactly. The corner maps to itself.
 */
export function analogEquivalentHz(freqHz: number, sampleRateHz: number, cornerHz = FILTER_CORNER_HZ): number {
  const wc = 2 * Math.PI * cornerHz
  const k = wc / Math.tan(wc / (2 * sampleRateHz))
  return (k * Math.tan((Math.PI * freqHz) / sampleRateHz)) / (2 * Math.PI)
}

/** The fifth-order Butterworth's closed form, in dB, at an analog frequency. */
export function butterworthDb(analogHz: number, cornerHz = FILTER_CORNER_HZ): number {
  return -10 * Math.log10(1 + Math.pow(analogHz / cornerHz, 10))
}

/** The fifth-order 1 dB Chebyshev's closed form, in dB, at an analog frequency. */
export function chebyshevDb(analogHz: number, cornerHz = FILTER_CORNER_HZ): number {
  const x = analogHz / cornerHz
  const t5 = 16 * x ** 5 - 20 * x ** 3 + 5 * x
  const epsilonSquared = Math.pow(10, 0.1) - 1
  return -10 * Math.log10(1 + epsilonSquared * t5 * t5)
}
