import type { SoundCardOption } from '../api/client'
import { buildCards, DEFAULT_HOST_API_KINDS, splitKey, type SoundCard } from './cardList'

/**
 * The one ASIO rule a two-card bench has to live by, and the two things it decides: what the card
 * dropdowns offer, and what the dialog says when the pair cannot be opened.
 *
 * <p><b>The rule.</b> The ASIO SDK has no notion of more than one driver open per process, and
 * PortAudio's backend carries that as a singleton whose callbacks take no context pointer — so a
 * split may have <b>at most one ASIO card</b>, whatever its two transfer threads do. The server
 * states it in <c>SoundCardSettings.SplitCombinationProblem</c> and refuses the pair at connect;
 * everything here is the same rule arriving early enough to be acted on.</p>
 *
 * <p><b>Why the list half exists at all.</b> <c>collapseToBestApi</c> offers one entry per physical
 * interface under the best host API that can drive it, which on Windows means ASIO wherever a card
 * has an ASIO driver — and that is right for a one-card bench, where the WASAPI twin is strictly
 * worse. On a two-card bench whose cards <i>both</i> have ASIO drivers it collapses away the only
 * pairing that works: reported here 2026-11-15, a MOTU M4 and a TOPPING D50 III both offered under
 * ASIO and nothing else, so the server's own advice — move one of them to WASAPI — could only be
 * followed through the Advanced checkbox, which on this bench is thirty further entries.</p>
 *
 * <p>Note the two cards are not symmetrical in how they reach this, and it is worth knowing which
 * kind of card is in front of you. A card whose ASIO and WASAPI device names share an interface
 * stem (<c>MOTU M Series</c> against <c>In 1-2 (MOTU M Series)</c>) is <i>one</i> interface and is
 * collapsed; one whose names do not (<c>Topping USB Audio Device</c> against <c>Luidsprekers
 * (TOPPING USB DAC)</c>) is already two cards in the picker and needs none of this. So the fault
 * only ever hid half of a pair, which is why it reads as one card refusing to appear.</p>
 */

/** PortAudio's own name for the ASIO host API — the literal a stored card document holds. */
export const ASIO_HOST_API = 'ASIO'

/** Whether a host API name is ASIO's. Mirrors <c>SoundCardSettings.IsAsio</c>. */
export function isAsioHostApi(hostApi: string): boolean {
  return hostApi.toUpperCase() === ASIO_HOST_API
}

/**
 * The same list, plus the best non-ASIO entry for every ASIO card in it.
 *
 * <p>Applied to a two-card bench's dropdowns and to nothing else: one card is one stream, where
 * ASIO is the better driver and its twin adds a worse duplicate of a card already in the list.</p>
 *
 * <p><b>Offered whether or not the other side is ASIO too</b>, rather than appearing the moment the
 * pair collides. A list that rearranges itself as the other dropdown moves is the harder thing to
 * use, and <i>which</i> card keeps ASIO is a judgement this code cannot make for the user — the
 * point of a two-card bench is that the better converter keeps the better driver, and only they
 * know which one that is. One extra entry per ASIO interface is the whole cost.</p>
 *
 * <p>Idempotent, and a no-op under Advanced, where every entry is in the list already.</p>
 */
export function withAsioAlternatives(
  cards: readonly SoundCard[],
  inputs: readonly SoundCardOption[],
  outputs: readonly SoundCardOption[],
  advanced = false,
): SoundCard[] {
  const asio = cards.filter((c) => isAsioHostApi(c.hostApi))
  if (asio.length === 0) return [...cards]

  // Every (host API, interface) pair the machine has, already merged across its devices — so the
  // alternative is built the same way the entry beside it was rather than assembled here.
  const every = buildCards(inputs, outputs, true)
  const offered = (c: SoundCard) =>
    advanced || (DEFAULT_HOST_API_KINDS as readonly string[]).includes(c.hostApiKind)

  const taken = new Set(cards.map((c) => c.key))
  const added: SoundCard[] = []
  for (const card of asio) {
    const alternative = every
      .filter(
        (c) =>
          c.interfaceName === card.interfaceName && !isAsioHostApi(c.hostApi) && offered(c),
      )
      .sort((a, b) => a.preference - b.preference || a.hostApi.localeCompare(b.hostApi))[0]

    if (!alternative || taken.has(alternative.key)) continue
    taken.add(alternative.key)
    added.push(alternative)
  }

  // The list's own order — the host APIs that reach the hardware first, the ones that resample
  // last — so an alternative does not sit somewhere its preference does not put it.
  return [...cards, ...added].sort(
    (a, b) => a.preference - b.preference || a.interfaceName.localeCompare(b.interfaceName),
  )
}

/**
 * Why these two card selections cannot be the two halves of a split, or null.
 *
 * <p>The client's mirror of <c>SoundCardSettings.SplitCombinationProblem</c>, which judges the same
 * pair on the server and refuses it at connect. Mirrored rather than fetched because the dialog is
 * editing a <i>draft</i> — the server's answer describes the bench as last saved, and a stale
 * refusal beside a selection it is not about is worse than none. <c>cardList.test.ts</c>'s
 * hand-mirrored <c>PreferenceOf</c> is the precedent, and <c>asioSplit.test.ts</c> reads the server's
 * own source so the two cannot drift apart on the advice they give.</p>
 *
 * <p>It is said in the dialog because this is where the pair is chosen, and because nothing else
 * there can say it: each card is probed on a stream of its own and <i>both answer perfectly</i> —
 * the two rate rows read healthy, the calibration steps stay live, and the only sign that the bench
 * cannot be opened arrives minutes later at Connect. That is the lesson
 * <c>DuplexCombinationProblem</c>'s own remarks record from 2026-11-12, where a dialog that was
 * truthful about the rates was so wrong about the cause that the fix it suggested had already been
 * followed.</p>
 */
export function asioSplitProblem(inputCardKey: string, outputCardKey: string): string | null {
  if (!inputCardKey || !outputCardKey) return null

  // A card key is its host API and its interface name, which is what the card is called on screen —
  // so the message names the two entries the reader is looking at rather than a device inside one.
  const input = splitKey(inputCardKey)
  const output = splitKey(outputCardKey)
  if (!isAsioHostApi(input.hostApi) || !isAsioHostApi(output.hostApi)) return null

  // One ASIO card on both halves is one interface and one clock, which is a Sound card bench rather
  // than a split -- and two streams on one driver is refused just the same.
  if (inputCardKey === outputCardKey) {
    return (
      `'${input.name}' is one interface, so it is a sound card rather than a split: choose `
      + 'Sound card in the analyzer picker, which opens both of its directions in one stream.'
    )
  }

  return (
    'A split cannot have two ASIO cards: ASIO allows one driver open per process, so '
    + `'${input.name}' and '${output.name}' cannot both be opened whatever the sample rate. `
    + 'Move one of them to WASAPI — which this analyzer opens in exclusive mode and trusts — and '
    + 'the other keeps ASIO.'
  )
}
