import { describe, expect, it } from 'vitest'
import { REPORT_KIND_INK, reportAccent } from './palette'
import { MEASUREMENT_KINDS, measurementKindTone } from '../measurements/kinds'

describe('the report accent', () => {
  it('gives every measurement kind the hue its own chip wears', () => {
    // The table is keyed on the tone union, so a *new tone* fails to compile. A new kind cannot: it
    // reaches this through measurementKindTone, which is what this case checks.
    for (const kind of MEASUREMENT_KINDS) {
      expect(reportAccent(kind.kind), kind.kind).toBe(REPORT_KIND_INK[kind.tone])
    }
  })

  it('is a six-digit hex everywhere, because that is all the renderer parses', () => {
    for (const value of Object.values(REPORT_KIND_INK)) {
      expect(value).toMatch(/^#[0-9a-f]{6}$/)
    }
  })

  it('falls back to a colour rather than to nothing for a kind nobody has listed', () => {
    // measurementKindTone answers for an unknown kind too; a chapter with no accent would be the one
    // colourless band in a coloured report, which reads as a rendering fault.
    expect(reportAccent('SomethingNew')).toBe(REPORT_KIND_INK[measurementKindTone('SomethingNew')])
    expect(reportAccent(null)).toMatch(/^#[0-9a-f]{6}$/)
  })
})
