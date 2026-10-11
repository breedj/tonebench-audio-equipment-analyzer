import { SIMULATION_SOURCE, type MeasurementSummary } from '../api/client'

/**
 * The History list's sections. Measured runs and imported simulations are both ordinary results of
 * the same measurement — see migration 014 for why a simulation is a marked row rather than a kind
 * of its own — so they arrive in one server-ordered list and are split here for display only.
 *
 * <p>In a `.ts` rather than inside the two components that render it because nothing in a `.vue`
 * file is testable under the node environment vitest runs in, and because both
 * MeasurementChecklist and MeasurementHistoryPanel have to split the same list the same way. They
 * had no grouping at all before this.</p>
 */
export type HistoryGroupId = 'measurements' | 'simulations'

export interface HistoryGroup {
  id: HistoryGroupId
  label: string
  items: MeasurementSummary[]
}

export function isSimulation(item: MeasurementSummary): boolean {
  return item.source === SIMULATION_SOURCE
}

/**
 * Splits a history listing into its sections, in display order, keeping the server's own ordering
 * (timestamp descending) within each.
 *
 * <p>An empty group is omitted entirely rather than rendered with a "none" line: a project with no
 * simulations in it — which is every project until someone imports one — should look exactly as it
 * did before this feature existed, with no header and no empty section.</p>
 *
 * <p>Measurements first, because they are what the screen is for; a simulation is the thing being
 * compared against. When a list holds *only* simulations the single section still carries its
 * header, so a row's provenance is never implied by position alone.</p>
 */
export function groupHistory(items: MeasurementSummary[]): HistoryGroup[] {
  const measurements = items.filter((item) => !isSimulation(item))
  const simulations = items.filter(isSimulation)

  const groups: HistoryGroup[] = []
  if (measurements.length > 0) groups.push({ id: 'measurements', label: 'Measurements', items: measurements })
  if (simulations.length > 0) groups.push({ id: 'simulations', label: 'Simulations', items: simulations })
  return groups
}

/**
 * Whether the section headers are worth drawing at all. One group is the ordinary case — a project
 * that has never had a simulation imported — and a lone "Measurements" header above a list that is
 * obviously measurements is noise.
 */
export function showsGroupHeaders(groups: HistoryGroup[]): boolean {
  return groups.length > 1
}
