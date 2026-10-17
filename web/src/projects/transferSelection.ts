/**
 * The arithmetic behind the Export and Import dialogs: what is ticked, what clashes with what, how
 * large the transfer is, and whether it can go ahead.
 *
 * In a module rather than in the dialogs because nothing in a `.vue` is testable under the node
 * environment vitest runs in here — the same reason `railCollapse.ts`, `freqRangePresets.ts` and
 * `hints.ts` exist — and because both dialogs ask these questions, so one answer keeps them from
 * disagreeing about a selection the server then treats as one thing.
 */
import { isKnownMeasurementKind, measurementKindLabel } from '../measurements/kinds'
import type { ProjectContentsResult, ProjectContentsType, ProjectTransfer } from '../api/client'

/** What to do with an incoming measurement whose name the destination already has. */
export type NameClashPolicy = 'Rename' | 'Merge' | 'Skip'

export const NAME_CLASH_POLICIES: readonly NameClashPolicy[] = ['Rename', 'Merge', 'Skip']

/**
 * What is ticked. Two sets rather than one, because a measurement can legitimately be taken
 * *without* any of its results — "send them the settings, not the runs", which is the same thing
 * Save As without results does for a whole project — and that is not expressible as a set of
 * result ids.
 */
export interface TransferSelectionState {
  readonly types: ReadonlySet<number>
  readonly results: ReadonlySet<number>
}

export const EMPTY_SELECTION: TransferSelectionState = { types: new Set(), results: new Set() }

/** One measurement's selection state, for its checkbox. */
export type GroupState = 'all' | 'none' | 'some'

/**
 * `some` covers two different partial states on purpose: a few of the results ticked, and the
 * measurement ticked with none of them. Both mean "this is going but not all of it", which is what
 * the indeterminate checkbox says.
 */
export function groupState(type: ProjectContentsType, selection: TransferSelectionState): GroupState {
  if (!selection.types.has(type.typeId)) return 'none'
  if (type.results.length === 0) return 'all'

  const picked = type.results.filter((r) => selection.results.has(r.resultId)).length
  return picked === type.results.length ? 'all' : 'some'
}

/** Ticking a measurement takes it and every result under it; unticking takes both away. */
export function setGroup(
  selection: TransferSelectionState,
  type: ProjectContentsType,
  on: boolean,
): TransferSelectionState {
  const types = new Set(selection.types)
  const results = new Set(selection.results)

  if (on) {
    types.add(type.typeId)
    for (const result of type.results) results.add(result.resultId)
  } else {
    types.delete(type.typeId)
    for (const result of type.results) results.delete(result.resultId)
  }

  return { types, results }
}

/**
 * Ticking a result implies its measurement — a result cannot travel without the measurement it
 * belongs to. Unticking the last one leaves the measurement ticked, which is the settings-only
 * pick; the group's own checkbox is how you drop it entirely.
 */
export function setResult(
  selection: TransferSelectionState,
  type: ProjectContentsType,
  resultId: number,
  on: boolean,
): TransferSelectionState {
  const types = new Set(selection.types)
  const results = new Set(selection.results)

  if (on) {
    types.add(type.typeId)
    results.add(resultId)
  } else {
    results.delete(resultId)
  }

  return { types, results }
}

export function setEverything(types: readonly ProjectContentsType[], on: boolean): TransferSelectionState {
  if (!on) return EMPTY_SELECTION
  return {
    types: new Set(types.map((t) => t.typeId)),
    results: new Set(types.flatMap((t) => t.results.map((r) => r.resultId))),
  }
}

/**
 * Which incoming measurements the destination already has a live measurement of the same name for,
 * mapped to that measurement.
 *
 * <p>The comparison is **case-sensitive**, and deliberately: `measurement_type.name` carries no
 * `COLLATE NOCASE`, so SQLite's unique index is a binary compare and "amp" and "Amp" coexist
 * happily. A case-insensitive check here would warn about a clash the server is not going to have,
 * and offer a Merge that would land the results under a measurement with a different name.</p>
 */
export function clashingTypes(
  incoming: readonly ProjectContentsType[],
  destination: readonly ProjectContentsType[],
): Map<number, ProjectContentsType> {
  const byName = new Map(destination.map((t) => [t.name, t]))
  const clashes = new Map<number, ProjectContentsType>()

  for (const type of incoming) {
    const existing = byName.get(type.name)
    if (existing) clashes.set(type.typeId, existing)
  }

  return clashes
}

/**
 * The incoming measurements whose chosen policy is Merge but whose kind differs from the one they
 * would merge into. The server refuses these outright — an FFT result under a sweep measurement is
 * a 500 the moment that measurement is selected — so the dialog has to stop before it asks.
 */
export function mergeBlocked(
  incoming: readonly ProjectContentsType[],
  clashes: ReadonlyMap<number, ProjectContentsType>,
  policyFor: (typeId: number) => NameClashPolicy,
): number[] {
  return incoming
    .filter((type) => {
      const existing = clashes.get(type.typeId)
      return existing !== undefined && policyFor(type.typeId) === 'Merge' && existing.kind !== type.kind
    })
    .map((type) => type.typeId)
}

/**
 * Kinds in the file that this build has never heard of.
 *
 * <p>A separate guard from the schema version, because a newer build can add a measurement kind
 * *without* a migration — Crosstalk and Output impedance both reuse `response_trace` — so a file at
 * this build's schema version can still hold a kind nothing here can draw. Imported, it would be a
 * sidebar row with no glyph and a 500 out of the apply endpoint the moment it was selected.</p>
 */
export function unknownKinds(types: readonly ProjectContentsType[]): string[] {
  return [...new Set(types.filter((t) => !isKnownMeasurementKind(t.kind)).map((t) => t.kind))].sort()
}

/** How many bytes the ticked results come to. A measurement with none of its results ticked adds nothing. */
export function selectedBytes(
  types: readonly ProjectContentsType[],
  selection: TransferSelectionState,
): number {
  let total = 0
  for (const type of types) {
    if (!selection.types.has(type.typeId)) continue
    for (const result of type.results) {
      if (selection.results.has(result.resultId)) total += result.approximateBytes
    }
  }
  return total
}

export function selectedResultCount(
  types: readonly ProjectContentsType[],
  selection: TransferSelectionState,
): number {
  return types
    .filter((t) => selection.types.has(t.typeId))
    .reduce((n, t) => n + t.results.filter((r) => selection.results.has(r.resultId)).length, 0)
}

const UNITS = ['bytes', 'kB', 'MB', 'GB', 'TB'] as const

/**
 * A size for a human. Binary steps, because that is what a file manager will show for the same
 * file. Guards a missing or nonsense figure rather than printing `NaN`: the server sends a whole
 * number of bytes, but an older build's response would not send the field at all.
 */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes <= 0) return '0 bytes'

  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024
    unit += 1
  }

  // Whole bytes and kB; a decimal from MB up, where one is the difference between "512 MB" and
  // "512.4 MB" and worth reading before attaching the thing to an e-mail.
  const digits = unit <= 1 ? 0 : value < 10 ? 1 : 0
  return `${value.toFixed(digits)} ${UNITS[unit]}`
}

/** What the request carries for one measurement. Mirrors `ProjectTransferItemRequest`. */
export interface TransferItem {
  typeId: number
  resultIds: number[]
  onNameClash?: NameClashPolicy
}

/**
 * The ticked selection as the endpoint wants it. A per-measurement policy rides along only where
 * one was actually chosen — the request's own default covers the rest, and sending the resolved
 * value for every item would make a later change to that default silently not apply.
 */
export function transferItems(
  types: readonly ProjectContentsType[],
  selection: TransferSelectionState,
  policyOverrides: ReadonlyMap<number, NameClashPolicy> = new Map(),
): TransferItem[] {
  return types
    .filter((type) => selection.types.has(type.typeId))
    .map((type) => {
      const override = policyOverrides.get(type.typeId)
      return {
        typeId: type.typeId,
        resultIds: type.results.filter((r) => selection.results.has(r.resultId)).map((r) => r.resultId),
        ...(override ? { onNameClash: override } : {}),
      }
    })
}

export interface TransferReadiness {
  /** Every measurement the dialog is showing. */
  types: readonly ProjectContentsType[]
  selection: TransferSelectionState
  /** False while no destination (export) or no source (import) has been chosen. */
  hasTarget: boolean
  busy: boolean
  blocked: readonly number[]
  policyFor: (typeId: number) => NameClashPolicy
}

/**
 * Whether the button can be pressed, and if not, the one line saying why. Returning the reason
 * from the same place that decides it is what keeps a disabled button from being a mystery.
 */
export function transferReadiness(state: TransferReadiness): { ready: boolean; reason: string | null } {
  if (state.busy) return { ready: false, reason: null }
  if (!state.hasTarget) return { ready: false, reason: 'Choose a file first.' }

  const picked = state.types.filter((t) => state.selection.types.has(t.typeId))
  if (picked.length === 0) return { ready: false, reason: 'Tick at least one measurement.' }

  if (state.blocked.length > 0) {
    return {
      ready: false,
      reason:
        state.blocked.length === 1
          ? 'One measurement cannot be merged into the one of that name here. Change its choice, or untick it.'
          : `${state.blocked.length} measurements cannot be merged into the ones of those names here. Change their choices, or untick them.`,
    }
  }

  // Everything ticked is being skipped, so pressing the button would copy nothing and report
  // success. Saying so beats letting it run and answering 0.
  if (picked.every((t) => state.policyFor(t.typeId) === 'Skip')) {
    return { ready: false, reason: 'Every measurement ticked is set to be skipped, so nothing would be copied.' }
  }

  return { ready: true, reason: null }
}

/** What the status bar says afterwards, in the server's own numbers. */
export function transferSummary(report: ProjectTransfer, direction: 'exported' | 'imported'): string {
  const created = report.types.filter((t) => t.outcome === 'Created')
  const merged = report.types.filter((t) => t.outcome === 'Merged')
  const skipped = report.types.filter((t) => t.outcome === 'Skipped')
  const renamed = created.filter((t) => t.destinationName !== null && t.destinationName !== t.sourceName)

  const parts = [`${count(report.resultsCopied, 'result')} ${direction}`]
  if (created.length > 0) parts.push(`${count(created.length, 'new measurement')}`)
  if (merged.length > 0) parts.push(`${count(merged.length, 'measurement')} merged`)
  if (renamed.length > 0) parts.push(`renamed ${renamed.map((t) => `“${t.destinationName}”`).join(', ')}`)
  if (skipped.length > 0) parts.push(`skipped ${skipped.map((t) => `“${t.sourceName}”`).join(', ')}`)

  return `${parts.join(' · ')}.`
}

function count(n: number, noun: string): string {
  return `${n} ${noun}${n === 1 ? '' : 's'}`
}

/** A result's one-line description for its row. */
export function resultLabel(result: ProjectContentsResult): string {
  return result.source === 'simulation' ? `${result.name} (simulation)` : result.name
}

export { measurementKindLabel }
