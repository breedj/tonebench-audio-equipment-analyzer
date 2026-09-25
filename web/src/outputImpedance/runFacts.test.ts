import { describe, expect, it } from 'vitest'
import { outputImpedanceRunFactsFromParams } from './display'

describe('outputImpedanceRunFactsFromParams', () => {
  // The case that was reported: a saved complex run showed no damping factor and a greyed-out
  // Phase box, because the screen asked the idle runner instead of the result.
  it('reads a complex run back off its own params', () => {
    const params = JSON.stringify({ rightChannelIsReference: true, nominalOhms: 8, loadOhms: 8 })

    expect(outputImpedanceRunFactsFromParams(params)).toEqual({ rightChannelIsReference: true, nominalOhms: 8 })
  })

  it('reads a magnitude-only run as having no phase', () => {
    const params = JSON.stringify({ rightChannelIsReference: false, nominalOhms: 4 })

    expect(outputImpedanceRunFactsFromParams(params)).toEqual({ rightChannelIsReference: false, nominalOhms: 4 })
  })

  // Null rather than a default, so the caller falls back to the live run instead of asserting
  // something the stored row never said.
  it('reports absent fields as null', () => {
    expect(outputImpedanceRunFactsFromParams(JSON.stringify({}))).toEqual({
      rightChannelIsReference: null,
      nominalOhms: null,
    })
    expect(outputImpedanceRunFactsFromParams(null)).toEqual({ rightChannelIsReference: null, nominalOhms: null })
    expect(outputImpedanceRunFactsFromParams('{')).toEqual({ rightChannelIsReference: null, nominalOhms: null })
  })

  // `nominal / |Z|` is undefined at zero, and a damping-factor axis built from it draws nothing —
  // which is indistinguishable from the bug this helper exists to fix.
  it('treats a zero or negative nominal load as absent', () => {
    expect(outputImpedanceRunFactsFromParams(JSON.stringify({ nominalOhms: 0 })).nominalOhms).toBeNull()
    expect(outputImpedanceRunFactsFromParams(JSON.stringify({ nominalOhms: -8 })).nominalOhms).toBeNull()
  })

  it('ignores fields of the wrong type rather than coercing them', () => {
    const params = JSON.stringify({ rightChannelIsReference: 'yes', nominalOhms: '8' })

    expect(outputImpedanceRunFactsFromParams(params)).toEqual({ rightChannelIsReference: null, nominalOhms: null })
  })
})
