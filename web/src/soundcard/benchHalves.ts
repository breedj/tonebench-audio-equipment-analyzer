import type {
  SoundCardConfig,
  SoundCardOption,
  SplitDeviceConfig,
  SplitHalfKindName,
} from '../api/client'
import { asioSplitProblem } from './asioSplit'
import { cardsOffering, splitKey, type SoundCard } from './cardList'

/**
 * One dropdown per direction, naming the <em>device</em> rather than a kind — the rule behind the
 * Split devices dialog's two selects, and the two documents a choice there writes.
 *
 * <p><b>Why one control and not two.</b> Until 2026-11-15 a split bench was configured through
 * three dialogs: one chose the two <i>kinds</i> (<c>device.split</c>), one chose <i>which card</i>
 * each half was (<c>soundcard.settings</c>) with a prose link between them, and a third held the
 * QA40x's own settings. "Which device captures" and "which card that is" were two questions about
 * one half, in two places, and a split bench showed all three gears at once. Merging them means a
 * half is picked once, by name.</p>
 *
 * <p><b>The encoding, which is what keeps that small.</b> A card's value <i>is</i> its card key
 * from <c>keyOf(hostApi, interfaceName)</c>, unchanged — so the dialog's <c>form.inputCardKey</c>
 * keeps its type and every existing consumer of it (<c>cardsOffering</c>, <c>matchHostApis</c>,
 * <c>reselectCardKey</c>, <c>asioSplitProblem</c>) is untouched. That works because a card key
 * contains a NUL separator and the two kind names cannot: parsing is one <c>includes</c>, and it
 * survives a host API or interface name containing any printable character — which a prefix scheme
 * such as <c>card:</c> would not, PortAudio device names being whatever the driver says.</p>
 *
 * <p>Nothing in a <c>.vue</c> is testable under this suite's node environment, which is why the
 * rules live here beside <c>cardList.ts</c>, <c>probeSides.ts</c> and <c>asioSplit.ts</c> rather
 * than in the dialog that renders them.</p>
 */

/** The two kinds that are not a card, as their own option values. */
export const QA40X_HALF: SplitHalfKindName = 'Hardware'
export const SIMULATOR_HALF: SplitHalfKindName = 'Simulated'

/** What one direction of the bench is: a kind, plus the card when that kind is a card. */
export interface BenchHalf {
  kind: SplitHalfKindName
  /** `keyOf(hostApi, interfaceName)`, or empty for the two kinds that are not a card. */
  cardKey: string
}

/** One entry of a direction's dropdown. */
export interface BenchHalfOption {
  /** `''` for "nothing chosen", a kind name, or a card key. */
  value: string
  label: string
  kind: SplitHalfKindName
  /** Empty on the two kind entries. */
  cardKey: string
}

/**
 * Whether an option value names a card rather than one of the two kinds.
 *
 * <p>The NUL is <c>keyOf</c>'s own separator, so this asks the one question that cannot be
 * confused: no <c>DeviceKind</c> name contains it, and every card key does.</p>
 */
function isCardValue(value: string): boolean {
  return value.includes('\u0000')
}

export function halfValueOf(half: BenchHalf): string {
  return half.kind === 'SoundCard' ? half.cardKey : half.kind
}

/**
 * The half an option value names.
 *
 * <p>An unrecognised value reads as <b>a card with no card chosen</b> rather than as a kind: that
 * is the state <c>benchPairingProblem</c> refuses by name, so a value this build does not
 * understand produces a sentence rather than silently selecting hardware nobody asked for.</p>
 */
export function halfFromValue(value: string): BenchHalf {
  if (isCardValue(value)) return { kind: 'SoundCard', cardKey: value }
  if (value === QA40X_HALF) return { kind: 'Hardware', cardKey: '' }
  if (value === SIMULATOR_HALF) return { kind: 'Simulated', cardKey: '' }
  return { kind: 'SoundCard', cardKey: '' }
}

/** What each kind is called on screen. QA40x rather than QA403, like the analyzer picker's entry. */
export const QA40X_LABEL = 'QA40x'
export const SIMULATOR_LABEL = 'Simulator'

/**
 * The entries one direction's dropdown offers: <b>QA40x first, then the cards, then Simulator</b>.
 *
 * <p>The order is the ask, and it reads as a scale rather than a list: the dedicated analyzer, the
 * hardware the machine happens to have, and the thing that is not hardware at all. The cards keep
 * <c>cardsOffering</c>'s order, which is <c>buildCards</c>' — the host APIs that reach the hardware
 * first and the ones that resample last — so a card does not sit somewhere its preference does not
 * put it.</p>
 *
 * <p><b>Direction filtering is delegated, not re-implemented.</b> <c>cardsOffering</c> already
 * drops a card that does not carry the direction's devices, which is what stops the input dropdown
 * offering a playback-only DAC and collapsing that side into a blank device list with nothing
 * saying why.</p>
 *
 * <p><b><c>offerKinds</c> is false on a plain sound-card bench</b>, where the list is then
 * byte-identical to the one this picker offered before the merge. Picking a QA40x there would have
 * to change the analyzer <i>kind</i>, which is the status bar's Analyzer dropdown's job — and a
 * dropdown that silently re-pointed the whole session would be a worse answer than not offering it.</p>
 */
export function benchHalfOptions(
  cards: readonly SoundCard[],
  devices: readonly SoundCardOption[],
  offerKinds: boolean,
): BenchHalfOption[] {
  const options: BenchHalfOption[] = [{ value: '', label: '—', kind: 'SoundCard', cardKey: '' }]

  if (offerKinds) {
    options.push({ value: QA40X_HALF, label: QA40X_LABEL, kind: 'Hardware', cardKey: '' })
  }

  for (const card of cardsOffering(cards, devices)) {
    options.push({
      value: card.key,
      label: `${card.interfaceName} — ${card.hostApi}${card.duplex ? ' (full duplex)' : ''}`,
      kind: 'SoundCard',
      cardKey: card.key,
    })
  }

  if (offerKinds) {
    options.push({ value: SIMULATOR_HALF, label: SIMULATOR_LABEL, kind: 'Simulated', cardKey: '' })
  }

  return options
}

/**
 * Why these two halves cannot be a bench, or null.
 *
 * <p>Composed rather than re-worded: each refusal already exists somewhere that judges it, and this
 * is the one place the dialog asks. The server refuses all of it again regardless — this is the
 * message, not the guard.</p>
 *
 * <ul>
 *   <li><b>A QA40x on both sides</b> — <c>SplitPairing.Resolve</c>'s own sentence, shared verbatim
 *   so the dialog and the 400 a user would otherwise meet say the same thing.</li>
 *   <li><b>Two cards</b> — delegated to <c>asioSplitProblem</c>, which holds the at-most-one-ASIO
 *   rule and names both devices. Not re-stated here, or the two would drift on the advice they
 *   give.</li>
 *   <li><b>A card half with no card</b> — mirrors <c>SoundCardSettings.IsUsable</c>. Reachable by
 *   opening the dialog on a bench whose stored card is gone from the list, which is the one state
 *   the dropdowns themselves cannot produce.</li>
 * </ul>
 *
 * <p>Two simulators and two cards are deliberately <b>allowed</b>: the first is the desk bench that
 * exercises the whole composite with nothing plugged in, and the second is the point of the feature
 * since each card half took a transfer thread of its own. <c>SplitPairing.Resolve</c>'s remarks hold
 * the reasoning for both.</p>
 */
export function benchPairingProblem(input: BenchHalf, output: BenchHalf): string | null {
  if (input.kind === 'Hardware' && output.kind === 'Hardware') {
    return (
      'A split cannot have a QA40x on both sides: that is one device opened twice, and the '
      + 'second claim would fail. A QA40x already generates and captures on its own clock.'
    )
  }

  if (input.kind === 'SoundCard' && !input.cardKey) {
    return 'Choose the card that captures, or pick the QA40x or the simulator instead.'
  }

  if (output.kind === 'SoundCard' && !output.cardKey) {
    return 'Choose the card that generates, or pick the QA40x or the simulator instead.'
  }

  if (input.kind === 'SoundCard' && output.kind === 'SoundCard') {
    return asioSplitProblem(input.cardKey, output.cardKey)
  }

  return null
}

/** Everything the dialog stages about the bench, in the two shapes the two documents want. */
export interface StagedBench {
  input: BenchHalf
  output: BenchHalf
  /** `keyOf(hostApi, deviceName)` for the chosen input *device*, inside the input card. */
  inputKey: string
  outputKey: string
  inputChannelOffset: number
  outputChannelOffset: number
  minStimulusFrequencyHz: number
  streamStartSettleMs: number
}

/**
 * The card document a staged bench writes.
 *
 * <p><b>A half that is not a card leaves its direction's stored device alone</b>, and that is the
 * one thing a naive merged dropdown breaks silently. The card document is a single document that a
 * split uses one direction of (<c>SoundCardSettings.ForDirection</c> blanks the other side), so
 * choosing the QA40x as the capturing half must not <i>erase</i> the input card underneath it:
 * <c>SplitDevicePage.vue</c> promises that switching the analyzer picker back to Sound card finds
 * that bench as it was, and the Sound card analyzer opens both directions of this same document.</p>
 *
 * <p>The channel offset goes with its own direction for the same reason — it belongs to the device
 * the offset was chosen on — while the two figures below the dropdowns are the card document's own
 * and are always carried, because the rows that set them are rendered whenever either half is a
 * card.</p>
 */
export function cardConfigFor(stored: SoundCardConfig, staged: StagedBench): SoundCardConfig {
  const next: SoundCardConfig = {
    ...stored,
    minStimulusFrequencyHz: staged.minStimulusFrequencyHz,
    streamStartSettleMs: staged.streamStartSettleMs,
  }

  if (staged.input.kind === 'SoundCard') {
    const input = splitKey(staged.inputKey)
    next.inputHostApi = input.hostApi
    next.inputDeviceName = input.name
    next.inputChannelOffset = staged.inputChannelOffset
  }

  if (staged.output.kind === 'SoundCard') {
    const output = splitKey(staged.outputKey)
    next.outputHostApi = output.hostApi
    next.outputDeviceName = output.name
    next.outputChannelOffset = staged.outputChannelOffset
  }

  return next
}

/** The split document a staged bench writes. */
export function splitConfigFor(staged: StagedBench, generatorSampleRateHz: number): SplitDeviceConfig {
  return {
    inputKind: staged.input.kind,
    outputKind: staged.output.kind,
    generatorSampleRateHz,
  }
}

/** The pair of documents a split bench is configured by. */
export interface BenchDocuments {
  card: SoundCardConfig
  split: SplitDeviceConfig
}

/**
 * Which of the two documents a staged bench actually moves.
 *
 * <p>Both are written on the same debounce and each reopens the device, so writing one that has not
 * changed costs a reopen, a <c>MeasurementRunGuard</c> window and a stream the user did not ask to
 * be restarted — on every nudge of a channel offset. A kind change legitimately moves both and
 * therefore pays two reopens; nothing else should pay one.</p>
 *
 * <p>A document that has <b>not been read yet</b> is reported unmoved, which is the existing
 * <c>if (!config.value) return</c> guard said in one place: a write built against a document this
 * dialog has never seen would be a write of defaults over whatever is stored.</p>
 *
 * <p>The card half is compared as JSON, which is what <c>applyDevice</c> has always done and is
 * safe because <c>cardConfigFor</c> spreads the stored document first and so preserves its key
 * order. The split half is compared field by field, because that one is built fresh rather than
 * spread and its key order is the server serializer's.</p>
 */
export function documentsTouched(
  staged: BenchDocuments,
  stored: { card: SoundCardConfig | null; split: SplitDeviceConfig | null },
): { card: boolean; split: boolean } {
  return {
    card: stored.card !== null && JSON.stringify(staged.card) !== JSON.stringify(stored.card),
    split:
      stored.split !== null
      && (staged.split.inputKind !== stored.split.inputKind
        || staged.split.outputKind !== stored.split.outputKind
        || staged.split.generatorSampleRateHz !== stored.split.generatorSampleRateHz),
  }
}
