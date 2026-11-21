/**
 * The figure on each math tile: the two operands and what the operation makes of them.
 *
 * <p><b>Why not the operator's own sign</b>, which is what these tiles carried until 2026-11-20.
 * The tile already says <code>A − B</code> in mono beside the figure, so a minus sign drawn next to
 * it is the same information twice — where a picture of two traces and the third one that comes out
 * of them is the thing the label cannot say. It is also the difference between the four binary
 * operations at a glance: a sum and a difference of the same pair are two obviously different
 * curves, and two signs are two characters.</p>
 *
 * <p><b>Computed from the operation rather than drawn by hand.</b> Each result path is the real
 * arithmetic applied to the two operand paths, so a figure cannot drift from what the operation
 * does — and <code>A × B</code> really does draw a carrier being modulated, because that is what
 * multiplying those two signals gives. Hand-drawn, seven of these would be seven guesses.</p>
 *
 * <p><b>The operand curves are chosen to make each operation legible</b>, which is the one place
 * judgement comes in: B is a fast ripple on a slow A, so a sum and a difference separate; the
 * multiplier is a plain carrier, so the product reads as modulation; the divisor is held
 * <em>positive</em>, because a divisor through zero rails the quotient and draws a picture of the
 * clamp rather than of division. The derivative is shown on a triangle (whose slope is a square)
 * and the integral on a square (whose integral is a triangle), which are the two cases where the
 * answer is a shape a reader already knows.</p>
 */

/** The box every math figure is drawn in — wider than the app's other glyph sets, like the well. */
export const MATH_FIGURE_WIDTH = 40
export const MATH_FIGURE_HEIGHT = 24

/** Where the traces start and end, leaving the stroke room inside the box. */
const X0 = 3
const X1 = 37

/** The zero line, and how many units one unit of signal is worth. */
const MID = MATH_FIGURE_HEIGHT / 2
const SCALE = 7

/**
 * How far a trace may run before it is clipped.
 *
 * <p>1.35 rather than the box's own 1.7, so a railed quotient sits visibly inside the well instead
 * of being cut off by it — a clipped curve reads as a drawing error where a flattened one reads as
 * the clamp it is.</p>
 */
const LIMIT = 1.35

/** How many points a curve is drawn with. One per pixel at the size the tiles draw it. */
const STEPS = 64

const TAU = 2 * Math.PI

function trace(fn: (u: number) => number): string {
  let d = ''
  for (let i = 0; i <= STEPS; i += 1) {
    const u = i / STEPS
    const v = Math.max(-LIMIT, Math.min(LIMIT, fn(u)))
    const x = X0 + (X1 - X0) * u
    const y = MID - SCALE * v
    d += `${i ? 'L' : 'M'}${x.toFixed(2)},${y.toFixed(2)} `
  }
  return d.trim()
}

/** The slow operand every figure opens with. */
const a = (u: number) => 0.75 * Math.sin(TAU * 1.5 * u)
/** A ripple fast enough to tell a sum from a difference at a glance. */
const b = (u: number) => 0.35 * Math.sin(TAU * 5 * u)
/** A plain carrier, so the product reads as modulation rather than as noise. */
const carrier = (u: number) => Math.sin(TAU * 6 * u)
/** A divisor that never reaches zero — see the note on division above. */
const divisor = (u: number) => 1.25 + 0.5 * Math.sin(TAU * 4 * u)
/** A triangle, whose slope is a square. */
const triangle = (u: number) => {
  const p = (u * 2) % 1
  return (p < 0.5 ? 4 * p - 1 : 3 - 4 * p) * 0.8
}
/** A square, whose integral is a triangle. */
const square = (u: number) => ((u * 2) % 1 < 0.5 ? 0.6 : -0.6)

/** One tile's figure: the operands that go in, and the trace that comes out. */
export interface MathFigure {
  /** The first operand. */
  a: string
  /** The second, or null where the operation reads one trace — the three unary tiles. */
  b: string | null
  /** What the operation draws. */
  result: string
}

/**
 * Every figure, by the operation its tile stands for.
 *
 * <p>Keyed by the <b>tile's</b> operation rather than by every stored operation: a tile and its
 * mirror (<code>B − A</code> for <code>A − B</code>) draw the same picture, because what the figure
 * is about is the arithmetic and not which socket is in which slot.</p>
 */
export const SCOPE_MATH_FIGURES: Readonly<Record<string, MathFigure>> = Object.freeze({
  AMinusB: { a: trace(a), b: trace(b), result: trace((u) => a(u) - b(u)) },
  APlusB: { a: trace(a), b: trace(b), result: trace((u) => a(u) + b(u)) },
  ATimesB: {
    a: trace(a),
    b: trace((u) => 0.35 * carrier(u)),
    result: trace((u) => 1.3 * a(u) * carrier(u)),
  },
  ADivB: {
    a: trace(a),
    // Drawn about zero although the divisor itself never crosses it: the figure is a picture of a
    // signal, and a trace pinned to the top of the well would read as a clipped one.
    b: trace((u) => divisor(u) - 1.25),
    result: trace((u) => (1.25 * a(u)) / divisor(u)),
  },
  NegateA: { a: trace(a), b: null, result: trace((u) => -a(u)) },
  DerivativeA: {
    a: trace(triangle),
    b: null,
    result: trace((u) => ((u * 2) % 1 < 0.5 ? 0.85 : -0.85)),
  },
  IntegralA: {
    a: trace(square),
    b: null,
    result: trace((u) => {
      const p = (u * 2) % 1
      return (p < 0.5 ? 4 * p - 1 : 3 - 4 * p) * 0.85
    }),
  },
})

/** One operation's figure, or null for a name there is none for. */
export function scopeMathFigure(operation: string): MathFigure | null {
  return SCOPE_MATH_FIGURES[operation] ?? null
}
