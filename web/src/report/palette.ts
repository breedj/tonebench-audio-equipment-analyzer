import { measurementKindTone, type MeasurementKindTone } from '../measurements/kinds'

/**
 * A measurement kind's colour on paper.
 *
 * The 2026-10-13 report design gives every chapter its own hue — the band behind its heading, its
 * dot on the contents page — which is what makes a chapter findable by its edge in a report of
 * twenty. The hue is the kind's own, the same one its chip wears in the sidebar, so the paper and
 * the screen agree about which measurement is which.
 *
 * **Hex rather than the `--kind-*-chip-fg` variables** the app draws with: a PDF is laid out by the
 * server, which has no stylesheet, so the colour has to travel on the wire. These are the light
 * theme's own values; the dark theme's would be unreadable on white paper, and a report is always
 * white paper. Only the *ink* is sent — the renderer mixes the band's tint from it, so a pair that
 * does not belong together cannot arrive.
 *
 * Black and white is **not** handled here. `ReportDocument.monochrome` rides on the document and the
 * renderer substitutes its own greys for everything, accents included: a flag the server reads is
 * one place where an uncoloured document is guaranteed, where a client that remembered to grey nine
 * accents and forgot the tenth is not.
 */
const INK: Record<MeasurementKindTone, string> = {
  blue: '#1d4ed8',
  violet: '#6d28d9',
  pink: '#be185d',
  orange: '#c2410c',
  cyan: '#0e7490',
  red: '#b91c1c',
  indigo: '#4338ca',
  teal: '#0f766e',
  amber: '#b45309',
  green: '#15803d',
}

/** The chapter accent for a measurement kind, as `#rrggbb`. */
export function reportAccent(kind: string | null | undefined): string {
  return INK[measurementKindTone(kind)]
}

/** The accent every tone resolves to, for the test that pins the table against the tone union. */
export const REPORT_KIND_INK: Readonly<Record<MeasurementKindTone, string>> = INK
