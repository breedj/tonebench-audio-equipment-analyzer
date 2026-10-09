import { describe, expect, it } from 'vitest'
import { manualPageForKind } from '../manual/pages'
import {
  CROSSTALK_KIND,
  groupedMeasurementKinds,
  isCrosstalkKind,
  isFrequencyResponseKind,
  isImpedanceKind,
  isOutputImpedanceKind,
  isSweepKind,
  MEASUREMENT_KIND_GROUPS,
  MEASUREMENT_KINDS,
  OUTPUT_IMPEDANCE_KIND,
} from './kinds'

describe('measurement kinds', () => {
  it('lists crosstalk as a kind of its own, with a manual page', () => {
    expect(MEASUREMENT_KINDS.some((k) => k.kind === CROSSTALK_KIND)).toBe(true)
    expect(manualPageForKind(CROSSTALK_KIND)).toBeTruthy()
  })

  it('lists output impedance as a kind of its own, with a manual page, outside the impedance family', () => {
    expect(MEASUREMENT_KINDS.some((k) => k.kind === OUTPUT_IMPEDANCE_KIND)).toBe(true)
    expect(manualPageForKind(OUTPUT_IMPEDANCE_KIND)).toBeTruthy()
    expect(isOutputImpedanceKind(OUTPUT_IMPEDANCE_KIND)).toBe(true)
    expect(isImpedanceKind(OUTPUT_IMPEDANCE_KIND)).toBe(false)
    expect(isFrequencyResponseKind(OUTPUT_IMPEDANCE_KIND)).toBe(false)
    expect(isOutputImpedanceKind('Impedance')).toBe(false)
  })

  it('keeps crosstalk out of every other family: it shares the response rows, not the response screen', () => {
    expect(isCrosstalkKind(CROSSTALK_KIND)).toBe(true)
    expect(isFrequencyResponseKind(CROSSTALK_KIND)).toBe(false)
    expect(isImpedanceKind(CROSSTALK_KIND)).toBe(false)
    expect(isSweepKind(CROSSTALK_KIND)).toBe(false)
    expect(isCrosstalkKind('FrequencyResponse')).toBe(false)
    expect(isCrosstalkKind(null)).toBe(false)
  })

  // The "new measurement" picker lists `groupedMeasurementKinds()`, so a kind whose group nothing
  // shows would simply not be creatable — with nothing failing, since the grid it fell out of still
  // renders. These three pin the whole path from the array to the sections.
  it('shows every kind exactly once across the picker’s sections', () => {
    const shown = groupedMeasurementKinds().flatMap((s) => s.kinds.map((k) => k.kind))
    expect([...shown].sort()).toEqual(MEASUREMENT_KINDS.map((k) => k.kind).sort())
    expect(new Set(shown).size).toBe(shown.length)
  })

  it('keeps each section in MEASUREMENT_KINDS order, and drops a section with nothing in it', () => {
    const distortion = groupedMeasurementKinds().find((s) => s.group.id === 'distortion')
    expect(distortion?.kinds.map((k) => k.kind)).toEqual(
      MEASUREMENT_KINDS.filter((k) => k.group === 'distortion').map((k) => k.kind),
    )
    expect(groupedMeasurementKinds([]).length).toBe(0)
    expect(groupedMeasurementKinds(MEASUREMENT_KINDS.filter((k) => k.group === 'channels')).map((s) => s.group.id)).toEqual([
      'channels',
    ])
  })

  it('gives every group a distinct position and every kind a tone', () => {
    const ids = MEASUREMENT_KIND_GROUPS.map((g) => g.id)
    expect(new Set(ids).size).toBe(ids.length)
    for (const k of MEASUREMENT_KINDS) expect(k.tone).toBeTruthy()
  })
})
