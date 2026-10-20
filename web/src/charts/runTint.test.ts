import { describe, expect, it } from 'vitest'
import { RUN_TINTS, mixHex, runTint } from './runTint'

/** The two bases the impedance chart mixes into, light theme then dark. */
const IMPEDANCE = ['#111827', '#f3f4f6']
const PHASE = ['#7c3aed', '#a78bfa']

/** Perceptual-ish distance, enough to say "these read as different colours". */
function distance(a: string, b: string): number {
  const channels = (hex: string) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16))
  const [x, y] = [channels(a), channels(b)]
  return Math.sqrt(x.reduce((sum, c, i) => sum + ((c - y[i]) ** 2), 0))
}

describe('mixHex', () => {
  it('returns the base at 0 and the tint at 1', () => {
    expect(mixHex('#000000', '#ffffff', 0)).toBe('#000000')
    expect(mixHex('#000000', '#ffffff', 1)).toBe('#ffffff')
  })

  it('mixes halfway', () => {
    expect(mixHex('#000000', '#ffffff', 0.5)).toBe('#808080')
    expect(mixHex('#102030', '#302010', 0.5)).toBe('#202020')
  })

  it('accepts the short form', () => {
    expect(mixHex('#abc', '#abc', 0.5)).toBe('#aabbcc')
  })

  it('falls back to the base rather than inventing a colour', () => {
    // A theme could hand it a var() or an rgb(); better the untinted curve than a broken one.
    expect(mixHex('var(--x)', '#2563eb', 0.5)).toBe('var(--x)')
    expect(mixHex('#112233', 'rebeccapurple', 0.5)).toBe('#112233')
  })

  it('clamps an out-of-range amount', () => {
    expect(mixHex('#000000', '#ffffff', -1)).toBe('#000000')
    expect(mixHex('#000000', '#ffffff', 5)).toBe('#ffffff')
  })
})

describe('runTint', () => {
  it('leaves run 0 alone, as runDash leaves it solid', () => {
    for (const base of [...IMPEDANCE, ...PHASE]) {
      expect(runTint(base, 0)).toBe(base)
      expect(runTint(base, -1)).toBe(base)
    }
  })

  it('gives every run on screen a colour of its own', () => {
    // Four runs: the live measurement plus the three history items the overlay cap allows.
    for (const base of [...IMPEDANCE, ...PHASE]) {
      const colours = [0, 1, 2, 3].map((run) => runTint(base, run))
      expect(new Set(colours).size).toBe(4)

      for (let i = 0; i < colours.length; i++) {
        for (let j = i + 1; j < colours.length; j++)
          expect(distance(colours[i], colours[j])).toBeGreaterThan(20)
      }
    }
  })

  it('keeps the two quantities apart rather than converging them', () => {
    // The point of "subtle". Pulling both bases toward one tint by the same amount scales the gap
    // between them by (1 - amount), so the quantities necessarily converge a little; what must not
    // happen is that |Z| and phase of the *same* run become hard to tell apart. Asserted as a
    // fraction of the untinted gap rather than as an absolute, because the two bases are 130 apart
    // in the light theme and 75 in the dark one -- a tinted near-white |Z| is legitimately closer to
    // a light violet phase than to its own base, which is why this is not the other assertion.
    for (let theme = 0; theme < 2; theme++) {
      const base = distance(IMPEDANCE[theme], PHASE[theme])
      for (let run = 1; run <= RUN_TINTS.length; run++) {
        const gap = distance(runTint(IMPEDANCE[theme], run), runTint(PHASE[theme], run))
        expect(gap).toBeGreaterThan(base * 0.5)
      }
    }
  })

  it('wraps past the tints it has, the way runDash wraps its patterns', () => {
    expect(runTint('#111827', RUN_TINTS.length + 1)).toBe(runTint('#111827', 1))
  })
})
