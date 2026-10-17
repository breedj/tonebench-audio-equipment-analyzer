/**
 * Which measurement produced the live state each screen is showing.
 *
 * <p>A measurement's *settings* are per type and restored on selection (params_json, see
 * useProjects' `selectType`), and each kind's *runner* has its own store (see the header of
 * useFrequencyResponse.ts). What had no owner at all is the state a run leaves behind on screen,
 * and one of those is shared by every measurement in the app: the live FFT display — the spectrum,
 * the meters, the harmonics, the oscilloscope, the IMD/multitone tables, and the retained snapshot
 * on the server that <b>Save measurement result</b> reads. Every runner drives the same
 * `SpectrumPipeline`, so every run writes it, whatever kind it is.</p>
 *
 * <p>So a stepped-sine sweep left its last point's spectrum on the FFT analysis screen of an
 * unrelated FFT measurement: selecting that measurement re-applied its settings and its axis
 * ranges, and then drew the sweep's record inside them. Reported 2026-09-27. Worse than it looks,
 * because the server's retained snapshot goes with it — <b>Save</b> on that screen would have
 * stored the sweep's capture as the FFT measurement's result.</p>
 *
 * <p>The fix is an owner per live store rather than a blanket clear on every switch: a blanket clear
 * would throw away a five-minute sweep the moment its own measurement was clicked away from and
 * back, which is the far more common thing to do. A store is touched only when the measurement now
 * on screen is not the one that filled it.</p>
 *
 * <p><b>Touched, not discarded — every store is now <i>parked</i> under the measurement it belongs
 * to and handed back on return.</b> Discarding was the first answer and it was half right: the two
 * must not show each other's run, but throwing the first one's away to achieve that is the same
 * loss by another route, reported twice (the FFT display 2026-10-16, the sweeps 2026-10-17). The
 * FFT display is parked in App.vue's own `fftDisplays`; all eight runners are parked on the
 * <i>server</i>, because Save reads the runner and not the browser — see RunParking and
 * ThdSweepRunner.SwitchOwner. What is left here is the bookkeeping that decides when a switch has
 * to happen at all, plus the clear path, which no store reaches today and which a store added
 * later with no park of its own still gets.</p>
 *
 * <p>Pure, and therefore tested — nothing in App.vue is.</p>
 */

/**
 * The live stores a run fills. One per server-side runner, plus `fft` for the shared live display.
 *
 * <p>Only the FFT analysis screen *reads* `fft`, but every measurement *writes* it — one
 * `SpectrumPipeline`, one set of frames — which is why {@link claimLive} always takes it, including
 * for the kinds that never show it. (The sweep screens' own FFT and oscilloscope previews are not
 * this: they are drawn from each sweep point's stored preview, see SweepFftPreviewChart.)</p>
 */
export type LiveStore =
  | 'fft'
  | 'sweep'
  | 'imdSweep'
  | 'chirp'
  | 'stepped'
  | 'crosstalk'
  | 'impedance'
  | 'impulse'
  | 'outputImpedance'

/**
 * Who a store's contents belong to: the project and the measurement type together, never the type id
 * alone. Type ids are per project (`AUTOINCREMENT` inside one `.tbench` file), so the first
 * measurement of every project is id 1 — and an owner recorded as "1" would make the previous
 * project's spectrum look like the new project's own, which is the one case this whole module exists
 * to stop.
 */
export function liveOwnerKey(projectId: number | null, typeId: number | null): string | null {
  return projectId === null || typeId === null ? null : `${projectId}:${typeId}`
}

/** Owner key per store, or absent for a store nothing has filled since the app started. */
export type LiveOwners = Partial<Record<LiveStore, string>>

/**
 * The store a kind's screen reads beyond the shared live display, or null for a kind that has no
 * runner of its own (FFT analysis, whose whole result <i>is</i> the live display).
 *
 * <p>Keyed on the same `kind` strings as `measurements/kinds.ts`; a new kind with a new runner needs
 * an arm here, or its screen keeps adopting whatever the previous measurement of its kind
 * measured.</p>
 */
export function liveStoreForKind(kind: string | null | undefined): LiveStore | null {
  switch (kind) {
    case 'ThdVsFrequency':
    case 'ThdVsAmplitude':
      return 'sweep'
    case 'ImdVsAmplitude':
      return 'imdSweep'
    case 'FrequencyResponse':
      return 'chirp'
    case 'SteppedResponse':
      return 'stepped'
    case 'Crosstalk':
      return 'crosstalk'
    case 'Impedance':
      return 'impedance'
    case 'ImpulseResponse':
      return 'impulse'
    case 'OutputImpedance':
      return 'outputImpedance'
    default:
      return null
  }
}

/**
 * The one store a kind's screen actually <i>draws</i> from: its own runner's, or — for FFT analysis,
 * whose result is the live display itself — `fft`.
 *
 * <p>Distinct from {@link liveStoreForKind}, and the distinction is the difference between reading
 * and writing. A stepped sweep <b>writes</b> the live display (claimLive takes `fft` for it) but
 * never shows it, so switching *to* a sweep screen must not discard it: run an FFT capture, glance
 * at a sweep's settings, come back — and clearing on the way out would have thrown the capture away
 * for a screen that could not have shown it. Measured 2026-09-27; that is exactly what the first cut
 * of this did.</p>
 */
export function liveScreenStore(kind: string | null | undefined): LiveStore {
  return liveStoreForKind(kind) ?? 'fft'
}

/**
 * Records that `owner` has just started a run into `store`. Takes `fft` as well, always, because the
 * run overwrites the live display whether its own screen shows it or not — which is also why a kind
 * with no runner of its own passes null and still claims something.
 *
 * <p>A null `owner` (no measurement open — nothing to save and nothing to switch away from) leaves
 * the map alone rather than stamping an owner no later switch can match.</p>
 */
export function claimLive(owners: LiveOwners, store: LiveStore | null, owner: string | null): LiveOwners {
  if (owner === null) return owners
  const next: LiveOwners = { ...owners, fft: owner }
  if (store !== null) next[store] = owner
  return next
}

/**
 * Of `stores`, the ones holding another measurement's state — what has to be discarded before
 * `owner`'s screen reads them.
 *
 * <p>A store with no owner is never foreign, and that is what keeps a reload mid-run re-attaching:
 * the run belongs to the server, this map does not survive the page, and an unowned store is adopted
 * exactly as it was before this existed.</p>
 */
export function foreignLiveStores(
  owners: LiveOwners,
  owner: string | null,
  stores: readonly (LiveStore | null)[],
): LiveStore[] {
  const out: LiveStore[] = []
  for (const store of stores) {
    if (store === null || out.includes(store)) continue
    const held = owners[store]
    if (held !== undefined && held !== owner) out.push(store)
  }
  return out
}

/** Forgets the owners of the stores just discarded, so the next switch does not discard them twice. */
export function releaseLive(owners: LiveOwners, stores: readonly LiveStore[]): LiveOwners {
  if (stores.length === 0) return owners
  const next = { ...owners }
  for (const store of stores) delete next[store]
  return next
}
