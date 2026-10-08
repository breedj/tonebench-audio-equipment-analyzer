import type { GeneratorKind } from '../api/client'

/**
 * Which stimuli "Snap to bin" applies to, and what the checkbox should read when a measurement is
 * loaded.
 *
 * <p>Its own module because the rule existed twice in `GeneratorPanel.vue` — once as the checkbox's
 * enabled state and once, spelled differently, in the watcher that restores a stored measurement —
 * and the two disagreed. Nothing in a `.vue` is testable under the node environment vitest runs in,
 * which is why the copy that was wrong had nothing to say so.</p>
 */

/**
 * Snapping only ever rewrites a channel's `tones` list (see DeviceSessionService.SnapTones), so it
 * is a no-op for the kinds that have none. The multitone comb counts: the server snaps it tone by
 * tone and its TD+N reading depends on that — unsnapped, every gap between two tones reads their
 * own Hann sidelobes, about −60 dBr on a straight wire, as if it were distortion.
 */
export function canSnapToBin(kind: GeneratorKind): boolean {
  return kind === 'Tones' || kind === 'Multitone'
}

/**
 * The checkbox's state when a stored measurement is applied: the value it was saved with, unless
 * the stimulus it was saved against cannot snap at all — a measurement written before the kinds
 * were gated could carry `snapToBin` true alongside white noise, and that must not come back
 * checked.
 *
 * <p><b>`Off` is not a stimulus and must not be read as one.</b> It is the never-configured state,
 * and `GeneratorChannelPanel` answers it by leaving its own default — sine tones — selected, so
 * that is the kind the user is looking at and the one this has to judge against. Reading `Off` as
 * "not Tones" is what made a brand-new FFT measurement open with the box *unchecked* while its own
 * stored parameters said `snapToBin: true`: `MeasurementSaveService.BuildDefaultParametersJsonForNewType`
 * forces snapping on for every new type, deliberately and with its reasons written out, and the
 * client then unticked it on the way in. Reported 2026-10-08.</p>
 */
export function snapToBinOnLoad(storedSnapToBin: boolean, storedKind: GeneratorKind): boolean {
  if (storedKind === 'Off') return storedSnapToBin
  return storedSnapToBin && canSnapToBin(storedKind)
}
