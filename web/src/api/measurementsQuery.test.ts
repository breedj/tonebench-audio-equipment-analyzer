import { describe, expect, it } from 'vitest'
import { measurementsQuery } from './client'

/**
 * Pins the filter's query parameter <em>names</em> against
 * `MeasurementsEndpoints`' handler signature.
 *
 * <p>This is not pedantry about spelling. Minimal APIs bind query parameters by name and silently
 * ignore one they do not recognise, so a misspelled filter does not fail — the server returns the
 * whole unfiltered list and the caller cannot tell. That has already caused real damage once: a
 * request for one measurement type's results came back with every result in the database, and the
 * caller deleted all of them believing the filter had applied.</p>
 *
 * <p>So the failure mode is "fails open, silently, on a rename" — which is exactly the shape a test
 * has to cover, because nothing else will.</p>
 */
describe('measurementsQuery', () => {
  it('sends the names the server binds', () => {
    const query = measurementsQuery({
      projectId: 3,
      measurementTypeId: 6,
      from: '2026-08-01T00:00:00.000Z',
      to: '2026-09-01T00:00:00.000Z',
      skip: 10,
      take: 25,
    })

    // Asserted individually so a failure names the parameter that drifted.
    expect(query).toContain('projectId=3')
    expect(query).toContain('measurementTypeId=6')
    expect(query).toContain('skip=10')
    expect(query).toContain('take=25')
    expect(query).toContain(`from=${encodeURIComponent('2026-08-01T00:00:00.000Z')}`)
    expect(query).toContain(`to=${encodeURIComponent('2026-09-01T00:00:00.000Z')}`)
  })

  /**
   * The old name, which the server no longer binds. Kept as an explicit assertion rather than left
   * implicit: a half-finished rename that sent both would look right in every manual check and
   * still filter correctly, until the day the old one was the only one left.
   */
  it('does not send the old typeId spelling', () => {
    expect(measurementsQuery({ measurementTypeId: 6 })).not.toMatch(/[?&]typeId=/)
  })

  it('omits absent filters rather than sending empty values', () => {
    expect(measurementsQuery({})).toBe('/measurements')
    expect(measurementsQuery({ measurementTypeId: 6 })).toBe('/measurements?measurementTypeId=6')
  })

  /**
   * Zero is a real project id nowhere in this app, but it is a real <em>skip</em>, and a builder
   * that dropped falsy values would silently page from the wrong place.
   */
  it('keeps a zero skip', () => {
    expect(measurementsQuery({ skip: 0, take: 50 })).toContain('skip=0')
  })
})
