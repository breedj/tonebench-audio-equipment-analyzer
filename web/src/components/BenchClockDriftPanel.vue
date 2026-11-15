<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { BenchClockDrift, SplitHalfKindName } from '../api/client'
import { api } from '../api/client'
import ClockDriftDialog from './ClockDriftDialog.vue'

/**
 * What this bench's two clocks were last measured to differ by, with the button that measures them.
 *
 * <p>Its own component because the panel is about a <em>pairing</em> and nothing else: it owns the
 * figure, the summary, the button and the nested run dialog, and the host only has to say which two
 * devices are staged. It stood inside the split dialog until the two device dialogs were merged, and
 * inside the sound-card dialog before that — the two copies being how one reading came to be
 * refreshed while the other went stale.</p>
 *
 * <p><b>It renders nothing at all unless the bench really is two clocks.</b> On a bench whose two
 * halves are one device there is nothing to measure and the test would read zero by construction, so
 * a panel saying so would be a section about a problem this bench does not have. The server decides
 * that — <c>BenchClocks</c> is the one thing that knows what a pairing is keyed by — which is also
 * why the figure is fetched rather than read out of a document here.</p>
 */
const props = defineProps<{
  open: boolean
  /** The generating half's kind. */
  generates: SplitHalfKindName
  /** The capturing half's kind. */
  captures: SplitHalfKindName
  /** The cards those halves name, where they name one. Empty otherwise. */
  inputHostApi: string
  inputDevice: string
  outputHostApi: string
  outputDevice: string
  /** The bench in a sentence, passed to the run dialog so it cannot be read the wrong way round. */
  pairingNote: string
  /** Whether the staged bench is not yet the one the session has open. */
  pending: boolean
  /** Why, for the held button's tooltip and the line under the figure. */
  pendingReason: string
}>()

const benchDrift = ref<BenchClockDrift | null>(null)
const clockDriftOpen = ref(false)

/**
 * Read for the pairing the dialog is <em>staging</em>, not the one the session has open.
 *
 * <p>Answered for the session's own bench, the panel shows the figure for the pairing being
 * replaced — which is what it did: a reading taken with the card generating stayed on screen, under
 * a "Measured" summary, after the dropdowns were turned round (reported 2026-11-13). The server
 * resolves it, so <c>BenchClockKey</c> stays the one thing that decides what a pairing is keyed
 * by.</p>
 *
 * <p>The two <b>card names</b> go with the two kinds, and that is not decoration: on a bench with a
 * card on each half the pairing is keyed per card, so turning the input card round is a different
 * pairing with a different stored figure. Without them such a change left the previous card's
 * reading on screen — the 2026-11-13 fault one layer down, and the one this panel could not reach
 * while the cards were chosen in a different dialog.</p>
 */
async function loadBenchDrift() {
  try {
    benchDrift.value = await api.getBenchClockDrift({
      generates: props.generates,
      captures: props.captures,
      // A card name only for a half that is a card, and `undefined` rather than empty for the
      // rest: the endpoint reads "neither given" as "use the stored document", and `toQuery` sends
      // an empty string as an empty string — which would key that half's clock to a card with no
      // name instead.
      ...(props.captures === 'SoundCard' && props.inputDevice
        ? { inputHostApi: props.inputHostApi, inputDevice: props.inputDevice }
        : {}),
      ...(props.generates === 'SoundCard' && props.outputDevice
        ? { outputHostApi: props.outputHostApi, outputDevice: props.outputDevice }
        : {}),
    })
  } catch {
    benchDrift.value = null
  }
}

/** Whether there is a stored figure to show, as opposed to a pairing nobody has measured yet. */
const measured = computed(() => benchDrift.value?.ppm !== null && benchDrift.value?.ppm !== undefined)

/** The drift dialog's own formatter, so a figure reads the same in both places. */
function fmt(value: number | null | undefined, digits = 2, unit = '') {
  return value == null || !Number.isFinite(value) ? '—' : `${value.toFixed(digits)}${unit}`
}

/**
 * The four figures a run produces, in the run dialog's own order and spelling.
 *
 * <p>A table rather than the sentence this was, asked for 2026-11-15. The sentence quoted the drift
 * and the spread and dropped σ and the tone margin, which are the two that say whether the figure
 * is worth trusting — so the panel that decides whether to measure again showed less than the
 * dialog that had just measured. The same four, read the same way, is the point.</p>
 */
const readings = computed(() => {
  const d = benchDrift.value
  return [
    { label: 'Drift', text: fmt(d?.ppm, 2, ' ppm') },
    { label: 'Spread', text: fmt(d?.spreadPpm, 2, ' ppm') },
    { label: 'σ', text: fmt(d?.sigmaPpm, 3, ' ppm') },
    { label: 'Tone margin', text: fmt(d?.toneMarginDb, 1, ' dB') },
  ]
})

/** When it was taken, under the figures — provenance rather than a reading. */
const measuredWhen = computed(() => {
  const d = benchDrift.value
  if (!measured.value) return null
  return d?.measuredUtc ? new Date(d.measuredUtc).toLocaleString() : 'date not recorded'
})

/**
 * The run stores its figure the moment it ends — there is no Keep button, by design — so the panel
 * behind this dialog is a run out of date the moment it closes.
 */
async function onClockDriftClosed() {
  clockDriftOpen.value = false
  await loadBenchDrift()
}

watch(
  () => props.open,
  async (open) => {
    if (open) await loadBenchDrift()
  },
  { immediate: true },
)

// The panel is the pairing's, so it has to be re-read when the pairing changes -- not only when the
// dialog opens. The generator rate is deliberately not in this list: a clock difference is a
// property of two crystals, and the rate one of them is clocked at does not change which two.
watch(
  () => [props.generates, props.captures, props.inputDevice, props.outputDevice].join('|'),
  async () => {
    if (props.open) await loadBenchDrift()
  },
)
</script>

<template>
  <!-- Rendered only once the pairing really is two devices. -->
  <section v-if="benchDrift?.twoClocks" class="dialog-card">
    <div class="flex items-center gap-2 border-b border-neutral-100 p-3.5 dark:border-neutral-800">
      <span class="flex-1 text-sm font-semibold">Clock drift</span>
      <button
        type="button"
        class="btn-chip"
        :style="{ backgroundColor: 'var(--kind-violet-chip-bg)', color: 'var(--kind-violet-chip-fg)' }"
        :disabled="props.pending"
        :title="
          props.pending
            ? props.pendingReason
            : `Plays a tone on the generating device and reads it back on the capturing one. It measures through whatever is connected, because a DUT changes a tone's level and phase but not its frequency.`
        "
        @click="clockDriftOpen = true"
      >
        {{ benchDrift?.ppm === null || benchDrift?.ppm === undefined ? 'Measure the drift' : 'Measure again' }}
      </button>
    </div>
    <!-- The run dialog's own four figures, in its order and its spelling, so the panel that
         decides whether to measure again shows what the dialog that measured did. -->
    <div v-if="measured" class="px-3.5 pt-3 text-[12px]">
      <div class="grid grid-cols-2 gap-x-6 gap-y-1 sm:grid-cols-4">
        <div v-for="r in readings" :key="r.label">
          <span class="text-neutral-500">{{ r.label }}</span><br />
          <span class="font-mono tabular-nums">{{ r.text }}</span>
        </div>
      </div>
    </div>

    <p class="m-0 p-3.5 text-[12.5px] leading-relaxed text-neutral-600 dark:text-neutral-400">
      <template v-if="measured">Measured {{ measuredWhen }}. </template>
      <template v-else>Not measured on this pairing yet. </template>
      Measured, the two chirp screens offer to take the clock's delay ramp off a sweep.
      <template v-if="props.pending">{{ props.pendingReason }}</template>
    </p>
  </section>

  <ClockDriftDialog :open="clockDriftOpen" :pairing-note="props.pairingNote" @close="onClockDriftClosed" />
</template>
