<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { displayPrecision, fromDisplay, toDisplay, type GainUnit } from '../soundcard/gainUnits'

/**
 * The two external-gain boxes and their unit selector, as a small panel above the status bar.
 *
 * <p><b>Why a popover rather than two boxes on the bar.</b> The gain is edited rarely — when the
 * bench is re-wired — but has to be <i>visible</i> constantly, because a divider the bench has
 * forgotten about makes every reported level wrong by its whole size. Those two pull opposite ways,
 * and trying to satisfy both with one widget is what previously put an editable copy on the bar and
 * a second, read-only copy of the same number a few pixels away. The badge answers the visibility
 * half; this answers the editing half, and the number exists once.</p>
 *
 * <p>It edits whichever analyzer is live — each bench stores its own gain, and the routing is in
 * App.vue's <c>setExternalGain</c>. This component is told nothing about that: it is handed the
 * effective figures and emits the new ones.</p>
 *
 * <p>Positioned by the caller (absolute, bottom-anchored over the bar) rather than here, because
 * only the bar knows where its own badge is.</p>
 *
 * <p>It is painted in <b>ground</b> rather than panel — the one place in the app where something
 * sitting on top of a surface is darker than it, in the light theme, instead of lighter. That is
 * deliberate: the status bar it rises out of is itself a panel, so a panel-coloured popover would
 * read as the bar having grown rather than as something opening over it. Spelled as the token and
 * not as the literal #f5f5f4 it resolves to, because a hard-coded light grey would stay light grey
 * in the dark theme.</p>
 */
const props = defineProps<{
  leftDb: number
  rightDb: number
  unit: GainUnit
  /** Which bench is being edited, for the line at the foot. */
  benchLabel: string
  /** Device changes are refused mid-run, so the fields say so rather than failing on commit. */
  disabled?: boolean
}>()

const emit = defineEmits<{
  apply: [gain: { leftDb: number; rightDb: number; unit: GainUnit }]
  close: []
}>()

/**
 * Drafts rather than direct binding, for the reason the Configuration dialog's fields already
 * carry: the status poll replaces the backing numbers about once a second, so a bound input is
 * overwritten mid-type — "-2" on its way to "-20" becomes 0 before it can be committed.
 */
const leftDraft = ref('')
const rightDraft = ref('')
const leftInput = ref<HTMLInputElement | null>(null)

/** Reseeded from the props whenever they change, which includes the unit being switched. */
watch(
  () => [props.leftDb, props.rightDb, props.unit].join('|'),
  () => {
    leftDraft.value = show(props.leftDb)
    rightDraft.value = show(props.rightDb)
  },
  { immediate: true },
)

// Opened by a press, so the first box takes the caret: the whole point of the panel is to type in
// it, and reaching for the mouse again to do that is a step the press already expressed.
void nextTick(() => leftInput.value?.select())

function show(db: number): string {
  return String(Number(toDisplay(db, props.unit).toFixed(displayPrecision(props.unit))))
}

/**
 * A field the user did not touch keeps its stored dB **exactly**, rather than being re-derived from
 * what the box shows.
 *
 * <p>The box shows a rounded figure — four decimals of a ratio — so `fromDisplay(show(db))` is not
 * `db`. Under the ratio unit a stored −6 dB displays as ×0.5012 and comes back as −5.99978, which
 * is nothing physically and still wrong in two ways that matter: opening the panel and pressing
 * Enter would alter the stored value, and toggling the unit a few times would walk it. Comparing
 * the draft against what the prop renders is what makes an untouched field a no-op, and it has to
 * be a string comparison in the display unit rather than a numeric one in dB, since that is the
 * only place the two are expressed the same way.</p>
 */
function commit() {
  emit('apply', {
    leftDb: leftDraft.value === show(props.leftDb) ? props.leftDb : fromDisplay(Number(leftDraft.value), props.unit),
    rightDb: rightDraft.value === show(props.rightDb) ? props.rightDb : fromDisplay(Number(rightDraft.value), props.unit),
    unit: props.unit,
  })
}

/**
 * Switching the unit commits the <i>stored</i> dB unchanged and only changes how it is read — so a
 * −20 dB divider shown as ×0.1 is still the same divider. Reading the drafts here instead would
 * reinterpret a half-typed number in the new unit.
 */
function changeUnit(event: Event) {
  emit('apply', {
    leftDb: props.leftDb,
    rightDb: props.rightDb,
    unit: (event.target as HTMLSelectElement).value as GainUnit,
  })
}
</script>

<template>
  <div
    class="flex w-72 flex-col gap-2 rounded border border-neutral-300 bg-[var(--surface-ground)] p-3 text-xs shadow-lg dark:border-neutral-700"
    role="dialog"
    aria-label="External gain"
    @keydown.esc.stop="emit('close')"
  >
    <div class="flex items-center justify-between">
      <span class="font-medium text-neutral-700 dark:text-neutral-200">External gain</span>
      <button
        type="button"
        class="flex h-5 w-5 items-center justify-center rounded text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
        aria-label="Close"
        @click="emit('close')"
      >
        ×
      </button>
    </div>

    <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      What sits between the DUT and each input — a 10:1 divider is −20 dB, or ×0.1. Every level
      shown is then the DUT's, with this undone.
    </p>

    <div class="flex items-center gap-2">
      <label class="flex items-center gap-1.5">
        <span class="w-3 uppercase tracking-wide text-neutral-500">L</span>
        <input
          ref="leftInput"
          type="number"
          class="w-20 rounded border border-neutral-300 px-1.5 py-0.5 font-mono text-xs disabled:opacity-50 dark:border-neutral-700"
          :step="unit === 'Times' ? 0.01 : 0.1"
          :disabled="disabled"
          v-model="leftDraft"
          @change="commit"
          @keyup.enter="commit"
        />
      </label>
      <label class="flex items-center gap-1.5">
        <span class="w-3 uppercase tracking-wide text-neutral-500">R</span>
        <input
          type="number"
          class="w-20 rounded border border-neutral-300 px-1.5 py-0.5 font-mono text-xs disabled:opacity-50 dark:border-neutral-700"
          :step="unit === 'Times' ? 0.01 : 0.1"
          :disabled="disabled"
          v-model="rightDraft"
          @change="commit"
          @keyup.enter="commit"
        />
      </label>
      <select
        class="rounded border border-neutral-300 bg-transparent px-1 py-0.5 text-xs disabled:opacity-50 dark:border-neutral-700"
        :value="unit"
        :disabled="disabled"
        title="How the two boxes are read. The stored value is dB either way."
        @change="changeUnit"
      >
        <option value="Db">dB</option>
        <option value="Times">×</option>
      </select>
    </div>

    <!-- Which bench this is, because the figures are per analyzer and switching swaps them: a
         number edited here does not follow you to the other one. -->
    <p class="text-[11px] text-neutral-400 dark:text-neutral-500">
      Stored with <strong>{{ benchLabel }}</strong>.
    </p>
    <p v-if="disabled" class="text-[11px] text-amber-700 dark:text-amber-400">
      Not while a measurement is running.
    </p>
  </div>
</template>
