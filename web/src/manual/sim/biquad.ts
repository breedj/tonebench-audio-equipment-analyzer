// A port of the simulator's own `Biquad` (src/Qa403.Device/Simulation/SimulatedDut.cs) — the same
// Audio-EQ-Cookbook designs, the same bilinear `fromAnalog`, the same direct-form II transposed
// recursion — so the manual's "expected result" charts are drawn from the transfer function that
// actually produces the simulated samples rather than from a sketch of it.
//
// This is a *third* copy of those formulas (the C# has one, and the coefficients per model live in
// models.ts beside this file). It is not on any measurement path: nothing here touches a device,
// a capture or a stored result. It exists so the manual can draw a model's response without
// shipping images, and biquad.test.ts pins it against figures measured through the real app so a
// drift from the C# shows up as a failing test rather than as a picture that is quietly wrong.

export interface Complex {
  re: number
  im: number
}

function mul(a: Complex, b: Complex): Complex {
  return { re: a.re * b.re - a.im * b.im, im: a.re * b.im + a.im * b.re }
}

function div(a: Complex, b: Complex): Complex {
  const d = b.re * b.re + b.im * b.im
  return { re: (a.re * b.re + a.im * b.im) / d, im: (a.im * b.re - a.re * b.im) / d }
}

export function magnitude(c: Complex): number {
  return Math.hypot(c.re, c.im)
}

export function phaseDeg(c: Complex): number {
  return (Math.atan2(c.im, c.re) * 180) / Math.PI
}

/** One second-order section, normalised so a0 = 1. */
export interface Biquad {
  b0: number
  b1: number
  b2: number
  a1: number
  a2: number
}

function prepare(freqHz: number, q: number, sampleRateHz: number) {
  // The C# clamps a pole asked for above Nyquist rather than folding it back down the spectrum: a
  // 38 kHz resonance is genuinely out of band at 48 kHz, and the model flattens there.
  const f0 = Math.min(Math.max(freqHz, 1), sampleRateHz * 0.45)
  const w0 = (2 * Math.PI * f0) / sampleRateHz
  return { cos: Math.cos(w0), alpha: Math.sin(w0) / (2 * Math.max(0.05, q)) }
}

function normalise(b0: number, b1: number, b2: number, a0: number, a1: number, a2: number): Biquad {
  return { b0: b0 / a0, b1: b1 / a0, b2: b2 / a0, a1: a1 / a0, a2: a2 / a0 }
}

export function lowPass(freqHz: number, q: number, sampleRateHz: number): Biquad {
  const { cos, alpha } = prepare(freqHz, q, sampleRateHz)
  const b1 = 1 - cos
  return normalise(b1 / 2, b1, b1 / 2, 1 + alpha, -2 * cos, 1 - alpha)
}

export function highPass(freqHz: number, q: number, sampleRateHz: number): Biquad {
  const { cos, alpha } = prepare(freqHz, q, sampleRateHz)
  const b0 = (1 + cos) / 2
  return normalise(b0, -(1 + cos), b0, 1 + alpha, -2 * cos, 1 - alpha)
}

/** Constant-peak-gain band-pass: unity at the centre, 6 dB per octave away either side. */
export function bandPass(freqHz: number, q: number, sampleRateHz: number): Biquad {
  const { cos, alpha } = prepare(freqHz, q, sampleRateHz)
  return normalise(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha)
}

/** A resonance: `gainDb` of lift (or cut) at `freqHz`, unity well away from it. */
export function peaking(freqHz: number, q: number, gainDb: number, sampleRateHz: number): Biquad {
  const { cos, alpha } = prepare(freqHz, q, sampleRateHz)
  const a = Math.pow(10, gainDb / 40)
  return normalise(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a)
}

/** A frequency-independent gain — the resistive impedance fixture, and nothing else. */
export function gain(g: number): Biquad {
  return { b0: g, b1: 0, b2: 0, a1: 0, a2: 0 }
}

/**
 * The bilinear transform of an analog `(b2 s² + b1 s + b0) / (a2 s² + a1 s + a0)`, unwarped: the
 * fixtures the C# builds this way all sit far below Nyquist, where `tan(x) ≈ x`.
 */
export function fromAnalog(
  b2: number,
  b1: number,
  b0: number,
  a2: number,
  a1: number,
  a0: number,
  sampleRateHz: number,
): Biquad {
  return bilinear(b2, b1, b0, a2, a1, a0, 2 * sampleRateHz)
}

/**
 * The same mapping prewarped so `prewarpHz` lands exactly on itself — `k = ω / tan(ωT/2)` in place
 * of `2fs`. What the C# `Biquad.FromAnalog(…, sampleRateHz, prewarpHz)` overload does, and what the
 * four classic filters are built with: they are specified by their corner and nothing else.
 */
export function fromAnalogPrewarped(
  b2: number,
  b1: number,
  b0: number,
  a2: number,
  a1: number,
  a0: number,
  sampleRateHz: number,
  prewarpHz: number,
): Biquad {
  const w = 2 * Math.PI * Math.min(Math.max(prewarpHz, 1), sampleRateHz * 0.45)
  return bilinear(b2, b1, b0, a2, a1, a0, w / Math.tan(w / (2 * sampleRateHz)))
}

function bilinear(b2: number, b1: number, b0: number, a2: number, a1: number, a0: number, k: number): Biquad {
  const kk = k * k
  return normalise(
    b2 * kk + b1 * k + b0,
    2 * (b0 - b2 * kk),
    b2 * kk - b1 * k + b0,
    a2 * kk + a1 * k + a0,
    2 * (a0 - a2 * kk),
    a2 * kk - a1 * k + a0,
  )
}

/** One section's exact response at a normalised angular frequency, `H(e^jω)`. */
export function sectionResponse(s: Biquad, omega: number): Complex {
  const z1: Complex = { re: Math.cos(omega), im: -Math.sin(omega) }
  const z2 = mul(z1, z1)
  const num: Complex = { re: s.b0 + s.b1 * z1.re + s.b2 * z2.re, im: s.b1 * z1.im + s.b2 * z2.im }
  const den: Complex = { re: 1 + s.a1 * z1.re + s.a2 * z2.re, im: s.a1 * z1.im + s.a2 * z2.im }
  return div(num, den)
}

/**
 * A DUT model's linear signal path: a cascade of sections, optionally followed by one delayed
 * reflection added to the direct path (the loudspeaker's wall) and a pure through delay (the
 * digital processor's latency). The same order the C# applies them in, minus the waveshaper and
 * the oscillator, which have no transfer function.
 */
export class Cascade {
  readonly sections: readonly Biquad[]
  readonly sampleRateHz: number
  readonly reflection: { delayMs: number; gain: number } | null
  readonly throughDelayMs: number

  constructor(
    sections: readonly Biquad[],
    sampleRateHz: number,
    reflection: { delayMs: number; gain: number } | null = null,
    throughDelayMs = 0,
  ) {
    this.sections = sections
    this.sampleRateHz = sampleRateHz
    this.reflection = reflection
    this.throughDelayMs = throughDelayMs
  }

  /** The exact complex response at `freqHz`, reflection and delay included. */
  responseAt(freqHz: number): Complex {
    const omega = (2 * Math.PI * freqHz) / this.sampleRateHz
    let h: Complex = { re: 1, im: 0 }
    for (const s of this.sections) h = mul(h, sectionResponse(s, omega))

    if (this.throughDelayMs > 0) {
      const n = Math.round((this.throughDelayMs * this.sampleRateHz) / 1000)
      h = mul(h, { re: Math.cos(omega * n), im: -Math.sin(omega * n) })
    }

    if (this.reflection) {
      const n = Math.round((this.reflection.delayMs * this.sampleRateHz) / 1000)
      const g = this.reflection.gain
      h = mul(h, { re: 1 + g * Math.cos(omega * n), im: -g * Math.sin(omega * n) })
    }

    return h
  }

  magnitudeDb(freqHz: number): number {
    return 20 * Math.log10(magnitude(this.responseAt(freqHz)))
  }

  /** Phase in degrees, wrapped to ±180. Callers wanting a continuous curve unwrap it themselves. */
  phaseDeg(freqHz: number): number {
    return phaseDeg(this.responseAt(freqHz))
  }

  /**
   * The impulse response, by running a unit impulse through the recursion — the same direct-form
   * II transposed update `SimulatedDut.Process` uses, so what is drawn is what the simulator would
   * emit for a single-sample kick.
   */
  impulse(sampleCount: number): Float64Array {
    const out = new Float64Array(sampleCount)
    const state = new Float64Array(this.sections.length * 2)
    const delaySamples = Math.round((this.throughDelayMs * this.sampleRateHz) / 1000)
    const delayRing = delaySamples > 0 ? new Float64Array(delaySamples) : null
    let delayCursor = 0
    const reflectionSamples = this.reflection ? Math.round((this.reflection.delayMs * this.sampleRateHz) / 1000) : 0
    const reflectionRing = reflectionSamples > 0 ? new Float64Array(reflectionSamples) : null
    let reflectionCursor = 0

    for (let n = 0; n < sampleCount; n++) {
      let x = n === 0 ? 1 : 0
      for (let s = 0; s < this.sections.length; s++) {
        const sec = this.sections[s]!
        const i = s * 2
        const y = sec.b0 * x + state[i]!
        state[i] = sec.b1 * x - sec.a1 * y + state[i + 1]!
        state[i + 1] = sec.b2 * x - sec.a2 * y
        x = y
      }
      if (delayRing) {
        const delayed = delayRing[delayCursor]!
        delayRing[delayCursor] = x
        delayCursor = (delayCursor + 1) % delayRing.length
        x = delayed
      }
      if (reflectionRing) {
        const delayed = reflectionRing[reflectionCursor]!
        reflectionRing[reflectionCursor] = x
        reflectionCursor = (reflectionCursor + 1) % reflectionRing.length
        x += this.reflection!.gain * delayed
      }
      out[n] = x
    }
    return out
  }
}
