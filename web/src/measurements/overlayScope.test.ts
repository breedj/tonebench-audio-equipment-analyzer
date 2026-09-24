import { describe, expect, it } from 'vitest'
import { foreignMeasurementName } from './overlayScope'

const LOOPBACK = { measurementTypeId: 1, measurementTypeName: 'Loopback 1 kHz' }
const AMP = { measurementTypeId: 5, measurementTypeName: 'Amp 1 kHz at 1 W' }

describe('foreignMeasurementName', () => {
  it('says nothing about an overlay of the measurement on screen', () => {
    expect(foreignMeasurementName(AMP, 5)).toBeNull()
  })

  it('names the measurement an overlay actually came from', () => {
    expect(foreignMeasurementName(LOOPBACK, 5)).toBe('Loopback 1 kHz')
  })

  // The case that was reported: two FFT measurements in one project, both with results checked on.
  // The Overlays list shows all six; four of them have a History row and two do not.
  it('marks only the rows the History checklist cannot show', () => {
    const loaded = [LOOPBACK, LOOPBACK, AMP, AMP, AMP, AMP]

    expect(loaded.map((o) => foreignMeasurementName(o, 5))).toEqual([
      'Loopback 1 kHz',
      'Loopback 1 kHz',
      null,
      null,
      null,
      null,
    ])
  })

  it('marks nothing while no measurement is open, since there is nothing to be foreign to', () => {
    expect(foreignMeasurementName(LOOPBACK, null)).toBeNull()
    expect(foreignMeasurementName(AMP, null)).toBeNull()
  })
})
