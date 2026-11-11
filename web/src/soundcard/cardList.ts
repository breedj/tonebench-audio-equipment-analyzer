import type { SoundCardHostApiOption, SoundCardOption } from '../api/client'

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

/** One thing the device picker can say about a host API that offered it nothing. */
export interface HostApiNote {
  hostApi: string
  /** What the host API reported. */
  text: string
  /** What to do about it, where there is anything to do. */
  hint: string | null
}

/**
 * What to say about a host API that offered this picker nothing.
 *
 * <p>Added 2026-11-07, after a user reported that his interface "was not discovered and thus I
 * could not choose it" — with an ASIO4ALL driver in front of it, which this app does support and
 * does not filter. Nothing on screen or in the log could say which of three states the machine was
 * in, and they need three different answers: the host API is not in this build; it is, and no
 * driver answered; or a driver answered and reported <b>no channels</b>, which is ASIO4ALL with
 * nothing ticked in its own panel, or a card another application is holding. Only the first is
 * ours, and the third is the one the user can fix in a minute once told.</p>
 *
 * <p>Two rules, and the split is about noise rather than about information:</p>
 *
 * <ul>
 *   <li><b>Devices but no channels is always reported.</b> It is genuinely wrong, it is rare, and
 *   it is actionable.</li>
 *   <li><b>No devices at all is reported only when the card list is empty.</b> A machine with no
 *   ASIO interface is the ordinary case, and a standing "ASIO found no devices" on every QA403
 *   bench's dialog would be a line nobody needs. With an empty dropdown in front of them, what was
 *   tried is exactly what the reader wants.</li>
 * </ul>
 *
 * <p>Scoped to {@link DEFAULT_HOST_API_KINDS} — the APIs the picker offers without Advanced. An
 * empty DirectSound says nothing worth a line, in Advanced mode as much as out of it, since those
 * host APIs are hidden because their measurements cannot be believed rather than because they are
 * redundant. (That list is <i>mostly</i> the ones whose <c>Trust</c> this app accepts; CoreAudio is
 * in it for a different reason, which {@link DEFAULT_HOST_API_KINDS} gives.)</p>
 */
export function hostApiNotes(
  hostApis: readonly SoundCardHostApiOption[],
  cardCount: number,
): HostApiNote[] {
  const notes: HostApiNote[] = []
  for (const api of hostApis) {
    if (!(DEFAULT_HOST_API_KINDS as readonly string[]).includes(api.kind)) continue

    const asio = api.kind === 'Asio'
    if (api.deviceCount > 0 && api.inputCount === 0 && api.outputCount === 0) {
      notes.push({
        hostApi: api.name,
        // "driver" under ASIO, where a device is one, and "device" elsewhere, where it is an
        // endpoint. The ASIO branch is the one this note exists for and the one whose wording a
        // reader will act on.
        text:
          api.deviceCount === 1
            ? `${api.name} listed 1 ${asio ? 'driver' : 'device'}, and it reports no input or output channels.`
            : `${api.name} listed ${api.deviceCount} ${asio ? 'drivers' : 'devices'}, and none of them reports any input or output channels.`,
        hint: asio
          ? 'An ASIO driver that cannot reach its hardware reports nothing rather than failing: ' +
            'check the card is not in use by another application, and open the driver’s own ' +
            'control panel to enable its inputs and outputs. ASIO4ALL in particular shows no ' +
            'channels until the card is ticked there.'
          : null,
      })
      continue
    }

    if (api.deviceCount === 0 && cardCount === 0) {
      notes.push({
        hostApi: api.name,
        text: `${api.name} is available but found no devices.`,
        hint: asio
          ? 'So the audio library does carry ASIO, and no ASIO driver answered. A card plugged in ' +
            'after the app started needs it restarted — PortAudio reads the ASIO list once.'
          : null,
      })
    }
  }

  return notes
}

/**
 * The host API kinds the picker offers by default — see {@link buildCards}.
 *
 * <p>Four of the five are here because their <c>Trust</c> is one this app accepts.
 * <b>CoreAudio is the exception and it is not optional:</b> its trust is <c>Resamples</c> (PortAudio's
 * macOS backend converts rather than retuning the device — see <c>SoundCardEnumerator.Trust</c>),
 * but it is the <i>only</i> host API that exists on macOS, so leaving it out does not hide a
 * doubtful choice the way hiding DirectSound does — it empties the dropdown on the whole platform
 * while the backend has found the card and every log line reads healthy. The honest answer is to
 * offer it and let the dialog's own warning say what to do about the rate, which is what
 * <c>trustWarning</c> does.</p>
 */
export const DEFAULT_HOST_API_KINDS = ['Asio', 'Wasapi', 'Alsa', 'Jack', 'CoreAudio'] as const

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
  preferHostApi?: string,
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

  const cards = advanced
    ? [...seen.values()]
    : collapseToBestApi([...seen.values()], inputs, outputs, preferHostApi)

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
 *
 * <p><b>preferHostApi</b> overrides the preference order for any interface that offers that API,
 * and exists because this function's per-interface independence is wrong the moment two cards have
 * to work together. PortAudio cannot open one stream across two host APIs — measured 2026-11-11, a
 * MOTU under ASIO against a G6 under WASAPI is refused at every rate — and collapsing each
 * interface to its own best API produces exactly that pair, since ASIO is the MOTU's best and the
 * G6 has none. So the natural selection in the picker was the one combination that cannot work,
 * and the only way to reach the MOTU's WASAPI entry was the Advanced checkbox, which is 32 further
 * entries. Passing the other side's host API here collapses this side to match it where it can,
 * which is what {@link matchHostApis} is for.</p>
 */
function collapseToBestApi(
  cards: readonly SoundCard[],
  inputs: readonly SoundCardOption[],
  outputs: readonly SoundCardOption[],
  preferHostApi?: string,
): SoundCard[] {
  const hasInput = new Set(inputs.map((o) => keyOf(o.hostApi, o.interfaceName)))
  const hasOutput = new Set(outputs.map((o) => keyOf(o.hostApi, o.interfaceName)))
  const complete = (c: SoundCard) => hasInput.has(c.key) && hasOutput.has(c.key)
  const preferred = (c: SoundCard) => !!preferHostApi && c.hostApi === preferHostApi

  const best = new Map<string, SoundCard>()
  for (const card of [...cards].sort((a, b) => a.preference - b.preference)) {
    const incumbent = best.get(card.interfaceName)
    if (!incumbent) {
      best.set(card.interfaceName, card)
      continue
    }

    // The preferred API wins outright: being openable alongside the other card beats being the
    // better driver, since the better driver cannot be opened at all in that pairing.
    if (preferred(card) && !preferred(incumbent)) {
      best.set(card.interfaceName, card)
      continue
    }
    if (preferred(incumbent)) continue

    // Lower preference already won unless it cannot carry both directions and this one can.
    if (!complete(incumbent) && complete(card)) best.set(card.interfaceName, card)
  }

  return [...best.values()]
}

/**
 * The same two interfaces under one host API, where there is one they share.
 *
 * <p>The picker's lists are each collapsed towards the other side's host API, so two dropdowns
 * cannot normally produce a mismatched pair. This is the safety net for the states that do not come
 * from the dropdowns: a configuration saved before this rule existed, Advanced being turned off
 * over a deliberate cross-API selection, and ticking Separate devices over a one-card choice.</p>
 *
 * <p>It moves <b>both</b> sides rather than making one win, because neither is more right than the
 * other and the host API is not the user's choice in this mode — the non-Advanced picker offers one
 * entry per physical interface precisely to keep the API out of it. The hardware the user named is
 * what is kept.</p>
 *
 * <p>Where the two share no host API at all, both keys come back unchanged: there is nothing
 * honest to move them to, and the probe's own refusal plus the dialog's "nothing here is in effect"
 * notice already say so in words. Silently repointing one side at hardware the user did not choose
 * would be worse than a pairing that is visibly refused.</p>
 */
export function matchHostApis(
  inputKey: string,
  outputKey: string,
  inputs: readonly SoundCardOption[],
  outputs: readonly SoundCardOption[],
  advanced = false,
): { inputKey: string; outputKey: string } {
  const unchanged = { inputKey, outputKey }
  if (!inputKey || !outputKey) return unchanged

  const from = splitKey(inputKey)
  const to = splitKey(outputKey)
  if (from.hostApi === to.hostApi) return unchanged

  const offered = (o: SoundCardOption) =>
    advanced || (DEFAULT_HOST_API_KINDS as readonly string[]).includes(o.hostApiKind)

  // An API is a candidate only if it carries this input interface's inputs AND this output
  // interface's outputs. Either half missing leaves a side with an empty device list.
  const candidates = new Map<string, number>()
  for (const i of inputs) {
    if (!offered(i) || i.interfaceName !== from.name) continue
    for (const o of outputs) {
      if (!offered(o) || o.interfaceName !== to.name || o.hostApi !== i.hostApi) continue
      const best = candidates.get(i.hostApi)
      if (best === undefined || i.preference < best) candidates.set(i.hostApi, i.preference)
    }
  }

  if (candidates.size === 0) return unchanged

  const [hostApi] = [...candidates.entries()].sort(
    (a, b) => a[1] - b[1] || a[0].localeCompare(b[0]),
  )[0]!

  return { inputKey: keyOf(hostApi, from.name), outputKey: keyOf(hostApi, to.name) }
}

/**
 * The cards that offer at least one device in the given direction.
 *
 * <p>Only reached in separate-devices mode, where the two card dropdowns are independent: a card
 * with no inputs cannot be the input card, and offering it would collapse that side into a blank
 * device list with nothing saying why. In the ordinary one-card mode every card is offered,
 * including one that carries a single direction — see {@link collapseToBestApi}, whose own remark
 * says why silently dropping it would be worse.</p>
 */
export function cardsOffering(
  cards: readonly SoundCard[],
  devices: readonly SoundCardOption[],
): SoundCard[] {
  const keys = new Set(devices.map((o) => keyOf(o.hostApi, o.interfaceName)))
  return cards.filter((c) => keys.has(c.key))
}

/**
 * What a chosen pair of cards amounts to. The dialog words each case; this is the rule.
 *
 * <ul>
 *   <li><b>duplex</b> — one card both ways. The case the one-card picker exists to guarantee: one
 *   piece of hardware, therefore one clock.</li>
 *   <li><b>separate</b> — two different cards, i.e. <b>two clocks</b>. Tone-based measurements
 *   survive it; the chirp's phase, group delay and impulse response do not, because the clock
 *   ratio reads as a frequency-dependent delay. Warned about, never refused — the precedent is
 *   <c>SoundCardEnumerator.Pairing</c>, which warns rather than blocking for the same reason.</li>
 *   <li><b>inputOnly</b> / <b>outputOnly</b> — a capture-only ADC or a playback-only DAC. Openable
 *   since the one-direction streams of 2026-11-11; half an analyzer, and the dialog says which
 *   half is missing.</li>
 *   <li><b>none</b> — nothing chosen, which is the one case that cannot be opened at all. Mirrors
 *   <c>SoundCardSettings.IsUsable</c> being false.</li>
 * </ul>
 */
export type DevicePairing = 'duplex' | 'separate' | 'inputOnly' | 'outputOnly' | 'none'

export function devicePairing(inputCardKey: string, outputCardKey: string): DevicePairing {
  if (!inputCardKey && !outputCardKey) return 'none'
  if (!outputCardKey) return 'inputOnly'
  if (!inputCardKey) return 'outputOnly'
  return inputCardKey === outputCardKey ? 'duplex' : 'separate'
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
