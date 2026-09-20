import type { ImdMethodName } from '../api/client'

/**
 * A product's combination label from its (m, n) — see IntermodulationProduct server-side for the
 * convention. SMPTE: m is the sideband sign (±1) and n the multiple of f1, so "f2 − 2·f1".
 * CCIF: the product is |m·f1 − n·f2|, written with the larger term first so it reads as a
 * positive frequency — which term that is depends on the actual tones, not just on m and n
 * (2·f1 − f2 is 18 kHz for 19 + 20 kHz but "f2 − 2·f1" for 1 + 10 kHz).
 */
export function imdProductLabel(
  method: ImdMethodName,
  product: { m: number; n: number },
  tones: { toneLowHz: number; toneHighHz: number },
): string {
  const times = (k: number, tone: string) => (k === 1 ? tone : `${k}·${tone}`)
  if (method === 'Smpte') return `f2 ${product.m < 0 ? '−' : '+'} ${times(product.n, 'f1')}`
  const lowFirst = product.m * tones.toneLowHz >= product.n * tones.toneHighHz
  return lowFirst ? `${times(product.m, 'f1')} − ${times(product.n, 'f2')}` : `${times(product.n, 'f2')} − ${times(product.m, 'f1')}`
}
