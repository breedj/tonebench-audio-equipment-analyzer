import type { SoundCardOption } from '../api/client'

/**
 * Which cards the picker offers, and under which host API.
 *
 * <p>Separate from <c>SoundCardDialog.vue</c> because nothing in a <c>.vue</c> is testable under
 * the node environment vitest runs in, and the filtering below is the kind of rule that is wrong
 * in ways no one notices — a card quietly missing from a list looks exactly like a card that is
 * not plugged in.</p>
 *
 * <p><b>Why a filter exists at all.</b> PortAudio enumerates every device under every host API, so
 * one physical interface appears once per API that exposes it: measured on this bench
 * (2026-10-02), 59 devices collapsed to <b>35</b> card entries for four pieces of hardware, where
 * ARTA's own picker offers five. The difference is not that ARTA hides things — it lists host API
 * <i>drivers</i> (one "WDM" entry standing for every Windows endpoint, plus one key per registered
 * ASIO driver) where this app lists card × host API. Both axes are legitimate; showing them
 * multiplied together is not.</p>
 */

/** The host API kinds the picker offers by default — see {@link buildCards}. */
export const DEFAULT_HOST_API_KINDS = ['Asio', 'Wasapi', 'Alsa', 'Jack'] as const

/** One entry of the Sound card dropdown. */
export interface SoundCard {
  /** `hostApi \u0000 interfaceName` — the same interface under two host APIs is two cards. */
  key: string
  hostApi: string
  hostApiKind: string
  interfaceName: string
  trust: string
  /** True when one device carries both directions, which on Windows means ASIO. */
  duplex: boolean
  /** Sort key: the host APIs that reach the hardware first, the ones that resample last. */
  preference: number
  hasControlPanel: boolean
}

/** Host API and name together: the same device name exists under several host APIs, and they are different devices. */
export function keyOf(hostApi: string, name: string) {
  return `${hostApi}\u0000${name}`
}

export function splitKey(key: string) {
  const [hostApi = '', name = ''] = key.split('\u0000')
  return { hostApi, name }
}

/**
 * The cards to offer, from the inventory's two device lists.
 *
 * <p><b>advanced</b> is the unfiltered list — every host API, one entry per (API, interface) — and
 * is what this picker showed before 2026-10-02. It is a temporary escape hatch while the filtered
 * list is checked against real hardware; the default below is meant to be the only list.</p>
 *
 * <p>The default drops two host API families, each for its own reason, and neither is
 * decluttering:</p>
 *
 * <ul>
 *   <li><b>DirectSound and MME</b> resample in software and accept any rate, so the rate reported
 *   is not the rate the converter ran at. <c>SoundCardEnumerator.Trust</c> already calls them
 *   <c>Resamples</c>; offering them at the top level means computing "this cannot be believed" and
 *   then putting it in the list anyway. They also carry pure fictions — <c>Microsoft
 *   geluidstoewijzing</c>, <c>Primair geluidsstuurprogramma</c> — which are mapper pseudo-devices
 *   rather than hardware. 14 of the 35 entries here.</li>
 *
 *   <li><b>WDM-KS</b> is bit-exact and has the lowest latency of the Microsoft stack, but its
 *   device names carry no interface name at all (<c>In 1-2 (In 1-2)</c>), so it is the one host API
 *   where <c>SoundCardEnumerator.Pairing</c> can only ever answer <c>Unknown</c>. Picking the card
 *   first exists precisely to guarantee that both directions are one piece of hardware and
 *   therefore one clock, and WDM-KS is where that guarantee cannot be made. Its only advantage over
 *   WASAPI exclusive is a few milliseconds of latency, which is worth nothing to a measurement
 *   built on multi-second records. 16 of the 35 entries, and the reason a name-based rule cannot
 *   help: nothing can tell that <c>In 1-2</c> <i>is</i> the MOTU, so "only show WDM-KS where there
 *   is nothing better" removed exactly one entry when it was tried.</li>
 * </ul>
 *
 * <p>Note ALSA and JACK are kept for the same reason ASIO and WASAPI are — <c>Trust</c> calls them
 * <c>Trusted</c> — so this filter is not Windows-only by accident.</p>
 */
export function buildCards(
  inputs: readonly SoundCardOption[],
  outputs: readonly SoundCardOption[],
  advanced: boolean,
): SoundCard[] {
  const devices = [...inputs, ...outputs]
  const offered = advanced
    ? devices
    : devices.filter((o) => (DEFAULT_HOST_API_KINDS as readonly string[]).includes(o.hostApiKind))

  const seen = new Map<string, SoundCard>()
  for (const o of offered) {
    const key = keyOf(o.hostApi, o.interfaceName)
    const existing = seen.get(key)
    if (existing) {
      existing.duplex ||= o.isFullDuplex
      existing.hasControlPanel ||= o.hasControlPanel
      continue
    }

    seen.set(key, {
      key,
      hostApi: o.hostApi,
      hostApiKind: o.hostApiKind,
      interfaceName: o.interfaceName,
      trust: o.trust,
      duplex: o.isFullDuplex,
      preference: o.preference,
      hasControlPanel: o.hasControlPanel,
    })
  }

  const cards = advanced ? [...seen.values()] : collapseToBestApi([...seen.values()], inputs, outputs)

  return cards.sort(
    (a, b) => a.preference - b.preference || a.interfaceName.localeCompare(b.interfaceName),
  )
}

/**
 * One entry per physical interface, under the best host API that can actually drive it.
 *
 * <p>Without this the MOTU appears twice, as ASIO and as WASAPI, which is exactly the duplication
 * that makes the list unreadable — and the second entry is strictly worse, since ASIO is the only
 * host API that presents the card as a single full-duplex device.</p>
 *
 * <p><b>"Can drive it" is the load-bearing part.</b> A host API wins only if it offers this
 * interface at least one input <i>and</i> at least one output, because choosing a card filters both
 * device lists to it: an API that exposes only one direction would collapse the card into an
 * unusable entry while hiding the API that could have run it. Where no API has both, the lowest
 * preference still wins and the dialog's existing pairing and rate probes report what is wrong —
 * silently dropping the card would be worse than offering a broken one.</p>
 */
function collapseToBestApi(
  cards: readonly SoundCard[],
  inputs: readonly SoundCardOption[],
  outputs: readonly SoundCardOption[],
): SoundCard[] {
  const hasInput = new Set(inputs.map((o) => keyOf(o.hostApi, o.interfaceName)))
  const hasOutput = new Set(outputs.map((o) => keyOf(o.hostApi, o.interfaceName)))
  const complete = (c: SoundCard) => hasInput.has(c.key) && hasOutput.has(c.key)

  const best = new Map<string, SoundCard>()
  for (const card of [...cards].sort((a, b) => a.preference - b.preference)) {
    const incumbent = best.get(card.interfaceName)
    if (!incumbent) {
      best.set(card.interfaceName, card)
      continue
    }

    // Lower preference already won unless it cannot carry both directions and this one can.
    if (!complete(incumbent) && complete(card)) best.set(card.interfaceName, card)
  }

  return [...best.values()]
}

/**
 * The card to select after the list changes under the current selection — which is what toggling
 * Advanced does.
 *
 * <p>Turning Advanced off while the MOTU's WASAPI entry is selected must not leave the dialog
 * pointing at a card that is no longer in its own dropdown: both device lists filter on the card
 * key, so the selection would survive as a blank Input and a blank Output. The same
 * <i>interface</i> under the list's best host API is the honest replacement — it is the same piece
 * of hardware — and where there is none, nothing is selected rather than something arbitrary.</p>
 */
export function reselectCardKey(
  previousKey: string,
  inputs: readonly SoundCardOption[],
  outputs: readonly SoundCardOption[],
  advanced: boolean,
): string {
  const cards = buildCards(inputs, outputs, advanced)
  if (cards.some((c) => c.key === previousKey)) return previousKey

  const previous = [...inputs, ...outputs].find((o) => keyOf(o.hostApi, o.interfaceName) === previousKey)
  if (!previous) return ''

  return cards.find((c) => c.interfaceName === previous.interfaceName)?.key ?? ''
}

/**
 * The channel pair offset a device can actually honour: the largest whole pair within its channel
 * count, and 0 for a device with fewer than two.
 *
 * <p>A pair is an offset plus two, so <em>3/4</em> needs four channels. Every device on a typical
 * Windows machine outside ASIO has exactly two — measured here, the MOTU is 8 in / 4 out under ASIO
 * and every other entry in the picker is 2 — so an offset chosen on an ASIO card and carried onto
 * any of them asks for channels that do not exist.</p>
 *
 * <p><b>That is a dead end rather than an error</b>, which is why it is clamped and not merely
 * reported: the dialog renders a channel-pair dropdown only where there is more than one pair to
 * choose, so a two-channel card shows no channel control at all — the probe's complaint about
 * channels then sits beside nothing that mentions channels, and the only way out is to select the
 * multi-channel card again, set 1/2 there, and come back. The probe still refuses an impossible
 * pair, for a saved configuration whose card has since changed; nothing the picker does can reach
 * it any more.</p>
 */
export function clampChannelOffset(offset: number, channelCount: number): number {
  const pairs = Math.floor(channelCount / 2)
  if (pairs < 1) return 0

  // Offsets step by two — an odd one would straddle two pairs, which no device lists.
  const whole = Math.max(0, Math.floor(offset / 2) * 2)
  return Math.min(whole, (pairs - 1) * 2)
}

/**
 * Whether a saved selection is reachable in the default list.
 *
 * <p>The config stores a host API and a device <i>name</i> rather than an index
 * (<c>SoundCardEnumerator.Find</c>), so a card under a now-hidden host API still resolves and still
 * works — but it would not appear in its own dropdown, which reads as the card having vanished. The
 * dialog turns Advanced on for that case instead.</p>
 */
export function savedCardNeedsAdvanced(
  inputs: readonly SoundCardOption[],
  outputs: readonly SoundCardOption[],
  hostApi: string,
  deviceName: string,
): boolean {
  if (!hostApi && !deviceName) return false

  const device = [...inputs, ...outputs].find((o) => o.hostApi === hostApi && o.name === deviceName)
  if (!device) return false

  const key = keyOf(device.hostApi, device.interfaceName)
  return !buildCards(inputs, outputs, false).some((c) => c.key === key)
}
